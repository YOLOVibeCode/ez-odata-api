using System.Text.RegularExpressions;

namespace EzOdata.Core.Services;

/// <summary>
/// Service name rules (spec 03 §2.1): URL slug, immutable after creation.
/// </summary>
public static class ServiceName
{
    public const int MaxLength = 63;

    private static readonly Regex Pattern = new("^[a-z][a-z0-9_-]{1,62}$", RegexOptions.Compiled);

    public static bool IsValid(string? name) => name is not null && Pattern.IsMatch(name);
}
