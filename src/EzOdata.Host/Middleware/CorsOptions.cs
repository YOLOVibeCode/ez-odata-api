namespace EzOdata.Host.Middleware;

/// <summary>spec 08 §7 / spec 12 §4 — instance-level CORS defaults.</summary>
public sealed class CorsOptions
{
    public const string SectionPath = "Cors";

    /// <summary>
    /// Origins allowed when the request presents no API key — notably browser preflight,
    /// which never carries custom headers (spec 08 §7).
    /// </summary>
    public string[] DefaultAllowedOrigins { get; set; } = [];
}
