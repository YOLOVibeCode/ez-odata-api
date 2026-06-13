using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Query;

namespace EzOdata.Connectors.Sqlite;

/// <summary>SQLite syntax (spec 04 §7.2). RETURNING requires SQLite 3.35+ (bundled e_sqlite3 qualifies).</summary>
public sealed class SqliteDialect : ISqlDialect
{
    public bool CaseInsensitiveLike => true; // ASCII LIKE is case-insensitive by default

    public ReturningMode Returning => ReturningMode.ReturningSuffix;

    public string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string Paginate(string sql, int? limit, int? offset)
    {
        if (limit is { } l) sql += $" LIMIT {l}";
        else if (offset is > 0) sql += " LIMIT -1"; // SQLite requires LIMIT before OFFSET
        if (offset is > 0) sql += $" OFFSET {offset}";
        return sql;
    }

    public string MapFunction(FilterFunction function, IReadOnlyList<string> args) => function switch
    {
        FilterFunction.Contains or FilterFunction.StartsWith or FilterFunction.EndsWith =>
            $"{args[0]} LIKE {args[1]} ESCAPE '\\'",
        FilterFunction.ToLower => $"lower({args[0]})",
        FilterFunction.ToUpper => $"upper({args[0]})",
        FilterFunction.Trim => $"trim({args[0]})",
        FilterFunction.Length => $"length({args[0]})",
        FilterFunction.IndexOf => $"(instr({args[0]}, {args[1]}) - 1)",
        FilterFunction.Substring when args.Count == 2 => $"substr({args[0]}, {args[1]} + 1)",
        FilterFunction.Substring => $"substr({args[0]}, {args[1]} + 1, {args[2]})",
        FilterFunction.Concat => $"({string.Join(" || ", args)})",
        FilterFunction.Year => $"CAST(strftime('%Y', {args[0]}) AS INTEGER)",
        FilterFunction.Month => $"CAST(strftime('%m', {args[0]}) AS INTEGER)",
        FilterFunction.Day => $"CAST(strftime('%d', {args[0]}) AS INTEGER)",
        FilterFunction.Hour => $"CAST(strftime('%H', {args[0]}) AS INTEGER)",
        FilterFunction.Minute => $"CAST(strftime('%M', {args[0]}) AS INTEGER)",
        FilterFunction.Second => $"CAST(strftime('%S', {args[0]}) AS INTEGER)",
        FilterFunction.Date => $"date({args[0]})",
        FilterFunction.Time => $"time({args[0]})",
        FilterFunction.Now => "datetime('now')",
        FilterFunction.Round => $"round({args[0]})",
        // floor/ceiling emulated: the math extension is not guaranteed (spec 04 §7.2)
        FilterFunction.Floor =>
            $"(CASE WHEN {args[0]} = CAST({args[0]} AS INTEGER) OR {args[0]} > 0 THEN CAST({args[0]} AS INTEGER) ELSE CAST({args[0]} AS INTEGER) - 1 END)",
        FilterFunction.Ceiling =>
            $"(CASE WHEN {args[0]} = CAST({args[0]} AS INTEGER) OR {args[0]} < 0 THEN CAST({args[0]} AS INTEGER) ELSE CAST({args[0]} AS INTEGER) + 1 END)",
        FilterFunction.Add => $"({args[0]} + {args[1]})",
        FilterFunction.Sub => $"({args[0]} - {args[1]})",
        FilterFunction.Mul => $"({args[0]} * {args[1]})",
        FilterFunction.Div => $"({args[0]} / {args[1]})",
        FilterFunction.Mod => $"({args[0]} % {args[1]})",
        _ => throw new NotSupportedQueryException($"Function '{function}' is not supported on SQLite."),
    };
}
