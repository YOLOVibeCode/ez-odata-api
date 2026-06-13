using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Query;

namespace EzOdata.Connectors.PostgreSql;

/// <summary>PostgreSQL syntax (spec 04 §7.2 translation column).</summary>
public sealed class PostgreSqlDialect : ISqlDialect
{
    public bool CaseInsensitiveLike => true; // ILIKE per spec 04 §7.2

    public ReturningMode Returning => ReturningMode.ReturningSuffix;

    public string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string Paginate(string sql, int? limit, int? offset)
    {
        if (limit is { } l) sql += $" LIMIT {l}";
        if (offset is > 0) sql += $" OFFSET {offset}";
        return sql;
    }

    public string MapFunction(FilterFunction function, IReadOnlyList<string> args) => function switch
    {
        FilterFunction.Contains or FilterFunction.StartsWith or FilterFunction.EndsWith =>
            $"{args[0]} ILIKE {args[1]} ESCAPE '\\'",
        FilterFunction.ToLower => $"lower({args[0]})",
        FilterFunction.ToUpper => $"upper({args[0]})",
        FilterFunction.Trim => $"trim({args[0]})",
        FilterFunction.Length => $"length({args[0]})",
        FilterFunction.IndexOf => $"(position({args[1]} in {args[0]}) - 1)",
        FilterFunction.Substring when args.Count == 2 => $"substr({args[0]}, {args[1]} + 1)",
        FilterFunction.Substring => $"substr({args[0]}, {args[1]} + 1, {args[2]})",
        FilterFunction.Concat => $"({string.Join(" || ", args)})",
        FilterFunction.Year => $"EXTRACT(YEAR FROM {args[0]})::int",
        FilterFunction.Month => $"EXTRACT(MONTH FROM {args[0]})::int",
        FilterFunction.Day => $"EXTRACT(DAY FROM {args[0]})::int",
        FilterFunction.Hour => $"EXTRACT(HOUR FROM {args[0]})::int",
        FilterFunction.Minute => $"EXTRACT(MINUTE FROM {args[0]})::int",
        FilterFunction.Second => $"EXTRACT(SECOND FROM {args[0]})::int",
        FilterFunction.Date => $"({args[0]})::date",
        FilterFunction.Time => $"({args[0]})::time",
        FilterFunction.Now => "now()",
        FilterFunction.Round => $"round({args[0]})",
        FilterFunction.Floor => $"floor({args[0]})",
        FilterFunction.Ceiling => $"ceiling({args[0]})",
        FilterFunction.Add => $"({args[0]} + {args[1]})",
        FilterFunction.Sub => $"({args[0]} - {args[1]})",
        FilterFunction.Mul => $"({args[0]} * {args[1]})",
        FilterFunction.Div => $"({args[0]} / {args[1]})",
        FilterFunction.Mod => $"({args[0]} % {args[1]})",
        _ => throw new NotSupportedQueryException($"Function '{function}' is not supported on PostgreSQL."),
    };
}
