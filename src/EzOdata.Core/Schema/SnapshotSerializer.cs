using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EzOdata.Core.Schema;

/// <summary>
/// Canonical JSON serialization + content hashing for snapshots (spec 04 §5.1).
/// Identical schemas always produce identical hashes, so $metadata ETags stay stable
/// across refreshes and process restarts.
/// </summary>
public static class SnapshotSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string Serialize(SchemaSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Options);

    public static SchemaSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<SchemaSnapshot>(json, Options)
        ?? throw new InvalidOperationException("Snapshot JSON deserialized to null.");

    /// <summary>
    /// SHA-256 over the canonical form: object keys sorted, tables/columns/FKs sorted by name,
    /// and the collection timestamp excluded (it is metadata about the run, not the schema).
    /// </summary>
    public static string ComputeHash(SchemaSnapshot snapshot)
    {
        var node = JsonSerializer.SerializeToNode(snapshot, Options) as JsonObject
                   ?? throw new InvalidOperationException("Snapshot did not serialize to an object.");
        node.Remove("collectedAt");

        SortTables(node);
        var canonical = Canonicalize(node);

        // netstandard2.0: no SHA256.HashData / Convert.ToHexString
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToJsonString()));
        var hex = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) hex.Append(b.ToString("x2"));
        return hex.ToString();
    }

    private static void SortTables(JsonObject root)
    {
        if (root["tables"] is not JsonArray tables) return;

        var sorted = tables
            .OfType<JsonObject>()
            .OrderBy(t => t["exposedName"]?.GetValue<string>(), StringComparer.Ordinal)
            .ToList();

        tables.Clear();
        foreach (var table in sorted)
        {
            SortByName(table, "columns", "exposedName");
            SortByName(table, "foreignKeys", "name");
            tables.Add(table.DeepClone());
        }
    }

    private static void SortByName(JsonObject table, string arrayKey, string nameKey)
    {
        if (table[arrayKey] is not JsonArray array) return;

        var sorted = array.OfType<JsonObject>()
            .OrderBy(x => x[nameKey]?.GetValue<string>(), StringComparer.Ordinal)
            .Select(x => x.DeepClone())
            .ToList();

        array.Clear();
        foreach (var item in sorted) array.Add(item);
    }

    private static JsonNode Canonicalize(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(
            obj.OrderBy(p => p.Key, StringComparer.Ordinal)
               .Select(p => new KeyValuePair<string, JsonNode?>(
                   p.Key, p.Value is null ? null : Canonicalize(p.Value)))),
        JsonArray array => new JsonArray(array.Select(x => x?.DeepClone()).Select(
            x => x is null ? null : Canonicalize(x)).ToArray()),
        _ => node.DeepClone(),
    };
}
