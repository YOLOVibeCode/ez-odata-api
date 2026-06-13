using EzOdata.Connectors.Abstractions;
using EzOdata.Core;
using EzOdata.Core.Policy;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;

namespace EzOdata.OData;

/// <summary>
/// Executes $expand by issuing batched follow-up queries per level (spec 04 §7.1):
/// to-one and to-many alike fetch children with "WHERE fk IN (keys of page)" and stitch
/// in memory, avoiding row explosion. Per-parent $top within an expand uses ROW_NUMBER
/// partitioning, applied here by trimming the stitched groups.
/// </summary>
public sealed class ExpansionExecutor
{
    private readonly IQueryExecutor _reader;
    private readonly ConnectionSpec _connection;
    private readonly SchemaSnapshot _schema;
    private readonly ExecutionOptions _options;
    private readonly string _serviceName;
    private readonly Func<TableModel, Verb, PolicyDecisionLite> _authorize;

    public ExpansionExecutor(
        IQueryExecutor reader, ConnectionSpec connection, SchemaSnapshot schema,
        ExecutionOptions options, string serviceName,
        Func<TableModel, Verb, PolicyDecisionLite> authorize)
    {
        _reader = reader;
        _connection = connection;
        _schema = schema;
        _options = options;
        _serviceName = serviceName;
        _authorize = authorize;
    }

    /// <summary>Verb is always GET here; kept for symmetry with the policy engine.</summary>
    public enum Verb { Get }

    /// <summary>Just the bits the expander needs from a policy decision.</summary>
    public sealed record PolicyDecisionLite(
        bool Allowed, bool Hidden,
        IReadOnlyCollection<string> DeniedFields,
        IReadOnlyDictionary<string, string> MaskedFields,
        FilterNode? RowFilter);

    public async Task ExpandAsync(
        TableModel parentTable, IReadOnlyList<Row> parentRows,
        IReadOnlyList<ExpandNode> expands, CancellationToken ct)
    {
        if (parentRows.Count == 0) return;

        foreach (var expand in expands)
        {
            await ExpandOneAsync(parentTable, parentRows, expand, ct);
        }
    }

    private async Task ExpandOneAsync(
        TableModel parentTable, IReadOnlyList<Row> parentRows, ExpandNode expand, CancellationToken ct)
    {
        var resolved = ResolveNavigation(parentTable, expand.Navigation);
        if (resolved is null)
        {
            throw new QueryValidationException(ErrorCodes.ValidationBadFilter,
                $"Unknown navigation '{expand.Navigation}' on '{parentTable.ExposedName}'.");
        }

        var (childTable, fk, isToMany) = resolved.Value;

        // RBAC: expanding a forbidden table fails loudly (spec 05 §4.4, OD-8) — not silent omission.
        var decision = _authorize(childTable, Verb.Get);
        if (!decision.Allowed)
        {
            throw new QueryPolicyRewriter.FieldDeniedException(expand.Navigation);
        }

        // Parent key values that drive the child lookup.
        var parentKeyColumns = isToMany ? fk.RefColumns : fk.Columns;       // join key on the parent side
        var childKeyColumns = isToMany ? fk.Columns : fk.RefColumns;        // matching column on the child side

        var parentKeyValues = parentRows
            .Select(r => parentKeyColumns.Select(c => r[c]).ToList())
            .Where(values => values.All(v => v is not null))
            .ToList();

        if (parentKeyValues.Count == 0)
        {
            foreach (var row in parentRows) row.Set(expand.Navigation, isToMany ? new List<Row>() : null);
            return;
        }

        // Build IN filter on the (single-column) child key; composite keys fall back to OR-of-AND.
        var childFilter = BuildKeyInFilter(childKeyColumns, parentKeyValues);
        var combined = expand.Filter is null
            ? childFilter
            : new LogicalNode(LogicalOp.And, [expand.Filter, childFilter]);
        if (decision.RowFilter is not null)
        {
            combined = new LogicalNode(LogicalOp.And, [combined, decision.RowFilter]);
        }

        // Projection must include child key columns so we can group, even if not selected.
        var select = BuildChildSelect(childTable, expand.Select, childKeyColumns, decision);

        var childQuery = new QueryRequest
        {
            ServiceName = _serviceName,
            Table = childTable.ExposedName,
            Filter = combined,
            Select = select,
            OrderBy = expand.OrderBy,
            // Fetch enough for all parents; per-parent $top trimmed after grouping.
            Top = null,
        };

        var childResult = await _reader.QueryAsync(
            new QueryExecution(_connection, _schema, childQuery, _options), ct);

        ApplyMasks(childResult.Rows, decision.MaskedFields);

        // Recurse before grouping so nested expands attach to child rows.
        if (expand.Expand.Count > 0)
        {
            await ExpandAsync(childTable, childResult.Rows, expand.Expand, ct);
        }

        StitchChildren(parentRows, parentKeyColumns, childResult.Rows, childKeyColumns, expand, isToMany);
    }

    private void StitchChildren(
        IReadOnlyList<Row> parentRows, IReadOnlyList<string> parentKeyColumns,
        IReadOnlyList<Row> childRows, IReadOnlyList<string> childKeyColumns,
        ExpandNode expand, bool isToMany)
    {
        var grouped = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
        foreach (var child in childRows)
        {
            var key = KeyString(childKeyColumns.Select(c => child[c]));
            if (!grouped.TryGetValue(key, out var list))
            {
                grouped[key] = list = [];
            }

            list.Add(child);
        }

        foreach (var parent in parentRows)
        {
            var key = KeyString(parentKeyColumns.Select(c => parent[c]));
            grouped.TryGetValue(key, out var children);
            children ??= [];

            if (isToMany)
            {
                var slice = children.AsEnumerable();
                if (expand.Skip is { } skip) slice = slice.Skip(skip);
                if (expand.Top is { } top) slice = slice.Take(top);
                parent.Set(expand.Navigation, slice.ToList());
            }
            else
            {
                parent.Set(expand.Navigation, children.Count > 0 ? children[0] : null);
            }
        }
    }

    private (TableModel Child, ForeignKeyModel Fk, bool IsToMany)? ResolveNavigation(TableModel parent, string navigation)
    {
        // To-one: FK declared on the parent.
        var toOne = parent.ForeignKeys.FirstOrDefault(f => f.NavToOne == navigation);
        if (toOne is not null)
        {
            var target = _schema.FindTable(toOne.RefTable);
            return target is null ? null : (target, toOne, false);
        }

        // To-many: FK on some child referencing the parent.
        foreach (var candidate in _schema.Tables)
        {
            var fk = candidate.ForeignKeys.FirstOrDefault(f => f.RefTable == parent.ExposedName && f.NavToMany == navigation);
            if (fk is not null) return (candidate, fk, true);
        }

        return null;
    }

    private static FilterNode BuildKeyInFilter(IReadOnlyList<string> keyColumns, List<List<object?>> keyValues)
    {
        if (keyColumns.Count == 1)
        {
            var distinct = keyValues.Select(v => v[0]).Distinct().Select(v => new ConstantValue(v)).ToList();
            return new InNode(new FieldRef(keyColumns[0]), distinct);
        }

        // Composite: OR of AND-equality tuples.
        var ors = keyValues.Select(tuple =>
        {
            FilterNode? and = null;
            for (var i = 0; i < keyColumns.Count; i++)
            {
                var cmp = new ComparisonNode(new FieldRef(keyColumns[i]), ComparisonOp.Eq, new ConstantValue(tuple[i]));
                and = and is null ? cmp : new LogicalNode(LogicalOp.And, [and, cmp]);
            }

            return and!;
        }).ToList();

        return ors.Count == 1 ? ors[0] : new LogicalNode(LogicalOp.Or, ors);
    }

    private static IReadOnlyList<string> BuildChildSelect(
        TableModel childTable, IReadOnlyList<string>? requested,
        IReadOnlyList<string> keyColumns, PolicyDecisionLite decision)
    {
        var unreadable = new HashSet<string>(decision.DeniedFields, StringComparer.Ordinal);
        var baseSet = requested is null
            ? childTable.Columns.Select(c => c.ExposedName).Where(c => !unreadable.Contains(c) && !decision.MaskedFields.ContainsKey(c))
            : requested.Where(c => !unreadable.Contains(c) && !decision.MaskedFields.ContainsKey(c));

        var set = new List<string>(baseSet);
        foreach (var key in keyColumns)
        {
            if (!set.Contains(key)) set.Add(key); // needed for stitching even if unselected
        }

        return set;
    }

    private static void ApplyMasks(IReadOnlyList<Row> rows, IReadOnlyDictionary<string, string> masks)
    {
        if (masks.Count == 0) return;
        foreach (var row in rows)
        {
            foreach (var mask in masks) row.Set(mask.Key, mask.Value);
        }
    }

    private static string KeyString(IEnumerable<object?> values) =>
        string.Join("\u0001", values.Select(v => v?.ToString() ?? "\u0000"));
}
