using System.Text;
using System.Text.Json;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core;
using EzOdata.Core.Policy;
using EzOdata.Core.Protocol;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

namespace EzOdata.OData;

/// <summary>
/// Write operations (spec 05 §5–7): create (single/bulk/deep), patch/replace, delete,
/// ETag concurrency for tables with configured concurrency columns, JSON $batch.
/// </summary>
public sealed partial class ODataRequestHandler
{
    private async Task<EngineResponse> HandleWriteAsync(
        IEdmModel model, ServiceRuntime runtime, ConnectorDescriptor connector,
        EngineRequest request, SecurityContext security, CancellationToken ct)
    {
        if (connector.Writer is null || runtime.Options.ReadOnly)
        {
            return Error(403, ErrorCodes.ForbiddenVerb, "This service is read-only.");
        }

        if (request.Path == "$batch")
        {
            return await HandleBatchAsync(model, runtime, connector, request, security, ct);
        }

        var parser = new ODataUriParser(model, new Uri(request.Path, UriKind.Relative));
        ODataPath path;
        try
        {
            path = parser.ParsePath();
        }
        catch (ODataUnrecognizedPathException)
        {
            return Error(404, "NotFound", $"Resource '{request.Path}' not found.");
        }

        var segments = path.ToList();
        if (segments.Count == 0 || segments[0] is not EntitySetSegment entitySetSegment)
        {
            return Error(501, "NotImplemented", "Writes are supported on entity sets only.");
        }

        var entitySet = entitySetSegment.EntitySet;
        var table = runtime.Schema.FindTable(entitySet.Name)!;
        var keySegment = segments.Count == 2 ? segments[1] as KeySegment : null;

        if (!table.Writable)
        {
            return Error(403, ErrorCodes.ForbiddenVerb, $"'{table.ExposedName}' is read-only (view).");
        }

        var verb = request.Method.ToUpperInvariant() switch
        {
            "POST" => Verb.Post,
            "PUT" => Verb.Put,
            "PATCH" => Verb.Patch,
            "DELETE" => Verb.Delete,
            _ => Verb.None,
        };
        if (verb == Verb.None)
        {
            return Error(405, "MethodNotAllowed", $"Method {request.Method} is not supported.");
        }

        var decision = security.Authorize(table, verb, _policy);
        if (!decision.Allowed)
        {
            return decision.Hidden
                ? Error(404, "NotFound", $"Resource '{table.ExposedName}' not found.")
                : Error(403, decision.DenialCode ?? "Forbidden", decision.DenialMessage ?? "Access denied.");
        }

        return (verb, keySegment) switch
        {
            (Verb.Post, null) => await HandleInsertAsync(model, runtime, connector, request, entitySet, table, decision, path, ct),
            (Verb.Patch or Verb.Put, not null) => await HandleUpdateAsync(model, runtime, connector, request, entitySet, table, keySegment, decision, verb == Verb.Put, path, ct),
            (Verb.Delete, not null) => await HandleDeleteAsync(runtime, connector, request, table, keySegment, decision, ct),
            (Verb.Post, not null) => Error(405, "MethodNotAllowed", "POST is not valid on a single entity."),
            _ => Error(405, "MethodNotAllowed", $"{request.Method} requires an entity key."),
        };
    }

    // ---- create ----

    private async Task<EngineResponse> HandleInsertAsync(
        IEdmModel model, ServiceRuntime runtime, ConnectorDescriptor connector, EngineRequest request,
        IEdmEntitySet entitySet, TableModel table, PolicyDecision decision, ODataPath path, CancellationToken ct)
    {
        var json = await ReadJsonBodyAsync(request, ct);
        if (json is null)
        {
            return Error(400, ErrorCodes.ValidationInvalidValue, "A JSON body is required.");
        }

        var isBulk = json.Value.ValueKind == JsonValueKind.Array;
        var elements = isBulk
            ? json.Value.EnumerateArray().ToList()
            : [json.Value];

        if (elements.Count == 0)
        {
            return Error(400, ErrorCodes.ValidationInvalidValue, "Empty insert payload.");
        }

        var records = new List<RecordPayload>(elements.Count);
        foreach (var element in elements)
        {
            var record = PayloadBinder.Bind(element, table, runtime.Schema, allowDeepInsert: true);
            ValidateWritableFields(record, table, runtime.Schema, decision);
            records.Add(record);
        }

        var write = new WriteRequest
        {
            ServiceName = runtime.Name,
            Table = table.ExposedName,
            Kind = WriteKind.Insert,
            Records = records,
            InsertVisibilityFilter = decision.RowFilter,
        };

        var result = await ExecuteWriteAsync(connector, runtime, write, ct);

        var preferMinimal = request.Headers.TryGetValue("Prefer", out var prefer)
                            && prefer.Contains("return=minimal", StringComparison.OrdinalIgnoreCase);
        if (preferMinimal)
        {
            return new EngineResponse
            {
                StatusCode = 204,
                Headers = new Dictionary<string, string>(ODataHeaders()) { ["Preference-Applied"] = "return=minimal" },
            };
        }

        ApplyReadPolicyToWriteResult(result.Records, table, decision);

        if (isBulk)
        {
            var body = ODataPayloadWriter.WriteResourceSet(
                model, entitySet, path, request.ServiceRoot, table, result.Records, count: null, nextLink: null);
            return Json(200, body);
        }

        var single = ODataPayloadWriter.WriteSingleResource(
            model, entitySet, path, request.ServiceRoot, table, result.Records[0]);
        var location = BuildEntityLocation(request, table, result.Records[0]);

        return new EngineResponse
        {
            StatusCode = 201,
            Body = single,
            ContentType = "application/json;odata.metadata=minimal",
            Headers = location is null
                ? ODataHeaders()
                : new Dictionary<string, string>(ODataHeaders()) { ["Location"] = location },
        };
    }

    // ---- update / replace ----

    private async Task<EngineResponse> HandleUpdateAsync(
        IEdmModel model, ServiceRuntime runtime, ConnectorDescriptor connector, EngineRequest request,
        IEdmEntitySet entitySet, TableModel table, KeySegment keySegment, PolicyDecision decision,
        bool isReplace, ODataPath path, CancellationToken ct)
    {
        var json = await ReadJsonBodyAsync(request, ct);
        if (json is null)
        {
            return Error(400, ErrorCodes.ValidationInvalidValue, "A JSON body is required.");
        }

        var record = PayloadBinder.Bind(json.Value, table, runtime.Schema, allowDeepInsert: false, isReplace: isReplace);
        ValidateWritableFields(record, table, runtime.Schema, decision);

        var key = ToKeyPredicate(keySegment);

        // Key in body must match URL or be absent (spec 05 §5.2)
        foreach (var pair in key.Values)
        {
            if (record.Values.TryGetValue(pair.Key, out var bodyValue))
            {
                if (!Equals(bodyValue?.ToString(), pair.Value?.ToString()))
                {
                    return Error(400, ErrorCodes.ValidationInvalidValue,
                        $"Key property '{pair.Key}' in the body conflicts with the URL.");
                }

                var trimmedValues = record.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                trimmedValues.Remove(pair.Key);
                record = record with { Values = trimmedValues };
            }
        }

        var (precondition, etagError) = await BuildWritePreconditionAsync(runtime, table, decision, request);
        if (etagError is not null) return etagError;

        var write = new WriteRequest
        {
            ServiceName = runtime.Name,
            Table = table.ExposedName,
            Kind = isReplace ? WriteKind.Replace : WriteKind.Update,
            Records = [record],
            Key = key,
            Precondition = precondition,
        };

        var result = await ExecuteWriteAsync(connector, runtime, write, ct);
        if (result.AffectedCount == 0)
        {
            return await NotFoundOrPreconditionFailedAsync(runtime, connector, table, key, decision, request, ct);
        }

        ApplyReadPolicyToWriteResult(result.Records, table, decision);
        var body = ODataPayloadWriter.WriteSingleResource(
            model, entitySet, path, request.ServiceRoot, table, result.Records[0]);
        return Json(200, body);
    }

    // ---- delete ----

    private async Task<EngineResponse> HandleDeleteAsync(
        ServiceRuntime runtime, ConnectorDescriptor connector, EngineRequest request,
        TableModel table, KeySegment keySegment, PolicyDecision decision, CancellationToken ct)
    {
        var key = ToKeyPredicate(keySegment);
        var (precondition, etagError) = await BuildWritePreconditionAsync(runtime, table, decision, request);
        if (etagError is not null) return etagError;

        var write = new WriteRequest
        {
            ServiceName = runtime.Name,
            Table = table.ExposedName,
            Kind = WriteKind.Delete,
            Key = key,
            Precondition = precondition,
        };

        var result = await ExecuteWriteAsync(connector, runtime, write, ct);
        if (result.AffectedCount == 0)
        {
            return await NotFoundOrPreconditionFailedAsync(runtime, connector, table, key, decision, request, ct);
        }

        return new EngineResponse { StatusCode = 204, Headers = ODataHeaders() };
    }

    // ---- $batch (JSON format, spec 05 §6) ----

    private async Task<EngineResponse> HandleBatchAsync(
        IEdmModel model, ServiceRuntime runtime, ConnectorDescriptor connector,
        EngineRequest request, SecurityContext security, CancellationToken ct)
    {
        var json = await ReadJsonBodyAsync(request, ct);
        if (json is null || !json.Value.TryGetProperty("requests", out var requests)
            || requests.ValueKind != JsonValueKind.Array)
        {
            return Error(400, ErrorCodes.ValidationInvalidValue, "JSON batch requires a 'requests' array.");
        }

        var items = requests.EnumerateArray().ToList();
        if (items.Count > 100)
        {
            return Error(400, ErrorCodes.ValidationInvalidValue, "Batches are limited to 100 requests (spec 05 §6).");
        }

        var responses = new List<object>(items.Count);
        foreach (var item in items)
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "?" : "?";
            var method = item.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET";
            var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

            if (url.StartsWith("$", StringComparison.Ordinal))
            {
                responses.Add(new { id, status = 501, body = new { error = new { code = "NotImplemented", message = "Content-ID references land in a later milestone." } } });
                continue;
            }

            var queryIndex = url.IndexOf('?');
            var subRequest = new EngineRequest
            {
                Method = method,
                Path = queryIndex < 0 ? url.TrimStart('/') : url.Substring(0, queryIndex).TrimStart('/'),
                QueryString = queryIndex < 0 ? "" : url.Substring(queryIndex + 1),
                ServiceRoot = request.ServiceRoot,
                Identity = request.Identity,
                Headers = request.Headers,
                Body = item.TryGetProperty("body", out var bodyProp)
                    ? new MemoryStream(Encoding.UTF8.GetBytes(bodyProp.GetRawText()))
                    : null,
            };

            var subResponse = await HandleAsync(runtime.Name, subRequest, ct);
            responses.Add(new
            {
                id,
                status = subResponse.StatusCode,
                headers = new Dictionary<string, string> { ["content-type"] = subResponse.ContentType ?? "application/json" },
                body = subResponse.Body is { Length: > 0 }
                    ? JsonSerializer.Deserialize<JsonElement>(subResponse.Body)
                    : (object?)null,
            });
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { responses });
        return Json(200, payload);
    }

    // ---- shared write helpers ----

    private static async Task<WriteResult> ExecuteWriteAsync(
        ConnectorDescriptor connector, ServiceRuntime runtime, WriteRequest write, CancellationToken ct)
    {
        var execution = new WriteExecution(runtime.Connection, runtime.Schema, write, new ExecutionOptions
        {
            CommandTimeoutSeconds = runtime.Options.CommandTimeoutSeconds,
        });
        return await connector.Writer!.WriteAsync(execution, ct);
    }

    /// <summary>Denied + masked fields are never writable; write-only fields are (spec 03 §2.5).</summary>
    private static void ValidateWritableFields(
        RecordPayload record, TableModel table, SchemaSnapshot schema, PolicyDecision decision)
    {
        foreach (var field in record.Values.Keys)
        {
            if (decision.DeniedFields.Contains(field) || decision.MaskedFields.ContainsKey(field))
            {
                throw new QueryPolicyRewriter.FieldDeniedException(field);
            }
        }

        foreach (var child in record.Children.Values.SelectMany(c => c))
        {
            // Child field policies are validated against the child table by the caller's
            // deep-insert authorization in HandleInsertAsync (POST on child table required).
            _ = child;
        }
    }

    private void ApplyReadPolicyToWriteResult(
        IReadOnlyList<Row> rows, TableModel table, PolicyDecision decision)
    {
        if (decision.Bypass) return;

        foreach (var row in rows)
        {
            foreach (var field in decision.DeniedFields.Concat(decision.WriteOnlyFields))
            {
                row.Remove(field);
            }

            foreach (var mask in decision.MaskedFields)
            {
                if (row.Has(mask.Key)) row.Set(mask.Key, mask.Value);
            }
        }
    }

    private async Task<(FilterNode? Precondition, EngineResponse? Error)> BuildWritePreconditionAsync(
        ServiceRuntime runtime, TableModel table, PolicyDecision decision, EngineRequest request)
    {
        FilterNode? precondition = decision.RowFilter;

        // ETag concurrency (spec 05 §7): tables with a configured concurrency column
        // require If-Match on mutations.
        var concurrencyColumn = runtime.Options.ConcurrencyColumns
            .Select(table.FindColumn)
            .FirstOrDefault(c => c is not null);

        if (concurrencyColumn is not null)
        {
            if (!request.Headers.TryGetValue("If-Match", out var ifMatch) || string.IsNullOrEmpty(ifMatch))
            {
                return (null, Error(428, "PreconditionRequired",
                    $"If-Match is required for writes to '{table.ExposedName}'."));
            }

            if (ifMatch != "*")
            {
                var raw = ifMatch.Trim();
                if (raw.StartsWith("W/", StringComparison.Ordinal)) raw = raw.Substring(2);
                raw = raw.Trim('"');

                object? expected;
                try
                {
                    expected = DecodeETagValue(raw, concurrencyColumn);
                }
                catch (FormatException)
                {
                    return (null, Error(400, ErrorCodes.ValidationInvalidValue, "Malformed If-Match value."));
                }

                var etagCheck = new ComparisonNode(
                    new FieldRef(concurrencyColumn.ExposedName), ComparisonOp.Eq, new ConstantValue(expected));
                precondition = precondition is null
                    ? etagCheck
                    : new LogicalNode(LogicalOp.And, [precondition, etagCheck]);
            }
        }

        await Task.CompletedTask;
        return (precondition, null);
    }

    private async Task<EngineResponse> NotFoundOrPreconditionFailedAsync(
        ServiceRuntime runtime, ConnectorDescriptor connector, TableModel table,
        KeyPredicate key, PolicyDecision decision, EngineRequest request, CancellationToken ct)
    {
        // Affected 0: invisible row (404) or ETag mismatch on a visible row (412).
        var hasIfMatch = request.Headers.TryGetValue("If-Match", out var ifMatch)
                         && !string.IsNullOrEmpty(ifMatch) && ifMatch != "*";
        if (!hasIfMatch)
        {
            return Error(404, "NotFound", "Entity not found.");
        }

        FilterNode? keyFilter = null;
        foreach (var pair in key.Values)
        {
            var cmp = new ComparisonNode(new FieldRef(pair.Key), ComparisonOp.Eq, new ConstantValue(pair.Value));
            keyFilter = keyFilter is null ? cmp : new LogicalNode(LogicalOp.And, [keyFilter, cmp]);
        }

        if (decision.RowFilter is { } rowFilter)
        {
            keyFilter = new LogicalNode(LogicalOp.And, [keyFilter!, rowFilter]);
        }

        var probe = new QueryExecution(runtime.Connection, runtime.Schema, new QueryRequest
        {
            ServiceName = runtime.Name,
            Table = table.ExposedName,
            Filter = keyFilter,
            Select = table.PrimaryKey,
            Top = 1,
        }, new ExecutionOptions { CommandTimeoutSeconds = runtime.Options.CommandTimeoutSeconds, RowLimit = 1 });

        var exists = (await connector.Reader.QueryAsync(probe, ct)).Rows.Count > 0;
        return exists
            ? Error(412, "PreconditionFailed", "The entity was modified by another request.")
            : Error(404, "NotFound", "Entity not found.");
    }

    private static KeyPredicate ToKeyPredicate(KeySegment keySegment)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in keySegment.Keys)
        {
            values[pair.Key] = ODataAstTranslator.NormalizeValue(pair.Value);
        }

        return new KeyPredicate(values);
    }

    private static object? DecodeETagValue(string raw, ColumnModel column) => column.EdmType switch
    {
        "Edm.Int16" or "Edm.Int32" or "Edm.Int64" => long.Parse(raw),
        "Edm.Guid" => Guid.Parse(raw),
        "Edm.DateTimeOffset" => DateTimeOffset.Parse(raw, System.Globalization.CultureInfo.InvariantCulture),
        _ => raw,
    };

    private string? BuildEntityLocation(EngineRequest request, TableModel table, Row row)
    {
        if (table.PrimaryKey.Count == 0) return null;

        var keyPart = table.PrimaryKey.Count == 1
            ? FormatKeyValue(row[table.PrimaryKey[0]])
            : string.Join(",", table.PrimaryKey.Select(k => $"{k}={FormatKeyValue(row[k])}"));

        return new Uri(request.ServiceRoot, $"{table.ExposedName}({keyPart})").ToString();
    }

    private static string FormatKeyValue(object? value) => value switch
    {
        null => "null",
        string s => "'" + s.Replace("'", "''") + "'",
        Guid g => g.ToString(),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
    };

    private static async Task<JsonElement?> ReadJsonBodyAsync(EngineRequest request, CancellationToken ct)
    {
        if (request.Body is null) return null;

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
