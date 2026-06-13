using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

namespace EzOdata.OData;

/// <summary>
/// ODL-direct serialization (spec 02 §5.4): rows stream out as ODataResource instances
/// via ODataMessageWriter; no CLR entity materialization.
/// </summary>
public static class ODataPayloadWriter
{
    /// <summary>Resolves a navigation name to its target table for nested expansion writing.</summary>
    public delegate TableModel? NavigationResolver(TableModel from, string navigation, out bool isCollection);

    public static byte[] WriteResourceSet(
        IEdmModel model, IEdmEntitySet entitySet, ODataPath path, Uri serviceRoot,
        TableModel table, IReadOnlyList<Row> rows, long? count, Uri? nextLink,
        NavigationResolver? navResolver = null)
    {
        using var stream = new MemoryStream();
        var message = new InMemoryResponseMessage(stream);
        var settings = WriterSettings(serviceRoot, path);

        using var writer = new ODataMessageWriter(message, settings, model);
        var setWriter = writer.CreateODataResourceSetWriter(entitySet, entitySet.EntityType());

        var resourceSet = new ODataResourceSet();
        if (count is { } c) resourceSet.Count = c;
        if (nextLink is not null) resourceSet.NextPageLink = nextLink;

        setWriter.WriteStart(resourceSet);
        foreach (var row in rows)
        {
            WriteResourceRecursive(setWriter, table, row, navResolver);
        }

        setWriter.WriteEnd();
        setWriter.Flush();
        return stream.ToArray();
    }

    public static byte[] WriteSingleResource(
        IEdmModel model, IEdmEntitySet entitySet, ODataPath path, Uri serviceRoot,
        TableModel table, Row row, NavigationResolver? navResolver = null)
    {
        using var stream = new MemoryStream();
        var message = new InMemoryResponseMessage(stream);
        var settings = WriterSettings(serviceRoot, path);

        using var writer = new ODataMessageWriter(message, settings, model);
        var resourceWriter = writer.CreateODataResourceWriter(entitySet, entitySet.EntityType());
        WriteResourceRecursive(resourceWriter, table, row, navResolver);
        resourceWriter.Flush();
        return stream.ToArray();
    }

    private static void WriteResourceRecursive(
        ODataWriter writer, TableModel table, Row row, NavigationResolver? navResolver)
    {
        writer.WriteStart(ToResource(table, row));

        if (navResolver is not null)
        {
            foreach (var pair in row.Values)
            {
                if (pair.Value is Row childRow)
                {
                    var target = navResolver(table, pair.Key, out _);
                    if (target is null) continue;
                    writer.WriteStart(new ODataNestedResourceInfo { Name = pair.Key, IsCollection = false });
                    WriteResourceRecursive(writer, target, childRow, navResolver);
                    writer.WriteEnd();
                }
                else if (pair.Value is IReadOnlyList<Row> children)
                {
                    var target = navResolver(table, pair.Key, out _);
                    if (target is null) continue;
                    writer.WriteStart(new ODataNestedResourceInfo { Name = pair.Key, IsCollection = true });
                    writer.WriteStart(new ODataResourceSet());
                    foreach (var child in children)
                    {
                        WriteResourceRecursive(writer, target, child, navResolver);
                    }

                    writer.WriteEnd();
                    writer.WriteEnd();
                }
            }
        }

        writer.WriteEnd();
    }

    public static byte[] WriteError(string code, string message, int statusCode)
    {
        using var stream = new MemoryStream();
        var responseMessage = new InMemoryResponseMessage(stream) { StatusCode = statusCode };
        var settings = new ODataMessageWriterSettings { EnableMessageStreamDisposal = false };

        using var writer = new ODataMessageWriter(responseMessage, settings);
        writer.WriteError(new ODataError { ErrorCode = code, Message = message }, includeDebugInformation: false);
        return stream.ToArray();
    }

    private static ODataMessageWriterSettings WriterSettings(Uri serviceRoot, ODataPath path) => new()
    {
        BaseUri = serviceRoot,
        ODataUri = new ODataUri { ServiceRoot = serviceRoot, Path = path },
        EnableMessageStreamDisposal = false,
        Version = ODataVersion.V4,
    };

    private static ODataResource ToResource(TableModel table, Row row)
    {
        var properties = new List<ODataProperty>();
        foreach (var pair in row.Values)
        {
            // Skip expanded navigations; those are written as nested resources.
            if (pair.Value is Row || pair.Value is IReadOnlyList<Row>) continue;

            var column = table.FindColumn(pair.Key);
            if (column is null) continue; // not a structural column (defensive)

            properties.Add(new ODataProperty
            {
                Name = pair.Key,
                Value = ToODataValue(pair.Value, column),
            });
        }

        return new ODataResource { Properties = properties };
    }

    private static object? ToODataValue(object? value, ColumnModel? column)
    {
        if (value is null) return null;

        // Arrays (e.g. PostgreSQL text[]) must be wrapped as ODataCollectionValue.
        if (column?.EdmType.StartsWith("Collection(", StringComparison.Ordinal) == true)
        {
            var elementType = column.EdmType.Substring("Collection(".Length).TrimEnd(')');
            var items = value is System.Collections.IEnumerable enumerable and not string
                ? enumerable.Cast<object?>().ToList()
                : [value];
            return new ODataCollectionValue { TypeName = column.EdmType, Items = items };
        }

        // Provider CLR values → EDM-compatible values per the column's declared type.
        // Engines disagree on CLR types for a given column (notably SQLite, whose type
        // affinity returns DECIMAL as double/string), so we normalize to the declared type.
        return column?.EdmType switch
        {
            "Edm.Int16" => Convert.ToInt16(value),
            "Edm.Int32" => Convert.ToInt32(value),
            "Edm.Int64" => Convert.ToInt64(value),
            "Edm.Decimal" => Convert.ToDecimal(value),
            "Edm.Double" => Convert.ToDouble(value),
            "Edm.Single" => Convert.ToSingle(value),
            "Edm.Boolean" => value is bool b ? b : Convert.ToInt64(value) != 0,
            "Edm.DateTimeOffset" => ToDateTimeOffset(value),
            "Edm.Date" => ToDate(value),
            "Edm.TimeOfDay" => ToTimeOfDay(value),
            "Edm.Guid" => value is Guid g ? g : Guid.Parse(value.ToString()!),
            "Edm.String" => value as string ?? value.ToString(),
            "Edm.Untyped" => new ODataUntypedValue { RawValue = value as string ?? value.ToString() },
            _ => value,
        };
    }

    private static DateTimeOffset ToDateTimeOffset(object value)
    {
        // System.DateOnly/TimeOnly are net6+ types; this assembly is netstandard2.0, so they
        // arrive only at runtime and are matched by type name (read reflectively).
        switch (value)
        {
            case DateTimeOffset dto: return dto;
            case DateTime dt: return new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt);
            case string s: return DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
        }

        if (TryReadDateOnly(value, out var y, out var mo, out var d))
        {
            return new DateTimeOffset(y, mo, d, 0, 0, 0, TimeSpan.Zero);
        }

        return Convert.ToDateTime(value);
    }

    private static Microsoft.OData.Edm.Date ToDate(object value)
    {
        switch (value)
        {
            case DateTime dt: return new Microsoft.OData.Edm.Date(dt.Year, dt.Month, dt.Day);
            case string s when DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed):
                return new Microsoft.OData.Edm.Date(parsed.Year, parsed.Month, parsed.Day);
        }

        if (TryReadDateOnly(value, out var y, out var mo, out var d))
        {
            return new Microsoft.OData.Edm.Date(y, mo, d);
        }

        var fallback = Convert.ToDateTime(value);
        return new Microsoft.OData.Edm.Date(fallback.Year, fallback.Month, fallback.Day);
    }

    private static Microsoft.OData.Edm.TimeOfDay ToTimeOfDay(object value)
    {
        TimeSpan ts;
        switch (value)
        {
            case TimeSpan span: ts = span; break;
            case DateTime dt: ts = dt.TimeOfDay; break;
            case string s: ts = TimeSpan.Parse(s, System.Globalization.CultureInfo.InvariantCulture); break;
            default:
                ts = TryReadTimeOnly(value, out var h, out var mi, out var sec) ? new TimeSpan(h, mi, sec) : TimeSpan.Zero;
                break;
        }

        return new Microsoft.OData.Edm.TimeOfDay(ts.Hours, ts.Minutes, ts.Seconds, 0);
    }

    private static bool TryReadDateOnly(object value, out int year, out int month, out int day)
    {
        year = month = day = 0;
        var type = value.GetType();
        if (type.Name != "DateOnly") return false;
        year = (int)type.GetProperty("Year")!.GetValue(value)!;
        month = (int)type.GetProperty("Month")!.GetValue(value)!;
        day = (int)type.GetProperty("Day")!.GetValue(value)!;
        return true;
    }

    private static bool TryReadTimeOnly(object value, out int hour, out int minute, out int second)
    {
        hour = minute = second = 0;
        var type = value.GetType();
        if (type.Name != "TimeOnly") return false;
        hour = (int)type.GetProperty("Hour")!.GetValue(value)!;
        minute = (int)type.GetProperty("Minute")!.GetValue(value)!;
        second = (int)type.GetProperty("Second")!.GetValue(value)!;
        return true;
    }

}

/// <summary>Minimal IODataResponseMessage over a MemoryStream, shared by the engine.</summary>
internal sealed class InMemoryResponseMessage : IODataResponseMessage
{
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Content-Type"] = "application/json;odata.metadata=minimal",
    };

    private readonly Stream _stream;

    public InMemoryResponseMessage(Stream stream) => _stream = stream;

    public IEnumerable<KeyValuePair<string, string>> Headers => _headers;

    public int StatusCode { get; set; } = 200;

    public string GetHeader(string headerName) =>
        _headers.TryGetValue(headerName, out var value) ? value : null!;

    public void SetHeader(string headerName, string headerValue) => _headers[headerName] = headerValue;

    public Stream GetStream() => _stream;
}
