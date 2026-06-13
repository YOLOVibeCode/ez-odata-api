namespace EzOdata.Connectors.Abstractions;

/// <summary>
/// Decrypted connection details for a target database (spec 04 §3).
/// Never serialized to API responses, logs, or audit events.
/// </summary>
public sealed record ConnectionSpec
{
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Database { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public TlsSpec Tls { get; init; } = new();

    /// <summary>SQLite only.</summary>
    public string? FilePath { get; init; }
    public bool ReadOnlyFile { get; init; }

    /// <summary>Whitelisted provider keywords only — validated per connector (spec 04 §3).</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>();
}

public sealed record TlsSpec
{
    /// <summary>"disable" | "prefer" | "require".</summary>
    public string Mode { get; init; } = "prefer";

    public string? CaCertPem { get; init; }
    public bool AllowInvalid { get; init; }
}
