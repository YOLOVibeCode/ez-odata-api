namespace EzOdata.Core.Query;

public enum WriteKind { Insert, Update, Replace, Delete }

/// <summary>One record's writable values, already validated/coerced (spec 02 §6).</summary>
public sealed record RecordPayload
{
    /// <summary>Column → typed CLR value (null = explicit SQL NULL).</summary>
    public IReadOnlyDictionary<string, object?> Values { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Deep insert (spec 05 §5.1): to-many navigation → child records, one level deep.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RecordPayload>> Children { get; init; } =
        new Dictionary<string, IReadOnlyList<RecordPayload>>(StringComparer.Ordinal);
}

/// <summary>Key column → value for single-record operations.</summary>
public sealed record KeyPredicate(IReadOnlyDictionary<string, object?> Values);

public sealed record WriteRequest
{
    public required string ServiceName { get; init; }
    public required string Table { get; init; }
    public required WriteKind Kind { get; init; }
    public IReadOnlyList<RecordPayload> Records { get; init; } = [];
    public KeyPredicate? Key { get; init; }

    /// <summary>Row filter and/or concurrency predicate AND-ed into UPDATE/DELETE (spec 08 §5.4).</summary>
    public FilterNode? Precondition { get; init; }

    /// <summary>Insert-time row filter check (spec 08 §5.4): inserted rows must satisfy it or roll back.</summary>
    public FilterNode? InsertVisibilityFilter { get; init; }
}

public sealed record WriteResult(
    int AffectedCount,
    IReadOnlyList<Row> Records,
    string? ErrorCode = null,
    string? ErrorDetail = null)
{
    public bool Succeeded => ErrorCode is null;
}

/// <summary>Connector error taxonomy (spec 04 §8): provider exceptions map to these.</summary>
public sealed class ConnectorException : Exception
{
    public ConnectorException(string errorCode, string safeMessage, bool isTransient = false, Exception? inner = null)
        : base(safeMessage, inner)
    {
        ErrorCode = errorCode;
        IsTransient = isTransient;
    }

    public string ErrorCode { get; }
    public bool IsTransient { get; }
}
