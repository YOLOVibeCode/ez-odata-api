using System.Text;
using System.Text.Json;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core;
using EzOdata.Core.Policy;
using EzOdata.Core.Protocol;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using EzOdata.Core.Services;

namespace EzOdata.Rest;

/// <summary>
/// REST/JSON dialect engine (spec 06): a thin front-end producing the same Query IR,
/// flowing through the same policy engine and connectors as OData. Routes are relative
/// to the service root: <c>_table</c>, <c>_table/{name}</c>, <c>_table/{name}/_schema</c>,
/// <c>_table/{name}/{id}</c>.
/// </summary>
public sealed class RestRequestHandler
{
    private readonly IServiceRuntimeResolver _services;
    private readonly IConnectorRegistry _connectors;
    private readonly PolicyEngine _policy;
    private readonly IPolicySource _policySource;
    private readonly RowFilterParserFactory _rowFilterFactory;

    public RestRequestHandler(
        IServiceRuntimeResolver services, IConnectorRegistry connectors,
        PolicyEngine policy, IPolicySource policySource, RowFilterParserFactory rowFilterFactory)
    {
        _services = services;
        _connectors = connectors;
        _policy = policy;
        _policySource = policySource;
        _rowFilterFactory = rowFilterFactory;
    }

    /// <summary>Binds a row-filter parser for a service+identity (provided by the host to avoid an OData dependency).</summary>
    public delegate RowFilterParser RowFilterParserFactory(
        string serviceName, SchemaSnapshot schema, string schemaVersion, RequestIdentity identity);

    public async Task<EngineResponse> HandleAsync(string serviceName, EngineRequest request, CancellationToken ct)
    {
        var runtime = await _services.ResolveAsync(serviceName, ct);
        if (runtime is null) return Error(404, "NotFound", $"Unknown service '{serviceName}'.");
        if (runtime.Status is ServiceStatus.Disabled) return Error(503, "ServiceUnavailable", "Service is disabled.");
        if (runtime.Status is not (ServiceStatus.Active or ServiceStatus.Refreshing))
        {
            return Error(503, "ServiceUnavailable", "Service schema is not ready yet.");
        }

        if (!_connectors.TryGet(runtime.ConnectorType, out var connector))
        {
            return Error(503, "ServiceUnavailable", $"Connector '{runtime.ConnectorType}' is unavailable.");
        }

        var roleRules = await _policySource.GetRoleRulesAsync(request.Identity.RoleIds, ct);
        var rowFilterParser = _rowFilterFactory(runtime.Name, runtime.Schema, runtime.SchemaVersion, request.Identity);
        var ctx = new Context(runtime, connector, request.Identity, roleRules, rowFilterParser, _policy);

        var segments = request.Path.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

        try
        {
            // _table  → list tables
            if (segments is ["_table"])
            {
                return request.Method == "GET" ? ListTables(ctx) : MethodNotAllowed();
            }

            if (segments is ["_table", var table])
            {
                return request.Method switch
                {
                    "GET" => await QueryTableAsync(ctx, table, request, ct),
                    "POST" => await InsertAsync(ctx, table, request, ct),
                    "PATCH" => await BulkUpdateAsync(ctx, table, request, ct),
                    "DELETE" => await BulkDeleteAsync(ctx, table, request, ct),
                    _ => MethodNotAllowed(),
                };
            }

            if (segments is ["_table", var t2, "_schema"] && request.Method == "GET")
            {
                return DescribeTable(ctx, t2);
            }

            if (segments is ["_table", var t3, var id])
            {
                return request.Method switch
                {
                    "GET" => await GetByIdAsync(ctx, t3, id, request, ct),
                    "PATCH" => await UpdateByIdAsync(ctx, t3, id, request, ct),
                    "PUT" => await ReplaceByIdAsync(ctx, t3, id, request, ct),
                    "DELETE" => await DeleteByIdAsync(ctx, t3, id, request, ct),
                    _ => MethodNotAllowed(),
                };
            }

            return Error(404, "NotFound", "Unrecognized REST resource path.");
        }
        catch (QueryPolicyRewriter.FieldDeniedException ex)
        {
            return Error(403, ErrorCodes.ForbiddenFieldDenied, ex.Message);
        }
        catch (PayloadBinder.BindException ex)
        {
            return Error(400, ex.ErrorCode, ex.Message);
        }
        catch (ConnectorException ex)
        {
            return Error(StatusForConnectorError(ex.ErrorCode), ex.ErrorCode, ex.Message);
        }
        catch (QueryValidationException ex)
        {
            return Error(400, ex.ErrorCode, ex.Message);
        }
        catch (NotSupportedQueryException ex)
        {
            return Error(501, "NotImplemented", ex.Message);
        }
    }

    private sealed class Context
    {
        private readonly PolicyEngine _policy;

        public Context(
            ServiceRuntime runtime, ConnectorDescriptor connector, RequestIdentity identity,
            IReadOnlyList<RoleRuleSet> roles, RowFilterParser rowFilterParser, PolicyEngine policy)
        {
            Runtime = runtime;
            Connector = connector;
            Identity = identity;
            Roles = roles;
            RowFilterParser = rowFilterParser;
            _policy = policy;
        }

        public ServiceRuntime Runtime { get; }
        public ConnectorDescriptor Connector { get; }
        public RequestIdentity Identity { get; }
        public IReadOnlyList<RoleRuleSet> Roles { get; }
        public RowFilterParser RowFilterParser { get; }

        public PolicyDecision Authorize(TableModel table, Verb verb) =>
            _policy.Authorize(
                Identity, Roles, Runtime.Name, table.ExposedName, verb,
                table.Columns.Select(c => c.ExposedName).ToList(), RowFilterParser);
    }

    // ---- discovery ----

    private EngineResponse ListTables(Context ctx)
    {
        var visible = SnapshotTrimmer.Trim(
            ctx.Runtime.Schema, ctx.Identity, ctx.Roles, ctx.Runtime.Name, _policy, ctx.RowFilterParser);

        var tables = visible.Tables.Select(t => new
        {
            name = t.ExposedName,
            label = t.ExposedName,
            isView = t.IsView,
            writable = t.Writable && !ctx.Runtime.Options.ReadOnly,
            description = t.Comment,
        });

        return JsonOk(new { resource = tables });
    }

    private EngineResponse DescribeTable(Context ctx, string tableName)
    {
        var table = ctx.Runtime.Schema.FindTable(tableName);
        if (table is null) return Error(404, "NotFound", $"Unknown table '{tableName}'.");

        var decision = ctx.Authorize(table, Verb.Get);
        if (!decision.Allowed)
        {
            return decision.Hidden ? Error(404, "NotFound", $"Unknown table '{tableName}'.")
                                   : Error(403, "Forbidden", "Access denied.");
        }

        var fields = table.Columns
            .Where(c => !decision.DeniedFields.Contains(c.ExposedName) && !decision.WriteOnlyFields.Contains(c.ExposedName))
            .Select(c => new
            {
                name = c.ExposedName,
                type = c.EdmType,
                nullable = c.Nullable,
                pk = c.IsPrimaryKey,
                autoIncrement = c.IsAutoGenerated,
                maxLength = c.MaxLength,
                allowedValues = c.AllowedValues,
                masked = decision.MaskedFields.ContainsKey(c.ExposedName),
                description = c.Comment,
            });

        return JsonOk(new { name = table.ExposedName, isView = table.IsView, writable = table.Writable, fields });
    }

    // ---- reads ----

    private async Task<EngineResponse> QueryTableAsync(Context ctx, string tableName, EngineRequest request, CancellationToken ct)
    {
        var table = ctx.Runtime.Schema.FindTable(tableName);
        if (table is null) return Error(404, "NotFound", $"Unknown table '{tableName}'.");

        var decision = ctx.Authorize(table, Verb.Get);
        if (!decision.Allowed)
        {
            return decision.Hidden ? Error(404, "NotFound", $"Unknown table '{tableName}'.")
                                   : Error(403, decision.DenialCode ?? "Forbidden", decision.DenialMessage ?? "Access denied.");
        }

        var q = ParseQuery(request.QueryString);
        var filter = q.Filter is null ? null : RestFilterParser.Parse(q.Filter);
        if (filter is not null && !decision.Bypass)
        {
            CrossTableFilterValidator.Validate(filter, table, ctx.Runtime.Schema, t => ctx.Authorize(t, Verb.Get));
        }

        var effectiveLimit = Math.Min(q.Limit ?? ctx.Runtime.Options.DefaultPageSize, ctx.Runtime.Options.MaxPageSize);

        var query = new QueryRequest
        {
            ServiceName = ctx.Runtime.Name,
            Table = table.ExposedName,
            Filter = filter,
            OrderBy = ParseOrder(q.Order),
            Select = q.Fields,
            Top = effectiveLimit,
            Skip = q.Offset,
            Count = q.IncludeCount,
        };

        var rewritten = QueryPolicyRewriter.Rewrite(query, decision, table);
        var masks = QueryPolicyRewriter.MasksToApply(query, decision);
        var execution = new QueryExecution(ctx.Runtime.Connection, ctx.Runtime.Schema, rewritten,
            new ExecutionOptions { CommandTimeoutSeconds = ctx.Runtime.Options.CommandTimeoutSeconds, RowLimit = effectiveLimit });

        var result = await ctx.Connector.Reader.QueryAsync(execution, ct);
        ApplyMasks(result.Rows, masks);
        long? count = q.IncludeCount ? await ctx.Connector.Reader.CountAsync(execution, ct) : null;

        var meta = new Dictionary<string, object?> { ["schemaVersion"] = ctx.Runtime.SchemaVersion };
        if (count is { } c) meta["count"] = c;
        if (result.HasMore) meta["next"] = $"_table/{table.ExposedName}?offset={(q.Offset ?? 0) + result.Rows.Count}&limit={effectiveLimit}";

        return JsonOk(new { resource = result.Rows.Select(RowToDict), meta });
    }

    private async Task<EngineResponse> GetByIdAsync(Context ctx, string tableName, string id, EngineRequest request, CancellationToken ct)
    {
        var table = ctx.Runtime.Schema.FindTable(tableName);
        if (table is null) return Error(404, "NotFound", $"Unknown table '{tableName}'.");

        var decision = ctx.Authorize(table, Verb.Get);
        if (!decision.Allowed)
        {
            return decision.Hidden ? Error(404, "NotFound", $"Unknown table '{tableName}'.") : Error(403, "Forbidden", "Access denied.");
        }

        var keyFilter = BuildKeyFilter(table, id);
        var query = new QueryRequest { ServiceName = ctx.Runtime.Name, Table = table.ExposedName, Filter = keyFilter, Top = 1 };
        var rewritten = QueryPolicyRewriter.Rewrite(query, decision, table);
        var masks = QueryPolicyRewriter.MasksToApply(query, decision);

        var result = await ctx.Connector.Reader.QueryAsync(
            new QueryExecution(ctx.Runtime.Connection, ctx.Runtime.Schema, rewritten,
                new ExecutionOptions { CommandTimeoutSeconds = ctx.Runtime.Options.CommandTimeoutSeconds, RowLimit = 1 }), ct);

        if (result.Rows.Count == 0) return Error(404, "NotFound", "Record not found.");
        ApplyMasks(result.Rows, masks);
        return JsonOk(RowToDict(result.Rows[0]));
    }

    // ---- writes ----

    private async Task<EngineResponse> InsertAsync(Context ctx, string tableName, EngineRequest request, CancellationToken ct)
    {
        var (table, decision, error) = await AuthorizeWriteAsync(ctx, tableName, Verb.Post);
        if (error is not null) return error;

        var json = await ReadJsonAsync(request, ct);
        if (json is null) return Error(400, ErrorCodes.ValidationInvalidValue, "A JSON body is required.");

        var (elements, isBulk) = ExtractRecords(json.Value);
        var records = new List<RecordPayload>();
        foreach (var element in elements)
        {
            var record = PayloadBinder.Bind(element, table!, ctx.Runtime.Schema, allowDeepInsert: false);
            ValidateWritable(record, decision!);
            records.Add(record);
        }

        var write = new WriteRequest
        {
            ServiceName = ctx.Runtime.Name, Table = table!.ExposedName, Kind = WriteKind.Insert,
            Records = records, InsertVisibilityFilter = decision!.RowFilter,
        };
        var result = await ctx.Connector.Writer!.WriteAsync(
            new WriteExecution(ctx.Runtime.Connection, ctx.Runtime.Schema, write,
                new ExecutionOptions { CommandTimeoutSeconds = ctx.Runtime.Options.CommandTimeoutSeconds }), ct);

        StripWrite(result.Records, decision);
        return isBulk
            ? Created(new { resource = result.Records.Select(RowToDict) })
            : Created(RowToDict(result.Records[0]));
    }

    private async Task<EngineResponse> UpdateByIdAsync(Context ctx, string tableName, string id, EngineRequest request, CancellationToken ct) =>
        await UpdateByIdInternalAsync(ctx, tableName, id, request, replace: false, ct);

    private async Task<EngineResponse> ReplaceByIdAsync(Context ctx, string tableName, string id, EngineRequest request, CancellationToken ct) =>
        await UpdateByIdInternalAsync(ctx, tableName, id, request, replace: true, ct);

    private async Task<EngineResponse> UpdateByIdInternalAsync(
        Context ctx, string tableName, string id, EngineRequest request, bool replace, CancellationToken ct)
    {
        var verb = replace ? Verb.Put : Verb.Patch;
        var (table, decision, error) = await AuthorizeWriteAsync(ctx, tableName, verb);
        if (error is not null) return error;

        var json = await ReadJsonAsync(request, ct);
        if (json is null) return Error(400, ErrorCodes.ValidationInvalidValue, "A JSON body is required.");

        var record = PayloadBinder.Bind(json.Value, table!, ctx.Runtime.Schema, allowDeepInsert: false, isReplace: replace);
        ValidateWritable(record, decision!);

        var write = new WriteRequest
        {
            ServiceName = ctx.Runtime.Name, Table = table!.ExposedName,
            Kind = replace ? WriteKind.Replace : WriteKind.Update,
            Records = [record], Key = BuildKeyPredicate(table, id), Precondition = decision!.RowFilter,
        };
        var result = await ctx.Connector.Writer!.WriteAsync(
            new WriteExecution(ctx.Runtime.Connection, ctx.Runtime.Schema, write,
                new ExecutionOptions { CommandTimeoutSeconds = ctx.Runtime.Options.CommandTimeoutSeconds }), ct);

        if (result.AffectedCount == 0) return Error(404, "NotFound", "Record not found.");
        StripWrite(result.Records, decision);
        return result.Records.Count > 0 ? JsonOk(RowToDict(result.Records[0])) : new EngineResponse { StatusCode = 204 };
    }

    private async Task<EngineResponse> DeleteByIdAsync(Context ctx, string tableName, string id, EngineRequest request, CancellationToken ct)
    {
        var (table, decision, error) = await AuthorizeWriteAsync(ctx, tableName, Verb.Delete);
        if (error is not null) return error;

        var write = new WriteRequest
        {
            ServiceName = ctx.Runtime.Name, Table = table!.ExposedName, Kind = WriteKind.Delete,
            Key = BuildKeyPredicate(table, id), Precondition = decision!.RowFilter,
        };
        var result = await ctx.Connector.Writer!.WriteAsync(
            new WriteExecution(ctx.Runtime.Connection, ctx.Runtime.Schema, write,
                new ExecutionOptions { CommandTimeoutSeconds = ctx.Runtime.Options.CommandTimeoutSeconds }), ct);

        return result.AffectedCount == 0 ? Error(404, "NotFound", "Record not found.") : new EngineResponse { StatusCode = 204 };
    }

    private async Task<EngineResponse> BulkUpdateAsync(Context ctx, string tableName, EngineRequest request, CancellationToken ct)
    {
        var (table, decision, error) = await AuthorizeWriteAsync(ctx, tableName, Verb.Patch);
        if (error is not null) return error;
        if (table!.PrimaryKey.Count == 0) return Error(400, ErrorCodes.ValidationInvalidValue, "Bulk update requires a primary key.");

        var json = await ReadJsonAsync(request, ct);
        if (json is null) return Error(400, ErrorCodes.ValidationInvalidValue, "A JSON body is required.");
        var (elements, _) = ExtractRecords(json.Value);

        var results = new List<object>();
        foreach (var element in elements)
        {
            var record = PayloadBinder.Bind(element, table, ctx.Runtime.Schema, allowDeepInsert: false);
            ValidateWritable(record, decision!);
            var keyValues = table.PrimaryKey.ToDictionary(k => k, k => record.Values.TryGetValue(k, out var v) ? v : null, StringComparer.Ordinal);
            var trimmed = record with { Values = record.Values.Where(kv => !table.PrimaryKey.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) };

            var write = new WriteRequest
            {
                ServiceName = ctx.Runtime.Name, Table = table.ExposedName, Kind = WriteKind.Update,
                Records = [trimmed], Key = new KeyPredicate(keyValues), Precondition = decision!.RowFilter,
            };
            var r = await ctx.Connector.Writer!.WriteAsync(
                new WriteExecution(ctx.Runtime.Connection, ctx.Runtime.Schema, write,
                    new ExecutionOptions { CommandTimeoutSeconds = ctx.Runtime.Options.CommandTimeoutSeconds }), ct);
            StripWrite(r.Records, decision);
            results.Add(new { status = r.AffectedCount > 0 ? 200 : 404, record = r.Records.Count > 0 ? RowToDict(r.Records[0]) : null });
        }

        return JsonOk(new { resource = results });
    }

    private async Task<EngineResponse> BulkDeleteAsync(Context ctx, string tableName, EngineRequest request, CancellationToken ct)
    {
        var (table, decision, error) = await AuthorizeWriteAsync(ctx, tableName, Verb.Delete);
        if (error is not null) return error;

        var q = ParseQuery(request.QueryString);
        FilterNode? filter = null;
        if (q.Ids is { Count: > 0 } && table!.PrimaryKey.Count == 1)
        {
            filter = new InNode(new FieldRef(table.PrimaryKey[0]), q.Ids.Select(v => new ConstantValue(CoerceKey(table, table.PrimaryKey[0], v))).ToList());
        }
        else if (q.Filter is not null)
        {
            filter = RestFilterParser.Parse(q.Filter);
        }
        else
        {
            return Error(400, ErrorCodes.ValidationInvalidValue, "Bulk delete requires ids= or filter=.");
        }

        var write = new WriteRequest
        {
            ServiceName = ctx.Runtime.Name, Table = table!.ExposedName, Kind = WriteKind.Delete,
            Precondition = decision!.RowFilter is null ? filter : new LogicalNode(LogicalOp.And, [filter, decision.RowFilter]),
        };
        var result = await ctx.Connector.Writer!.WriteAsync(
            new WriteExecution(ctx.Runtime.Connection, ctx.Runtime.Schema, write,
                new ExecutionOptions { CommandTimeoutSeconds = ctx.Runtime.Options.CommandTimeoutSeconds }), ct);

        return JsonOk(new { meta = new { affected = result.AffectedCount } });
    }

    // ---- helpers ----

    private async Task<(TableModel? Table, PolicyDecision? Decision, EngineResponse? Error)> AuthorizeWriteAsync(
        Context ctx, string tableName, Verb verb)
    {
        var table = ctx.Runtime.Schema.FindTable(tableName);
        if (table is null) return (null, null, Error(404, "NotFound", $"Unknown table '{tableName}'."));
        if (!table.Writable || ctx.Runtime.Options.ReadOnly)
        {
            return (null, null, Error(403, ErrorCodes.ForbiddenVerb, $"'{tableName}' is read-only."));
        }

        if (ctx.Connector.Writer is null)
        {
            return (null, null, Error(403, ErrorCodes.ForbiddenVerb, "This service is read-only."));
        }

        var decision = ctx.Authorize(table, verb);
        if (!decision.Allowed)
        {
            return (null, null, decision.Hidden
                ? Error(404, "NotFound", $"Unknown table '{tableName}'.")
                : Error(403, decision.DenialCode ?? "Forbidden", decision.DenialMessage ?? "Access denied."));
        }

        await Task.CompletedTask;
        return (table, decision, null);
    }

    private static void ValidateWritable(RecordPayload record, PolicyDecision decision)
    {
        foreach (var field in record.Values.Keys)
        {
            if (decision.DeniedFields.Contains(field) || decision.MaskedFields.ContainsKey(field))
            {
                throw new QueryPolicyRewriter.FieldDeniedException(field);
            }
        }
    }

    private static void StripWrite(IReadOnlyList<Row> rows, PolicyDecision decision)
    {
        if (decision.Bypass) return;
        foreach (var row in rows)
        {
            foreach (var field in decision.DeniedFields.Concat(decision.WriteOnlyFields)) row.Remove(field);
            foreach (var mask in decision.MaskedFields)
            {
                if (row.Has(mask.Key)) row.Set(mask.Key, mask.Value);
            }
        }
    }

    private static FilterNode BuildKeyFilter(TableModel table, string id)
    {
        var predicate = BuildKeyPredicate(table, id);
        FilterNode? filter = null;
        foreach (var pair in predicate.Values)
        {
            var cmp = new ComparisonNode(new FieldRef(pair.Key), ComparisonOp.Eq, new ConstantValue(pair.Value));
            filter = filter is null ? cmp : new LogicalNode(LogicalOp.And, [filter, cmp]);
        }

        return filter ?? throw new QueryValidationException(ErrorCodes.ValidationInvalidValue, "Table has no key.");
    }

    private static KeyPredicate BuildKeyPredicate(TableModel table, string id)
    {
        if (table.PrimaryKey.Count == 0)
        {
            throw new QueryValidationException(ErrorCodes.ValidationInvalidValue, $"Table '{table.ExposedName}' has no key.");
        }

        var parts = id.Split(',');
        if (parts.Length != table.PrimaryKey.Count)
        {
            throw new QueryValidationException(ErrorCodes.ValidationInvalidValue,
                $"Expected {table.PrimaryKey.Count} key value(s).");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < parts.Length; i++)
        {
            values[table.PrimaryKey[i]] = CoerceKey(table, table.PrimaryKey[i], parts[i]);
        }

        return new KeyPredicate(values);
    }

    private static object CoerceKey(TableModel table, string column, string raw)
    {
        var model = table.FindColumn(column);
        return model?.EdmType switch
        {
            "Edm.Int16" or "Edm.Int32" or "Edm.Int64" => long.Parse(raw),
            "Edm.Guid" => Guid.Parse(raw),
            _ => raw,
        };
    }

    private static (IReadOnlyList<JsonElement> Records, bool IsBulk) ExtractRecords(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Array)
        {
            return (json.EnumerateArray().ToList(), true);
        }

        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("resource", out var resource)
            && resource.ValueKind == JsonValueKind.Array)
        {
            return (resource.EnumerateArray().ToList(), true);
        }

        return ([json], false);
    }

    private static IReadOnlyList<OrderByItem> ParseOrder(string? order)
    {
        if (order is null || order.Trim().Length == 0) return [];
        return order.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var bits = part.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return new OrderByItem(bits[0], bits.Length > 1 && bits[1].Equals("desc", StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    private static Dictionary<string, object?> RowToDict(Row row) =>
        row.Values.ToDictionary(p => p.Key, p => NormalizeForJson(p.Value), StringComparer.Ordinal);

    private static object? NormalizeForJson(object? value) => value switch
    {
        DBNull => null,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value,
    };

    private static void ApplyMasks(IReadOnlyList<Row> rows, IReadOnlyDictionary<string, string> masks)
    {
        if (masks.Count == 0) return;
        foreach (var row in rows)
        {
            foreach (var mask in masks) row.Set(mask.Key, mask.Value);
        }
    }

    private static QueryParams ParseQuery(string queryString)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in queryString.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part.Substring(0, eq);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
            pairs[Uri.UnescapeDataString(key)] = value;
        }

        string? Get(string key) => pairs.TryGetValue(key, out var v) ? v : null;
        int? ParseInt(string key) => int.TryParse(Get(key) ?? "", out var v) ? v : null;

        return new QueryParams
        {
            Filter = Get("filter"),
            Fields = Get("fields") is { } f && f != "*"
                ? f.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
                : null,
            Order = Get("order"),
            Limit = ParseInt("limit"),
            Offset = ParseInt("offset"),
            IncludeCount = Get("include_count") == "true",
            Ids = Get("ids")?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
        };
    }

    private sealed record QueryParams
    {
        public string? Filter { get; init; }
        public IReadOnlyList<string>? Fields { get; init; }
        public string? Order { get; init; }
        public int? Limit { get; init; }
        public int? Offset { get; init; }
        public bool IncludeCount { get; init; }
        public IReadOnlyList<string>? Ids { get; init; }
    }

    private static async Task<JsonElement?> ReadJsonAsync(EngineRequest request, CancellationToken ct)
    {
        if (request.Body is null) return null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int StatusForConnectorError(string code) => code switch
    {
        ErrorCodes.ConflictUniqueViolation or ErrorCodes.ConflictForeignKeyViolation => 409,
        ErrorCodes.ValidationNotNullViolation or ErrorCodes.ValidationValueTooLong or ErrorCodes.ValidationInvalidValue => 400,
        ErrorCodes.ForbiddenRowFilter => 403,
        ErrorCodes.UpstreamPermissionDenied => 502,
        ErrorCodes.UpstreamTimeout => 504,
        ErrorCodes.UpstreamUnavailable => 503,
        _ => 500,
    };

    private static EngineResponse JsonOk(object payload) => Json(200, payload);

    private static EngineResponse Created(object payload) => Json(201, payload);

    private static EngineResponse Json(int status, object payload) => new()
    {
        StatusCode = status,
        Body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        ContentType = "application/json",
    };

    private static EngineResponse MethodNotAllowed() => Error(405, "MethodNotAllowed", "Method not allowed on this resource.");

    private static EngineResponse Error(int status, string code, string message) => new()
    {
        StatusCode = status,
        Body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "about:blank",
            title = message,
            status,
            errorCode = code,
        }, JsonOptions),
        ContentType = "application/problem+json",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
