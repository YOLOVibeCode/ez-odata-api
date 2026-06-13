using EzOdata.Core.Query;
using EzOdata.Core.Schema;

namespace EzOdata.Core.Policy;

/// <summary>
/// Applies an allow decision to a QueryRequest (spec 08 §4 steps 6–8): security is
/// enforced by construction in the query — a restricted field can never appear in
/// SELECT/WHERE/ORDER BY, and the row filter is always AND-ed.
/// </summary>
public static class QueryPolicyRewriter
{
    public sealed class FieldDeniedException : Exception
    {
        public FieldDeniedException(string field)
            : base($"Access to field '{field}' is denied.") => Field = field;

        public string Field { get; }
    }

    public static QueryRequest Rewrite(QueryRequest query, PolicyDecision decision, TableModel table)
    {
        if (decision.Bypass) return query;

        var unreadable = new HashSet<string>(decision.DeniedFields, StringComparer.Ordinal);
        unreadable.UnionWith(decision.WriteOnlyFields);

        // Masked fields are not filterable/sortable (spec 03 §2.5); they are also removed
        // from the SQL projection — the mask literal is injected at serialization.
        var unfilterable = new HashSet<string>(unreadable, StringComparer.Ordinal);
        unfilterable.UnionWith(decision.MaskedFields.Keys);

        // 1. Validate client filter and orderby never touch restricted fields (explicit 403).
        if (query.Filter is not null)
        {
            foreach (var field in FilterFieldWalker.RootFields(query.Filter))
            {
                if (unfilterable.Contains(field)) throw new FieldDeniedException(field);
            }
        }

        foreach (var item in query.OrderBy)
        {
            if (unfilterable.Contains(item.Field)) throw new FieldDeniedException(item.Field);
        }

        // 2. Projection: explicit $select of a denied field → 403; masked fields are
        //    allowed in $select but stripped from SQL. Implicit select = all readable.
        IReadOnlyList<string>? select;
        if (query.Select is null)
        {
            select = table.Columns
                .Select(c => c.ExposedName)
                .Where(c => !unreadable.Contains(c) && !decision.MaskedFields.ContainsKey(c))
                .ToList();
        }
        else
        {
            foreach (var field in query.Select)
            {
                if (unreadable.Contains(field)) throw new FieldDeniedException(field);
            }

            select = query.Select.Where(f => !decision.MaskedFields.ContainsKey(f)).ToList();
            if (select.Count == 0)
            {
                // Everything requested is masked; still need the PK for row identity.
                select = table.PrimaryKey.Count > 0 ? table.PrimaryKey : [table.Columns[0].ExposedName];
            }
        }

        // 3. Row filter AND-ed into the client filter (never OR, never replaced).
        var filter = (query.Filter, decision.RowFilter) switch
        {
            (null, null) => null,
            (var f, null) => f,
            (null, var rf) => rf,
            var (f, rf) => new LogicalNode(LogicalOp.And, [f!, rf!]),
        };

        return query with { Select = select, Filter = filter };
    }

    /// <summary>Fields the serializer must add back as mask literals for a given request.</summary>
    public static IReadOnlyDictionary<string, string> MasksToApply(
        QueryRequest originalQuery, PolicyDecision decision)
    {
        if (decision.MaskedFields.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return originalQuery.Select is null
            ? decision.MaskedFields
            : decision.MaskedFields
                .Where(m => originalQuery.Select.Contains(m.Key, StringComparer.Ordinal))
                .ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
    }
}

/// <summary>Collects root-table field references from a filter tree.</summary>
public static class FilterFieldWalker
{
    public static IEnumerable<string> RootFields(FilterNode node)
    {
        switch (node)
        {
            case ComparisonNode cmp:
                if (!cmp.Field.IsNavigated) yield return cmp.Field.Leaf;
                break;
            case InNode inNode:
                if (!inNode.Field.IsNavigated) yield return inNode.Field.Leaf;
                break;
            case FunctionNode fn:
                foreach (var arg in fn.Args.OfType<FieldArg>())
                {
                    if (!arg.Field.IsNavigated) yield return arg.Field.Leaf;
                }

                break;
            case LogicalNode logical:
                foreach (var operand in logical.Operands)
                foreach (var field in RootFields(operand))
                {
                    yield return field;
                }

                break;
            case NotNode not:
                foreach (var field in RootFields(not.Operand)) yield return field;
                break;
        }
    }

    /// <summary>Navigations referenced via paths or lambdas (used for cross-table checks).</summary>
    public static IEnumerable<string> Navigations(FilterNode node)
    {
        switch (node)
        {
            case ComparisonNode { Field.IsNavigated: true } cmp:
                yield return cmp.Field.Path[0];
                break;
            case LambdaNode lambda:
                yield return lambda.Navigation;
                break;
            case LogicalNode logical:
                foreach (var operand in logical.Operands)
                foreach (var nav in Navigations(operand))
                {
                    yield return nav;
                }

                break;
            case NotNode not:
                foreach (var nav in Navigations(not.Operand)) yield return nav;
                break;
            case FunctionNode fn:
                foreach (var arg in fn.Args.OfType<FieldArg>().Where(a => a.Field.IsNavigated))
                {
                    yield return arg.Field.Path[0];
                }

                break;
        }
    }
}
