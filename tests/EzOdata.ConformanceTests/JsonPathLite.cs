using System.Globalization;
using System.Text.Json;

namespace EzOdata.ConformanceTests;

/// <summary>
/// The tiny JSON-path subset the conformance YAML uses:
///   $.value, $.value[0].name, $['@odata.count'], $.value.length()
/// </summary>
public static class JsonPathLite
{
    public static (bool Found, JsonElement Element, int? Length) Evaluate(JsonElement root, string path)
    {
        if (!path.StartsWith("$", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Path must start with $: {path}");
        }

        var current = root;
        var rest = path.Substring(1);

        while (rest.Length > 0)
        {
            if (rest.StartsWith("['", StringComparison.Ordinal))
            {
                var end = rest.IndexOf("']", StringComparison.Ordinal);
                var name = rest.Substring(2, end - 2);
                rest = rest.Substring(end + 2);
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    return (false, default, null);
                }
            }
            else if (rest.StartsWith("[", StringComparison.Ordinal))
            {
                var end = rest.IndexOf(']');
                var index = int.Parse(rest.Substring(1, end - 1), CultureInfo.InvariantCulture);
                rest = rest.Substring(end + 1);
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    return (false, default, null);
                }

                current = current[index];
            }
            else if (rest.StartsWith(".", StringComparison.Ordinal))
            {
                rest = rest.Substring(1);
                if (rest.StartsWith("length()", StringComparison.Ordinal))
                {
                    return current.ValueKind == JsonValueKind.Array
                        ? (true, current, current.GetArrayLength())
                        : (false, default, null);
                }

                var end = rest.IndexOfAny(['.', '[']);
                var name = end < 0 ? rest : rest.Substring(0, end);
                rest = end < 0 ? "" : rest.Substring(end);
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    return (false, default, null);
                }
            }
            else
            {
                throw new ArgumentException($"Unsupported path syntax near '{rest}'.");
            }
        }

        return (true, current, null);
    }
}
