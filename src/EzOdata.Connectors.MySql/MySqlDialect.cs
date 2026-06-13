using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Query;

namespace EzOdata.Connectors.MySql;

/// <summary>MySQL/MariaDB syntax (spec 04 §7.2).</summary>
public sealed class MySqlDialect : ISqlDialect
{
    public bool CaseInsensitiveLike => true; // default *_ci collations

    public ReturningMode Returning => ReturningMode.None; // LAST_INSERT_ID strategy

    public string QuoteIdentifier(string identifier) =>
        "`" + identifier.Replace("`", "``") + "`";

    public string Paginate(string sql, int? limit, int? offset)
    {
        if (limit is { } l) sql += $" LIMIT {l}";
        else if (offset is > 0) sql += " LIMIT 18446744073709551615"; // MySQL requires LIMIT with OFFSET
        if (offset is > 0) sql += $" OFFSET {offset}";
        return sql;
    }

    public string MapFunction(FilterFunction function, IReadOnlyList<string> args) => function switch
    {
        FilterFunction.Contains or FilterFunction.StartsWith or FilterFunction.EndsWith =>
            $"{args[0]} LIKE {args[1]} ESCAPE '\\\\'",
        FilterFunction.ToLower => $"LOWER({args[0]})",
        FilterFunction.ToUpper => $"UPPER({args[0]})",
        FilterFunction.Trim => $"TRIM({args[0]})",
        FilterFunction.Length => $"CHAR_LENGTH({args[0]})",
        FilterFunction.IndexOf => $"(LOCATE({args[1]}, {args[0]}) - 1)",
        FilterFunction.Substring when args.Count == 2 => $"SUBSTRING({args[0]}, {args[1]} + 1)",
        FilterFunction.Substring => $"SUBSTRING({args[0]}, {args[1]} + 1, {args[2]})",
        FilterFunction.Concat => $"CONCAT({string.Join(", ", args)})",
        FilterFunction.Year => $"YEAR({args[0]})",
        FilterFunction.Month => $"MONTH({args[0]})",
        FilterFunction.Day => $"DAY({args[0]})",
        FilterFunction.Hour => $"HOUR({args[0]})",
        FilterFunction.Minute => $"MINUTE({args[0]})",
        FilterFunction.Second => $"SECOND({args[0]})",
        FilterFunction.Date => $"DATE({args[0]})",
        FilterFunction.Time => $"TIME({args[0]})",
        FilterFunction.Now => "NOW()",
        FilterFunction.Round => $"ROUND({args[0]})",
        FilterFunction.Floor => $"FLOOR({args[0]})",
        FilterFunction.Ceiling => $"CEILING({args[0]})",
        FilterFunction.Add => $"({args[0]} + {args[1]})",
        FilterFunction.Sub => $"({args[0]} - {args[1]})",
        FilterFunction.Mul => $"({args[0]} * {args[1]})",
        FilterFunction.Div => $"({args[0]} / {args[1]})",
        FilterFunction.Mod => $"({args[0]} % {args[1]})",
        _ => throw new NotSupportedQueryException($"Function '{function}' is not supported on MySQL."),
    };
}
