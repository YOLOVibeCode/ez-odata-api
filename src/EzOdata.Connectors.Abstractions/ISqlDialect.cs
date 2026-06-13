using EzOdata.Core.Query;

namespace EzOdata.Connectors.Abstractions;

/// <summary>How generated keys/rows come back from INSERT/UPDATE (spec 04 §7.3).</summary>
public enum ReturningMode
{
    /// <summary>PostgreSQL/SQLite: "... RETURNING cols" suffix.</summary>
    ReturningSuffix,

    /// <summary>SQL Server: "OUTPUT INSERTED.col" clause before VALUES/WHERE.</summary>
    OutputClause,

    /// <summary>MySQL: no returning — executor follows up with LAST_INSERT_ID()/keyed SELECT.</summary>
    None,
}

/// <summary>
/// Engine-specific SQL differences (spec 04 §2, §7). The shared <see cref="Sql.SqlCompiler"/>
/// owns structure and parameterization; dialects own syntax.
/// </summary>
public interface ISqlDialect
{
    string QuoteIdentifier(string identifier);

    /// <summary>Appends LIMIT/OFFSET (PG/MySQL/SQLite) or OFFSET..FETCH (MSSQL).</summary>
    string Paginate(string sql, int? limit, int? offset);

    /// <summary>
    /// Renders one filter function call, e.g. contains → ILIKE/LIKE.
    /// Args arrive as already-rendered SQL fragments (quoted identifiers or parameter markers).
    /// </summary>
    string MapFunction(FilterFunction function, IReadOnlyList<string> args);

    /// <summary>True when string LIKE comparisons are case-insensitive for this service (spec 04 §7.2).</summary>
    bool CaseInsensitiveLike { get; }

    ReturningMode Returning { get; }
}
