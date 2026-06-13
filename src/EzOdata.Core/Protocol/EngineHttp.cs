namespace EzOdata.Core.Protocol;

/// <summary>
/// Host-agnostic HTTP contract (spec 02 §5.2): adapters translate ASP.NET Core or
/// classic Web API requests into this shape; protocol engines never see a host framework.
/// </summary>
public sealed record EngineRequest
{
    public required string Method { get; init; }

    /// <summary>Path relative to the service root, e.g. "customers(42)" or "$metadata". No leading slash.</summary>
    public required string Path { get; init; }

    /// <summary>Raw query string without the leading '?', or "".</summary>
    public string QueryString { get; init; } = "";

    /// <summary>Absolute external service root for link generation, proxy-aware.</summary>
    public required Uri ServiceRoot { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public Stream? Body { get; init; }

    /// <summary>Authenticated principal, built by the host adapter (spec 08 §2).</summary>
    public Policy.RequestIdentity Identity { get; init; } = Policy.RequestIdentity.Anonymous;
}

public sealed record EngineResponse
{
    public int StatusCode { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public byte[]? Body { get; init; }
    public string? ContentType { get; init; }

    public static EngineResponse Empty(int statusCode) => new() { StatusCode = statusCode };
}
