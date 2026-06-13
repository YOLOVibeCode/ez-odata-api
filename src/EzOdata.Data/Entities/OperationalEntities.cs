namespace EzOdata.Data.Entities;

/// <summary>spec 03 §2.10.</summary>
public class RateLimitPolicyEntity
{
    public long Id { get; set; }

    /// <summary>"app" | "role" | "user" | "service" | "instance".</summary>
    public required string ScopeType { get; set; }

    public long? ScopeId { get; set; }
    public int WindowSeconds { get; set; }
    public int MaxRequests { get; set; }

    /// <summary>Optional verb bitmask restriction; NULL = all verbs.</summary>
    public int? Verbs { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long RowVersion { get; set; }
}

/// <summary>spec 03 §2.12 — append-only; never row data or credentials.</summary>
public class AuditEventEntity
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string RequestId { get; set; }

    /// <summary>"data.read" | "data.write" | "auth" | "admin" | "mcp" | "system".</summary>
    public required string Category { get; set; }

    public required string Action { get; set; }

    /// <summary>"ok" | "denied" | "error".</summary>
    public required string Outcome { get; set; }

    public long? ServiceId { get; set; }
    public long? AppId { get; set; }
    public long? UserId { get; set; }
    public long? RoleId { get; set; }
    public string? Resource { get; set; }
    public string DetailJson { get; set; } = "{}";
    public int? DurationMs { get; set; }
}

/// <summary>spec 03 §2.13 — mutable instance settings; secrets stay in env config.</summary>
public class SystemSettingEntity
{
    public required string Key { get; set; }
    public required string ValueJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>spec 03 §2.14 — background job bookkeeping.</summary>
public class JobEntity
{
    public long Id { get; set; }

    /// <summary>"introspection" | "snapshot-prune" | "audit-prune".</summary>
    public required string Kind { get; set; }

    public long? ServiceId { get; set; }

    /// <summary>"queued" | "running" | "succeeded" | "failed".</summary>
    public required string Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
}
