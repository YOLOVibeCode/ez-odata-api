namespace EzOdata.Core.Query;

/// <summary>
/// Engine-agnostic result rows: ordered field/value maps, already shaped by projection.
/// Expanded navigations appear as nested Row lists (to-many) or Rows (to-one).
/// </summary>
public sealed record QueryResult(IReadOnlyList<Row> Rows, bool HasMore, KeysetCursor? NextCursor);

public sealed class Row
{
    private readonly Dictionary<string, object?> _values;

    public Row(IEnumerable<KeyValuePair<string, object?>> values)
    {
        _values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in values) _values[pair.Key] = pair.Value;
    }

    public IReadOnlyDictionary<string, object?> Values => _values;

    public object? this[string field] => _values.TryGetValue(field, out var v) ? v : null;

    public bool Has(string field) => _values.ContainsKey(field);

    public void Set(string field, object? value) => _values[field] = value;

    public bool Remove(string field) => _values.Remove(field);
}
