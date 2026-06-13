using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Query;

namespace EzOdata.Connectors.SqlServer;

/// <summary>SQL Server syntax (spec 04 §7.2). Pagination is OFFSET..FETCH (requires ORDER BY,
/// which the shared compiler always emits via PK tiebreakers).</summary>
public sealed class SqlServerDialect : ISqlDialect
{
    public bool CaseInsensitiveLike => true; // default CI collations

    public ReturningMode Returning => ReturningMode.OutputClause;

    public string QuoteIdentifier(string identifier) =>
        "[" + identifier.Replace("]", "]]") + "]";

    public string Paginate(string sql, int? limit, int? offset)
    {
        if (limit is null && offset is not > 0) return sql;

        sql += $" OFFSET {offset ?? 0} ROWS";
        if (limit is { } l) sql += $" FETCH NEXT {l} ROWS ONLY";
        return sql;
    }

    public string MapFunction(FilterFunction function, IReadOnlyList<string> args) => function switch
    {
        FilterFunction.Contains or FilterFunction.StartsWith or FilterFunction.EndsWith =>
            $"{args[0]} LIKE {args[1]} ESCAPE '\\'",
        FilterFunction.ToLower => $"LOWER({args[0]})",
        FilterFunction.ToUpper => $"UPPER({args[0]})",
        FilterFunction.Trim => $"TRIM({args[0]})",
        FilterFunction.Length => $"LEN({args[0]})",
        FilterFunction.IndexOf => $"(CHARINDEX({args[1]}, {args[0]}) - 1)",
        FilterFunction.Substring when args.Count == 2 => $"SUBSTRING({args[0]}, {args[1]} + 1, LEN({args[0]}))",
        FilterFunction.Substring => $"SUBSTRING({args[0]}, {args[1]} + 1, {args[2]})",
        FilterFunction.Concat => $"CONCAT({string.Join(", ", args)})",
        FilterFunction.Year => $"DATEPART(year, {args[0]})",
        FilterFunction.Month => $"DATEPART(month, {args[0]})",
        FilterFunction.Day => $"DATEPART(day, {args[0]})",
        FilterFunction.Hour => $"DATEPART(hour, {args[0]})",
        FilterFunction.Minute => $"DATEPART(minute, {args[0]})",
        FilterFunction.Second => $"DATEPART(second, {args[0]})",
        FilterFunction.Date => $"CAST({args[0]} AS date)",
        FilterFunction.Time => $"CAST({args[0]} AS time)",
        FilterFunction.Now => "SYSDATETIMEOFFSET()",
        FilterFunction.Round => $"ROUND({args[0]}, 0)",
        FilterFunction.Floor => $"FLOOR({args[0]})",
        FilterFunction.Ceiling => $"CEILING({args[0]})",
        FilterFunction.Add => $"({args[0]} + {args[1]})",
        FilterFunction.Sub => $"({args[0]} - {args[1]})",
        FilterFunction.Mul => $"({args[0]} * {args[1]})",
        FilterFunction.Div => $"({args[0]} / {args[1]})",
        FilterFunction.Mod => $"({args[0]} % {args[1]})",
        _ => throw new NotSupportedQueryException($"Function '{function}' is not supported on SQL Server."),
    };
}
