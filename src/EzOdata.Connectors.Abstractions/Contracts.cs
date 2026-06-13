using EzOdata.Core.Query;
using EzOdata.Core.Schema;

namespace EzOdata.Connectors.Abstractions;

/// <summary>Derived from ServiceOptions for introspection runs (spec 04 §4).</summary>
public sealed record IntrospectionOptions
{
    public IReadOnlyList<string> IncludeSchemas { get; init; } = [];
    public IReadOnlyList<string> ExcludeTables { get; init; } = [];
    public bool IncludeViews { get; init; } = true;

    /// <summary>"original" or "pascal" (spec 04 §4.5).</summary>
    public string ExposedNameStyle { get; init; } = "original";
}

/// <summary>Per-call execution settings, sourced from service options.</summary>
public sealed record ExecutionOptions
{
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>Rows fetched = limit + 1 to detect HasMore without COUNT.</summary>
    public int RowLimit { get; init; } = 25;
}

/// <summary>Everything a connector needs to run one read operation.</summary>
public sealed record QueryExecution(
    ConnectionSpec Connection,
    SchemaSnapshot Schema,
    QueryRequest Query,
    ExecutionOptions Options);

/// <summary>Segregated capability: schema discovery only (spec 04 §2).</summary>
public interface ISchemaIntrospector
{
    Task<SchemaSnapshot> IntrospectAsync(ConnectionSpec spec, IntrospectionOptions options, CancellationToken ct);
}

/// <summary>Segregated capability: reads only. The ONLY connector surface read paths see.</summary>
public interface IQueryExecutor
{
    Task<QueryResult> QueryAsync(QueryExecution execution, CancellationToken ct);

    Task<long> CountAsync(QueryExecution execution, CancellationToken ct);
}

/// <summary>Everything a connector needs to run one write operation.</summary>
public sealed record WriteExecution(
    ConnectionSpec Connection,
    SchemaSnapshot Schema,
    WriteRequest Write,
    ExecutionOptions Options);

/// <summary>
/// Segregated capability: writes only (spec 02 §3.1). Absent (null in the descriptor)
/// for read-only engines/configurations — unable to write by type, not by guard.
/// </summary>
public interface IWriteExecutor
{
    /// <summary>One write (single or bulk records); transactional per call (spec 04 §7.3).</summary>
    Task<WriteResult> WriteAsync(WriteExecution execution, CancellationToken ct);

    /// <summary>
    /// Multiple writes in ONE transaction — $batch changesets (spec 05 §6).
    /// All succeed or all roll back; results returned in order.
    /// </summary>
    Task<IReadOnlyList<WriteResult>> WriteAtomicAsync(IReadOnlyList<WriteExecution> executions, CancellationToken ct);
}

/// <summary>Raised when a request references unknown tables/fields or unsupported constructs.</summary>
public sealed class QueryValidationException : Exception
{
    public QueryValidationException(string errorCode, string message) : base(message) => ErrorCode = errorCode;

    public string ErrorCode { get; }
}

/// <summary>Raised for constructs the platform intentionally does not support (spec 05 OD-9: fail loudly).</summary>
public sealed class NotSupportedQueryException : Exception
{
    public NotSupportedQueryException(string message) : base(message) { }
}
