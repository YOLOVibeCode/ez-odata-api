namespace EzOdata.Core.Services;

/// <summary>
/// Per-service options persisted as <c>services.options_json</c> (spec 03 §2.1).
/// </summary>
public sealed record ServiceOptions
{
    public IReadOnlyList<string> IncludeSchemas { get; init; } = [];
    public IReadOnlyList<string> ExcludeTables { get; init; } = [];
    public bool IncludeViews { get; init; } = true;
    public bool ReadOnly { get; init; }
    public int DefaultPageSize { get; init; } = 25;
    public int MaxPageSize { get; init; } = 1000;
    public int CommandTimeoutSeconds { get; init; } = 30;
    public int MaxPoolSize { get; init; } = 50;

    /// <summary>"original" (default) or "pascal" (spec 04 §4.5).</summary>
    public string ExposedNameStyle { get; init; } = "original";

    /// <summary>
    /// Columns that drive ETag optimistic concurrency (spec 05 §7): a table participates
    /// when it contains one of these; writes then require If-Match.
    /// </summary>
    public IReadOnlyList<string> ConcurrencyColumns { get; init; } = [];

    /// <summary>$expand limits (spec 05 §4.4).</summary>
    public int MaxExpandDepth { get; init; } = 3;
    public int MaxExpandWidth { get; init; } = 10;

    /// <summary>$search opt-in (spec 05 §4.6); off by default.</summary>
    public bool EnableSearch { get; init; }

    public string? Error()
    {
        if (DefaultPageSize is < 1 or > 10_000) return "defaultPageSize must be between 1 and 10000";
        if (MaxPageSize < DefaultPageSize || MaxPageSize > 100_000) return "maxPageSize must be >= defaultPageSize and <= 100000";
        if (CommandTimeoutSeconds is < 1 or > 3600) return "commandTimeoutSeconds must be between 1 and 3600";
        if (MaxPoolSize is < 1 or > 1000) return "maxPoolSize must be between 1 and 1000";
        if (ExposedNameStyle is not ("original" or "pascal")) return "exposedNameStyle must be 'original' or 'pascal'";
        return null;
    }
}
