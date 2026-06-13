using System.Text;
using EzOdata.Core;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;

namespace EzOdata.Connectors.Abstractions.Sql;

public sealed record SqlParam(string Name, object? Value);

public sealed record CompiledQuery(string Sql, IReadOnlyList<SqlParam> Parameters, IReadOnlyList<ColumnModel> Projection);

/// <summary>
/// Compiles Query IR to parameterized SQL (spec 04 §7). Structure and parameterization
/// are shared; syntax differences go through <see cref="ISqlDialect"/>.
///
/// Safety invariants (NFR-3, CON-3):
/// - every identifier is resolved against the schema snapshot, then dialect-quoted;
///   client strings never reach SQL text directly
/// - every value becomes a parameter; there is no code path that renders a constant inline
/// </summary>
public sealed class SqlCompiler
{
    private readonly ISqlDialect _dialect;

    public SqlCompiler(ISqlDialect dialect) => _dialect = dialect;

    public CompiledQuery CompileSelect(SchemaSnapshot schema, QueryRequest query)
    {
        var context = new CompileContext(schema, _dialect, ResolveTable(schema, query.Table));

        var projection = ResolveProjection(context.RootTable, query.Select);
        var selectList = string.Join(", ", projection.Select(c =>
            $"t0.{_dialect.QuoteIdentifier(c.DbName)} AS {_dialect.QuoteIdentifier(c.ExposedName)}"));

        var where = query.Filter is null ? null : RenderFilter(context, query.Filter, "t0", context.RootTable);
        var orderBy = RenderOrderBy(context, query.OrderBy, projection);

        var sql = new StringBuilder();
        sql.Append("SELECT ").Append(selectList);
        sql.Append(" FROM ").Append(QualifiedTable(context.RootTable)).Append(" AS t0");
        foreach (var join in context.Joins) sql.Append(' ').Append(join);
        if (where is not null) sql.Append(" WHERE ").Append(where);
        if (orderBy.Length > 0) sql.Append(" ORDER BY ").Append(orderBy);

        // Fetch one extra row to detect HasMore without a second query.
        var paged = _dialect.Paginate(sql.ToString(), query.Top is { } top ? top + 1 : null, query.Skip);
        return new CompiledQuery(paged, context.Parameters, projection);
    }

    public CompiledQuery CompileApply(SchemaSnapshot schema, QueryRequest query)
    {
        var apply = query.Apply ?? throw new QueryValidationException(
            ErrorCodes.ValidationInvalidValue, "No $apply clause to compile.");
        var context = new CompileContext(schema, _dialect, ResolveTable(schema, query.Table));
        var table = context.RootTable;

        var projection = new List<string>();
        var resultColumns = new List<ColumnModel>();

        foreach (var groupField in apply.GroupBy)
        {
            var column = table.FindColumn(groupField)
                ?? throw new QueryValidationException(ErrorCodes.ValidationUnknownProperty,
                    $"Unknown groupby property '{groupField}'.");
            projection.Add($"t0.{_dialect.QuoteIdentifier(column.DbName)} AS {_dialect.QuoteIdentifier(column.ExposedName)}");
            resultColumns.Add(column);
        }

        foreach (var aggregate in apply.Aggregations)
        {
            projection.Add($"{RenderAggregate(table, aggregate)} AS {_dialect.QuoteIdentifier(aggregate.Alias)}");
            resultColumns.Add(new ColumnModel
            {
                DbName = aggregate.Alias,
                ExposedName = aggregate.Alias,
                DbType = "aggregate",
                EdmType = aggregate.Op is AggregateOp.Count or AggregateOp.CountDistinct ? "Edm.Int64" : "Edm.Decimal",
                Nullable = true,
            });
        }

        var where = query.Filter is null ? null : RenderFilter(context, query.Filter, "t0", table);

        var sql = new StringBuilder("SELECT ").Append(string.Join(", ", projection));
        sql.Append(" FROM ").Append(QualifiedTable(table)).Append(" AS t0");
        foreach (var join in context.Joins) sql.Append(' ').Append(join);
        if (where is not null) sql.Append(" WHERE ").Append(where);

        if (apply.GroupBy.Count > 0)
        {
            sql.Append(" GROUP BY ")
               .Append(string.Join(", ", apply.GroupBy.Select(g =>
                   $"t0.{_dialect.QuoteIdentifier(table.FindColumn(g)!.DbName)}")));
        }

        return new CompiledQuery(sql.ToString(), context.Parameters, resultColumns);
    }

    private string RenderAggregate(TableModel table, Aggregation aggregate)
    {
        if (aggregate.Op == AggregateOp.Count)
        {
            return "COUNT(*)";
        }

        var column = table.FindColumn(aggregate.Field ?? "")
            ?? throw new QueryValidationException(ErrorCodes.ValidationUnknownProperty,
                $"Unknown aggregate property '{aggregate.Field}'.");
        var quoted = $"t0.{_dialect.QuoteIdentifier(column.DbName)}";

        return aggregate.Op switch
        {
            AggregateOp.Sum => $"SUM({quoted})",
            AggregateOp.Average => $"AVG({quoted})",
            AggregateOp.Min => $"MIN({quoted})",
            AggregateOp.Max => $"MAX({quoted})",
            AggregateOp.CountDistinct => $"COUNT(DISTINCT {quoted})",
            _ => throw new NotSupportedQueryException($"Unsupported aggregate {aggregate.Op}."),
        };
    }

    public CompiledQuery CompileCount(SchemaSnapshot schema, QueryRequest query)
    {
        var context = new CompileContext(schema, _dialect, ResolveTable(schema, query.Table));
        var where = query.Filter is null ? null : RenderFilter(context, query.Filter, "t0", context.RootTable);

        var sql = new StringBuilder("SELECT COUNT(*) FROM ")
            .Append(QualifiedTable(context.RootTable)).Append(" AS t0");
        foreach (var join in context.Joins) sql.Append(' ').Append(join);
        if (where is not null) sql.Append(" WHERE ").Append(where);

        return new CompiledQuery(sql.ToString(), context.Parameters, []);
    }

    // ---- resolution ----

    private static TableModel ResolveTable(SchemaSnapshot schema, string exposedName) =>
        schema.FindTable(exposedName)
        ?? throw new QueryValidationException(ErrorCodes.ValidationInvalidValue, $"Unknown table '{exposedName}'.");

    private static IReadOnlyList<ColumnModel> ResolveProjection(TableModel table, IReadOnlyList<string>? select)
    {
        if (select is null) return table.Columns;

        var projection = new List<ColumnModel>(select.Count);
        foreach (var field in select)
        {
            projection.Add(table.FindColumn(field)
                ?? throw new QueryValidationException(ErrorCodes.ValidationUnknownProperty,
                    $"Unknown property '{field}' on '{table.ExposedName}'."));
        }

        return projection;
    }

    private string QualifiedTable(TableModel table) =>
        string.IsNullOrEmpty(table.DbSchema)
            ? _dialect.QuoteIdentifier(table.DbName)
            : $"{_dialect.QuoteIdentifier(table.DbSchema)}.{_dialect.QuoteIdentifier(table.DbName)}";

    private string RenderOrderBy(CompileContext context, IReadOnlyList<OrderByItem> orderBy, IReadOnlyList<ColumnModel> projection)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in orderBy)
        {
            var column = context.RootTable.FindColumn(item.Field)
                ?? throw new QueryValidationException(ErrorCodes.ValidationUnknownProperty,
                    $"Unknown property '{item.Field}' in $orderby.");
            parts.Add($"t0.{_dialect.QuoteIdentifier(column.DbName)}{(item.Descending ? " DESC" : "")}");
            seen.Add(column.ExposedName);
        }

        // Deterministic pagination (OD-12): PK appended as tiebreaker; keyless tables fall
        // back to all projected columns.
        var tiebreakers = context.RootTable.HasKey
            ? context.RootTable.PrimaryKey
            : projection.Select(c => c.ExposedName).ToList() as IReadOnlyList<string>;
        foreach (var key in tiebreakers)
        {
            if (seen.Contains(key)) continue;
            var column = context.RootTable.FindColumn(key);
            if (column is not null) parts.Add($"t0.{_dialect.QuoteIdentifier(column.DbName)}");
        }

        return string.Join(", ", parts);
    }

    // ---- filter rendering ----

    private string RenderFilter(CompileContext ctx, FilterNode node, string alias, TableModel table) => node switch
    {
        LogicalNode logical => "(" + string.Join(
            logical.Op == LogicalOp.And ? " AND " : " OR ",
            logical.Operands.Select(o => RenderFilter(ctx, o, alias, table))) + ")",

        NotNode not => $"NOT ({RenderFilter(ctx, not.Operand, alias, table)})",

        ComparisonNode cmp => RenderComparison(ctx, cmp, alias, table),

        InNode inNode => RenderIn(ctx, inNode, alias, table),

        FunctionNode fn => RenderFunction(ctx, fn, alias, table),

        LambdaNode lambda => RenderLambda(ctx, lambda, alias, table),

        _ => throw new NotSupportedQueryException($"Unsupported filter construct '{node.GetType().Name}'."),
    };

    private string RenderComparison(CompileContext ctx, ComparisonNode cmp, string alias, TableModel table)
    {
        var (fieldSql, column) = ResolveFieldRef(ctx, cmp.Field, alias, table);

        if (cmp.Value.Value is null)
        {
            return cmp.Op switch
            {
                ComparisonOp.Eq => $"{fieldSql} IS NULL",
                ComparisonOp.Ne => $"{fieldSql} IS NOT NULL",
                _ => throw new QueryValidationException(ErrorCodes.ValidationBadFilter,
                    "null can only be compared with eq/ne."),
            };
        }

        _ = column; // type-compat validation happens at the protocol parse layer
        return $"{fieldSql} {OpSql(cmp.Op)} {ctx.AddParameter(cmp.Value.Value)}";
    }

    private string RenderIn(CompileContext ctx, InNode node, string alias, TableModel table)
    {
        if (node.Values.Count == 0)
        {
            return "1 = 0"; // empty IN list matches nothing, by definition
        }

        var (fieldSql, _) = ResolveFieldRef(ctx, node.Field, alias, table);
        var markers = node.Values.Select(v => ctx.AddParameter(
            v.Value ?? throw new QueryValidationException(ErrorCodes.ValidationBadFilter, "null is not allowed in 'in' lists.")));
        return $"{fieldSql} IN ({string.Join(", ", markers)})";
    }

    private string RenderFunction(CompileContext ctx, FunctionNode fn, string alias, TableModel table)
    {
        var args = new List<string>(fn.Args.Count);
        foreach (var arg in fn.Args)
        {
            switch (arg)
            {
                case FieldArg fieldArg:
                    args.Add(ResolveFieldRef(ctx, fieldArg.Field, alias, table).Sql);
                    break;
                case ConstantArg constant:
                    var value = constant.Value.Value;
                    // contains/startswith/endswith get LIKE-pattern treatment with escaped wildcards
                    if (fn.Function is FilterFunction.Contains or FilterFunction.StartsWith or FilterFunction.EndsWith
                        && value is string s)
                    {
                        value = fn.Function switch
                        {
                            FilterFunction.Contains => $"%{EscapeLike(s)}%",
                            FilterFunction.StartsWith => $"{EscapeLike(s)}%",
                            _ => $"%{EscapeLike(s)}",
                        };
                    }

                    args.Add(ctx.AddParameter(value));
                    break;
                default:
                    throw new NotSupportedQueryException("Unsupported function argument kind.");
            }
        }

        var rendered = _dialect.MapFunction(fn.Function, args);

        if (fn.Op is { } op && fn.Comparand is { } comparand)
        {
            return comparand.Value is null
                ? op switch
                {
                    ComparisonOp.Eq => $"{rendered} IS NULL",
                    ComparisonOp.Ne => $"{rendered} IS NOT NULL",
                    _ => throw new QueryValidationException(ErrorCodes.ValidationBadFilter, "null can only be compared with eq/ne."),
                }
                : $"{rendered} {OpSql(op)} {ctx.AddParameter(comparand.Value)}";
        }

        return rendered;
    }

    private string RenderLambda(CompileContext ctx, LambdaNode lambda, string alias, TableModel table)
    {
        // any/all over a to-many navigation → EXISTS subquery (spec 04 §7.2)
        var fk = FindToManyNavigation(ctx.Schema, table, lambda.Navigation)
                 ?? throw new QueryValidationException(ErrorCodes.ValidationBadFilter,
                     $"Unknown collection navigation '{lambda.Navigation}' on '{table.ExposedName}'.");

        var (childTable, foreignKey) = fk;
        var childAlias = ctx.NextLambdaAlias();

        var joinCondition = string.Join(" AND ", foreignKey.Columns.Select((col, i) =>
        {
            var childColumn = childTable.FindColumn(col) ?? childTable.Columns.First(c => c.DbName == col);
            var parentColumn = table.Columns.First(c =>
                c.ExposedName == foreignKey.RefColumns[i] || c.DbName == foreignKey.RefColumns[i]);
            return $"{childAlias}.{_dialect.QuoteIdentifier(childColumn.DbName)} = {alias}.{_dialect.QuoteIdentifier(parentColumn.DbName)}";
        }));

        var predicate = lambda.Predicate is null
            ? null
            : RenderFilter(ctx, lambda.Predicate, childAlias, childTable);

        var subquery = $"SELECT 1 FROM {QualifiedTable(childTable)} AS {childAlias} WHERE {joinCondition}";

        return lambda.Kind switch
        {
            LambdaKind.Any when predicate is null => $"EXISTS ({subquery})",
            LambdaKind.Any => $"EXISTS ({subquery} AND {predicate})",
            // all(p) ≡ NOT EXISTS (child WHERE NOT p)
            LambdaKind.All when predicate is null => throw new QueryValidationException(
                ErrorCodes.ValidationBadFilter, "all() requires a predicate."),
            LambdaKind.All => $"NOT EXISTS ({subquery} AND NOT ({predicate}))",
            _ => throw new NotSupportedQueryException("Unknown lambda kind."),
        };
    }

    private (string Sql, ColumnModel Column) ResolveFieldRef(CompileContext ctx, FieldRef field, string alias, TableModel table)
    {
        if (!field.IsNavigated)
        {
            var column = table.FindColumn(field.Leaf)
                ?? throw new QueryValidationException(ErrorCodes.ValidationUnknownProperty,
                    $"Unknown property '{field.Leaf}' on '{table.ExposedName}'.");
            return ($"{alias}.{_dialect.QuoteIdentifier(column.DbName)}", column);
        }

        // To-one navigation path, depth ≤ 2 (spec 05 §4.3): customer/country
        if (field.Path.Count > 3)
        {
            throw new NotSupportedQueryException("Navigation paths in filters are limited to depth 2.");
        }

        var currentTable = table;
        var currentAlias = alias;
        for (var i = 0; i < field.Path.Count - 1; i++)
        {
            var nav = field.Path[i];
            var fk = currentTable.ForeignKeys.FirstOrDefault(f => f.NavToOne == nav)
                ?? throw new QueryValidationException(ErrorCodes.ValidationBadFilter,
                    $"Unknown navigation '{nav}' on '{currentTable.ExposedName}'.");

            var target = ctx.Schema.FindTable(fk.RefTable)
                ?? throw new QueryValidationException(ErrorCodes.ValidationInvalidValue,
                    $"Navigation '{nav}' references unknown table '{fk.RefTable}'.");

            currentAlias = ctx.EnsureJoin(currentAlias, currentTable, fk, target, QualifiedTable(target));
            currentTable = target;
        }

        var leaf = currentTable.FindColumn(field.Leaf)
            ?? throw new QueryValidationException(ErrorCodes.ValidationUnknownProperty,
                $"Unknown property '{field.Leaf}' on '{currentTable.ExposedName}'.");
        return ($"{currentAlias}.{_dialect.QuoteIdentifier(leaf.DbName)}", leaf);
    }

    private static (TableModel Child, ForeignKeyModel Fk)? FindToManyNavigation(SchemaSnapshot schema, TableModel parent, string navigation)
    {
        foreach (var candidate in schema.Tables)
        {
            foreach (var fk in candidate.ForeignKeys)
            {
                if (fk.RefTable == parent.ExposedName && fk.NavToMany == navigation)
                {
                    return (candidate, fk);
                }
            }
        }

        return null;
    }

    private static string OpSql(ComparisonOp op) => op switch
    {
        ComparisonOp.Eq => "=",
        ComparisonOp.Ne => "<>",
        ComparisonOp.Gt => ">",
        ComparisonOp.Ge => ">=",
        ComparisonOp.Lt => "<",
        ComparisonOp.Le => "<=",
        _ => throw new NotSupportedQueryException($"Unknown operator {op}."),
    };

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Per-compilation state: parameters, joins, alias allocation.</summary>
    private sealed class CompileContext
    {
        private readonly List<SqlParam> _parameters = [];
        private readonly List<string> _joins = [];
        private readonly Dictionary<string, string> _joinAliases = new(StringComparer.Ordinal);
        private readonly ISqlDialect _dialect;
        private int _lambdaCounter;

        public CompileContext(SchemaSnapshot schema, ISqlDialect dialect, TableModel rootTable)
        {
            Schema = schema;
            _dialect = dialect;
            RootTable = rootTable;
        }

        public SchemaSnapshot Schema { get; }

        public TableModel RootTable { get; }

        public IReadOnlyList<SqlParam> Parameters => _parameters;

        public IReadOnlyList<string> Joins => _joins;

        public string AddParameter(object? value)
        {
            var name = $"@p{_parameters.Count}";
            _parameters.Add(new SqlParam(name, value));
            return name;
        }

        public string NextLambdaAlias() => $"l{_lambdaCounter++}";

        public string EnsureJoin(string fromAlias, TableModel fromTable, ForeignKeyModel fk, TableModel target, string qualifiedTarget)
        {
            var key = $"{fromAlias}:{fk.Name}";
            if (_joinAliases.TryGetValue(key, out var existing)) return existing;

            var alias = $"t{_joinAliases.Count + 1}";
            _joinAliases[key] = alias;

            var condition = string.Join(" AND ", fk.Columns.Select((col, i) =>
            {
                var fromColumn = fromTable.Columns.First(c => c.ExposedName == col || c.DbName == col);
                var toColumn = target.Columns.First(c => c.ExposedName == fk.RefColumns[i] || c.DbName == fk.RefColumns[i]);
                return $"{fromAlias}.{_dialect.QuoteIdentifier(fromColumn.DbName)} = {alias}.{_dialect.QuoteIdentifier(toColumn.DbName)}";
            }));

            _joins.Add($"LEFT JOIN {qualifiedTarget} AS {alias} ON {condition}");
            return alias;
        }
    }
}
