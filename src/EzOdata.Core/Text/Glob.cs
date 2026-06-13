using System.Text.RegularExpressions;

namespace EzOdata.Core.Text;

/// <summary>Case-insensitive glob matching for table/field patterns: * and ? only (spec 03 §2.4).</summary>
public static class Glob
{
    public static bool IsMatch(string value, string pattern)
    {
        if (pattern == "*") return true;

        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
    }

    public static bool MatchesAny(string value, IReadOnlyList<string> patterns) =>
        patterns.Any(p => IsMatch(value, p));
}
