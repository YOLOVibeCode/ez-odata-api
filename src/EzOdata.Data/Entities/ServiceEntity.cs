using EzOdata.Core.Services;

namespace EzOdata.Data.Entities;

/// <summary>spec 03 §2.1 — one row per connected data source.</summary>
public class ServiceEntity
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string Label { get; set; }
    public string? Description { get; set; }
    public required string ConnectorType { get; set; }

    /// <summary>AES-GCM envelope (spec 08 §9) of the connection JSON. Never returned by APIs.</summary>
    public required string ConnectionEncrypted { get; set; }

    /// <summary>SHA-256 of normalized host+db; display-safe (no secrets).</summary>
    public required string ConnectionFingerprint { get; set; }

    /// <summary>Display-safe summary, e.g. "db.internal:5432/sales (user api_reader)".</summary>
    public required string ConnectionDisplay { get; set; }

    public required string OptionsJson { get; set; }
    public ServiceStatus Status { get; set; } = ServiceStatus.Pending;
    public string? StatusDetail { get; set; }
    public int? SchemaRefreshMinutes { get; set; }
    public bool IsDeleted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long RowVersion { get; set; }

    public List<SchemaSnapshotEntity> Snapshots { get; set; } = [];
}

/// <summary>spec 03 §2.2 — persisted introspection results.</summary>
public class SchemaSnapshotEntity
{
    public long Id { get; set; }
    public long ServiceId { get; set; }
    public ServiceEntity? Service { get; set; }
    public required string VersionHash { get; set; }
    public required string SnapshotJson { get; set; }
    public int TableCount { get; set; }
    public int ViewCount { get; set; }
    public DateTimeOffset IntrospectedAt { get; set; }
    public bool IsCurrent { get; set; }
}
