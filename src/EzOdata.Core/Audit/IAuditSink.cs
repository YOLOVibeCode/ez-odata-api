namespace EzOdata.Core.Audit;

/// <summary>
/// Write-only audit contract (ISP, spec 02 §3.1): querying is a separate, admin-only
/// concern. Implementations must never block the data path (spec 08 §8 / NFR-8).
/// </summary>
public interface IAuditSink
{
    /// <summary>Enqueue an event; drops (with a counter) rather than blocking on overflow.</summary>
    void Record(AuditEvent auditEvent);
}

/// <summary>One audit event (spec 03 §2.12). Never contains row data or credentials.</summary>
public sealed record AuditEvent
{
    public DateTimeOffset OccurredAt { get; init; }
    public required string RequestId { get; init; }

    /// <summary>"data.read" | "data.write" | "auth" | "admin" | "mcp" | "system".</summary>
    public required string Category { get; init; }

    public required string Action { get; init; }

    /// <summary>"ok" | "denied" | "error".</summary>
    public required string Outcome { get; init; }

    public long? ServiceId { get; init; }
    public long? AppId { get; init; }
    public long? UserId { get; init; }
    public long? RoleId { get; init; }
    public string? Resource { get; init; }
    public string DetailJson { get; init; } = "{}";
    public int? DurationMs { get; init; }
}
