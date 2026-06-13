using System.Text.Json;
using System.Text.Json.Nodes;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core;
using EzOdata.Core.Policy;
using EzOdata.Core.Query;
using EzOdata.Core.Schema;
using EzOdata.Core.Services;
using EzOdata.Rest;

namespace EzOdata.Mcp;

/// <summary>
/// MCP server (spec 09): JSON-RPC 2.0 over HTTP. Tools are derived per-identity from the
/// caller's role, so AI clients can only see/invoke what their key permits. Every tool
/// call flows through the same policy engine + connectors as HTTP (MCP-3).
/// </summary>
public sealed class McpServer
{
    private readonly IServiceRuntimeResolver _services;
    private readonly IConnectorRegistry _connectors;
    private readonly PolicyEngine _policy;
    private readonly IPolicySource _policySource;
    private readonly RowFilterParserFactory _rowFilterFactory;
    private readonly McpOptions _options;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _visibleServices;

    public McpServer(
        IServiceRuntimeResolver services, IConnectorRegistry connectors, PolicyEngine policy,
        IPolicySource policySource, RowFilterParserFactory rowFilterFactory, McpOptions options,
        Func<CancellationToken, Task<IReadOnlyList<string>>> visibleServices)
    {
        _services = services;
        _connectors = connectors;
        _policy = policy;
        _policySource = policySource;
        _rowFilterFactory = rowFilterFactory;
        _options = options;
        _visibleServices = visibleServices;
    }

    public delegate RowFilterParser RowFilterParserFactory(
        string serviceName, SchemaSnapshot schema, string schemaVersion, RequestIdentity identity);

    /// <summary>Handles one JSON-RPC request, returning the response node (null for notifications).</summary>
    public async Task<JsonNode?> HandleAsync(JsonNode request, RequestIdentity identity, bool mcpAllowedForApp, CancellationToken ct)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();

        try
        {
            return method switch
            {
                "initialize" => Result(id, InitializeResult()),
                "notifications/initialized" => null,
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, await ToolsListAsync(identity, ct)),
                "tools/call" => Result(id, await ToolsCallAsync(request["params"], identity, mcpAllowedForApp, ct)),
                _ => ErrorResponse(id, -32601, $"Method '{method}' not found."),
            };
        }
        catch (McpToolException ex)
        {
            return Result(id, ToolError(ex.Message));
        }
        catch (QueryPolicyRewriter.FieldDeniedException ex)
        {
            return Result(id, ToolError($"Forbidden.FieldDenied: {ex.Message}"));
        }
        catch (QueryValidationException ex)
        {
            return Result(id, ToolError($"{ex.ErrorCode}: {ex.Message}"));
        }
        catch (ConnectorException ex)
        {
            return Result(id, ToolError($"{ex.ErrorCode}: {ex.Message}"));
        }
        catch (Exception)
        {
            return ErrorResponse(id, -32603, "Internal error.");
        }
    }

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject { ["listChanged"] = true },
        },
        ["serverInfo"] = new JsonObject { ["name"] = "ez-odata-api", ["version"] = "1.0" },
    };

    // ---- tools/list ----

    private async Task<JsonObject> ToolsListAsync(RequestIdentity identity, CancellationToken ct)
    {
        var tools = new JsonArray
        {
            Tool("list_services", "List data services visible to you.", EmptySchema()),
            Tool("explain_filter_syntax", "Return the filter grammar cheat-sheet for query tools.", EmptySchema()),
        };

        foreach (var serviceName in await _visibleServices(ct))
        {
            var runtime = await _services.ResolveAsync(serviceName, ct);
            if (runtime is null || runtime.Status is not (ServiceStatus.Active or ServiceStatus.Refreshing)) continue;

            var roleRules = await _policySource.GetRoleRulesAsync(identity.RoleIds, ct);
            var parser = _rowFilterFactory(runtime.Name, runtime.Schema, runtime.SchemaVersion, identity);
            var visible = SnapshotTrimmer.Trim(runtime.Schema, identity, roleRules, runtime.Name, _policy, parser);
            if (visible.Tables.Count == 0) continue;

            var tableNames = visible.Tables.Select(t => t.ExposedName).ToArray();
            var p = serviceName;

            tools.Add(Tool($"{p}_list_tables", $"List tables in the '{p}' service.", EmptySchema()));
            tools.Add(Tool($"{p}_describe_table", $"Describe a table's columns in '{p}'.", TableArgSchema(tableNames)));
            tools.Add(Tool($"{p}_query", $"Query a table in '{p}' with an optional filter.", QuerySchema(tableNames)));
            tools.Add(Tool($"{p}_count", $"Count rows in a table in '{p}'.", CountSchema(tableNames)));

            // Writes gated by instance + (future) per-service flag (MCP-6)
            var writable = visible.Tables.Any(t => t.Writable);
            if (_options.AllowWrites && writable && _connectors.TryGet(runtime.ConnectorType, out var c) && c.Writer is not null)
            {
                tools.Add(Tool($"{p}_insert", $"Insert a record into a table in '{p}'.", WriteSchema(tableNames)));
                tools.Add(Tool($"{p}_update", $"Update a record by key in '{p}'.", WriteSchema(tableNames)));
                tools.Add(Tool($"{p}_delete", $"Delete a record by key in '{p}' (requires confirm:true).", DeleteSchema(tableNames)));
            }
        }

        return new JsonObject { ["tools"] = tools };
    }

    // ---- tools/call ----

    private async Task<JsonObject> ToolsCallAsync(JsonNode? parameters, RequestIdentity identity, bool mcpAllowedForApp, CancellationToken ct)
    {
        if (!mcpAllowedForApp)
        {
            throw new McpToolException("MCP access is disabled for this application key.");
        }

        var name = parameters?["name"]?.GetValue<string>()
            ?? throw new McpToolException("Tool name is required.");
        var args = parameters?["arguments"] as JsonObject ?? [];

        if (name == "list_services")
        {
            var names = await _visibleServices(ct);
            return ToolResult(new JsonObject { ["services"] = new JsonArray(names.Select(n => (JsonNode)n!).ToArray()) });
        }

        if (name == "explain_filter_syntax")
        {
            return ToolResult(new JsonObject { ["syntax"] = FilterSyntaxHelp });
        }

        var underscore = name.IndexOf('_');
        if (underscore < 0) throw new McpToolException($"Unknown tool '{name}'.");

        // service-prefixed tools: {service}_{verb}; service names may contain underscores,
        // so match against the known visible services.
        var serviceName = await ResolveServicePrefixAsync(name, ct)
            ?? throw new McpToolException($"Unknown tool '{name}'.");
        var verb = name.Substring(serviceName.Length + 1);

        var runtime = await _services.ResolveAsync(serviceName, ct)
            ?? throw new McpToolException($"Service '{serviceName}' is unavailable.");
        if (!_connectors.TryGet(runtime.ConnectorType, out var connector))
        {
            throw new McpToolException($"Connector for '{serviceName}' is unavailable.");
        }

        var roleRules = await _policySource.GetRoleRulesAsync(identity.RoleIds, ct);
        var parser = _rowFilterFactory(runtime.Name, runtime.Schema, runtime.SchemaVersion, identity);

        return verb switch
        {
            "list_tables" => ListTables(runtime, identity, roleRules, parser),
            "describe_table" => DescribeTable(runtime, identity, roleRules, parser, args),
            "query" => await QueryAsync(runtime, connector, identity, roleRules, parser, args, ct),
            "count" => await CountAsync(runtime, connector, identity, roleRules, parser, args, ct),
            "insert" => await InsertAsync(runtime, connector, identity, roleRules, parser, args, ct),
            "update" => await UpdateAsync(runtime, connector, identity, roleRules, parser, args, ct),
            "delete" => await DeleteAsync(runtime, connector, identity, roleRules, parser, args, ct),
            _ => throw new McpToolException($"Unknown tool '{name}'."),
        };
    }

    private async Task<string?> ResolveServicePrefixAsync(string toolName, CancellationToken ct)
    {
        foreach (var service in await _visibleServices(ct))
        {
            if (toolName.StartsWith(service + "_", StringComparison.Ordinal)) return service;
        }

        return null;
    }

    private JsonObject ListTables(ServiceRuntime runtime, RequestIdentity identity, IReadOnlyList<RoleRuleSet> roles, RowFilterParser parser)
    {
        var visible = SnapshotTrimmer.Trim(runtime.Schema, identity, roles, runtime.Name, _policy, parser);
        var tables = new JsonArray();
        foreach (var table in visible.Tables)
        {
            tables.Add(new JsonObject
            {
                ["name"] = table.ExposedName,
                ["isView"] = table.IsView,
                ["description"] = table.Comment,
            });
        }

        return ToolResult(new JsonObject { ["tables"] = tables });
    }

    private JsonObject DescribeTable(ServiceRuntime runtime, RequestIdentity identity, IReadOnlyList<RoleRuleSet> roles, RowFilterParser parser, JsonObject args)
    {
        var tableName = args["table"]?.GetValue<string>() ?? throw new McpToolException("'table' is required.");
        var table = runtime.Schema.FindTable(tableName) ?? throw new McpToolException($"Unknown table '{tableName}'.");
        var decision = Authorize(runtime, identity, roles, parser, table, Verb.Get);
        if (!decision.Allowed) throw new McpToolException($"Table '{tableName}' is not accessible.");

        var columns = new JsonArray();
        foreach (var c in table.Columns)
        {
            if (decision.DeniedFields.Contains(c.ExposedName) || decision.WriteOnlyFields.Contains(c.ExposedName)) continue;
            columns.Add(new JsonObject
            {
                ["name"] = c.ExposedName,
                ["type"] = c.EdmType,
                ["nullable"] = c.Nullable,
                ["primaryKey"] = c.IsPrimaryKey,
                ["masked"] = decision.MaskedFields.ContainsKey(c.ExposedName),
                ["allowedValues"] = c.AllowedValues is null ? null : new JsonArray(c.AllowedValues.Select(v => (JsonNode)v!).ToArray()),
                ["description"] = c.Comment,
            });
        }

        return ToolResult(new JsonObject { ["table"] = tableName, ["columns"] = columns });
    }

    private async Task<JsonObject> QueryAsync(
        ServiceRuntime runtime, ConnectorDescriptor connector, RequestIdentity identity,
        IReadOnlyList<RoleRuleSet> roles, RowFilterParser parser, JsonObject args, CancellationToken ct)
    {
        var tableName = args["table"]?.GetValue<string>() ?? throw new McpToolException("'table' is required.");
        var table = runtime.Schema.FindTable(tableName) ?? throw new McpToolException($"Unknown table '{tableName}'.");
        var decision = Authorize(runtime, identity, roles, parser, table, Verb.Get);
        if (!decision.Allowed) throw new McpToolException($"Table '{tableName}' is not accessible.");

        var filter = args["filter"]?.GetValue<string>() is { Length: > 0 } f ? RestFilterParser.Parse(f) : null;
        if (filter is not null && !decision.Bypass)
        {
            CrossTableFilterValidator.Validate(filter, table, runtime.Schema,
                t => Authorize(runtime, identity, roles, parser, t, Verb.Get));
        }

        var limit = Math.Min(args["limit"]?.GetValue<int>() ?? 25, _options.MaxRowsPerCall);
        var fields = (args["fields"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList();

        var query = new QueryRequest
        {
            ServiceName = runtime.Name,
            Table = table.ExposedName,
            Filter = filter,
            Select = fields,
            OrderBy = ParseOrder(args["order"]?.GetValue<string>()),
            Top = limit,
        };

        var rewritten = QueryPolicyRewriter.Rewrite(query, decision, table);
        var masks = QueryPolicyRewriter.MasksToApply(query, decision);
        var result = await connector.Reader.QueryAsync(
            new QueryExecution(runtime.Connection, runtime.Schema, rewritten,
                new ExecutionOptions { CommandTimeoutSeconds = runtime.Options.CommandTimeoutSeconds, RowLimit = limit }), ct);

        var rows = new JsonArray();
        foreach (var row in result.Rows)
        {
            var obj = new JsonObject();
            foreach (var pair in row.Values)
            {
                obj[pair.Key] = masks.TryGetValue(pair.Key, out var mask) ? mask : JsonValue.Create(ToJson(pair.Value));
            }

            rows.Add(obj);
        }

        return ToolResult(new JsonObject
        {
            ["rows"] = rows,
            ["rowCount"] = result.Rows.Count,
            ["truncated"] = result.HasMore,
        });
    }

    private async Task<JsonObject> CountAsync(
        ServiceRuntime runtime, ConnectorDescriptor connector, RequestIdentity identity,
        IReadOnlyList<RoleRuleSet> roles, RowFilterParser parser, JsonObject args, CancellationToken ct)
    {
        var tableName = args["table"]?.GetValue<string>() ?? throw new McpToolException("'table' is required.");
        var table = runtime.Schema.FindTable(tableName) ?? throw new McpToolException($"Unknown table '{tableName}'.");
        var decision = Authorize(runtime, identity, roles, parser, table, Verb.Get);
        if (!decision.Allowed) throw new McpToolException($"Table '{tableName}' is not accessible.");

        var filter = args["filter"]?.GetValue<string>() is { Length: > 0 } f ? RestFilterParser.Parse(f) : null;
        var query = new QueryRequest { ServiceName = runtime.Name, Table = table.ExposedName, Filter = filter };
        var rewritten = QueryPolicyRewriter.Rewrite(query, decision, table);
        var count = await connector.Reader.CountAsync(
            new QueryExecution(runtime.Connection, runtime.Schema, rewritten,
                new ExecutionOptions { CommandTimeoutSeconds = runtime.Options.CommandTimeoutSeconds }), ct);

        return ToolResult(new JsonObject { ["count"] = count });
    }

    private async Task<JsonObject> InsertAsync(
        ServiceRuntime runtime, ConnectorDescriptor connector, RequestIdentity identity,
        IReadOnlyList<RoleRuleSet> roles, RowFilterParser parser, JsonObject args, CancellationToken ct)
    {
        var (table, decision) = AuthorizeWrite(runtime, connector, identity, roles, parser, args, Verb.Post);
        var record = BindRecord(args["record"], table, runtime.Schema, decision);

        var write = new WriteRequest
        {
            ServiceName = runtime.Name, Table = table.ExposedName, Kind = WriteKind.Insert,
            Records = [record], InsertVisibilityFilter = decision.RowFilter,
        };
        var result = await connector.Writer!.WriteAsync(
            new WriteExecution(runtime.Connection, runtime.Schema, write,
                new ExecutionOptions { CommandTimeoutSeconds = runtime.Options.CommandTimeoutSeconds }), ct);

        return ToolResult(new JsonObject { ["inserted"] = result.AffectedCount, ["record"] = RowToJson(result.Records, decision) });
    }

    private async Task<JsonObject> UpdateAsync(
        ServiceRuntime runtime, ConnectorDescriptor connector, RequestIdentity identity,
        IReadOnlyList<RoleRuleSet> roles, RowFilterParser parser, JsonObject args, CancellationToken ct)
    {
        var (table, decision) = AuthorizeWrite(runtime, connector, identity, roles, parser, args, Verb.Patch);
        var record = BindRecord(args["record"], table, runtime.Schema, decision);
        var key = BindKey(args["key"], table);

        var write = new WriteRequest
        {
            ServiceName = runtime.Name, Table = table.ExposedName, Kind = WriteKind.Update,
            Records = [record], Key = key, Precondition = decision.RowFilter,
        };
        var result = await connector.Writer!.WriteAsync(
            new WriteExecution(runtime.Connection, runtime.Schema, write,
                new ExecutionOptions { CommandTimeoutSeconds = runtime.Options.CommandTimeoutSeconds }), ct);

        if (result.AffectedCount == 0) throw new McpToolException("Record not found or not visible.");
        return ToolResult(new JsonObject { ["updated"] = result.AffectedCount, ["record"] = RowToJson(result.Records, decision) });
    }

    private async Task<JsonObject> DeleteAsync(
        ServiceRuntime runtime, ConnectorDescriptor connector, RequestIdentity identity,
        IReadOnlyList<RoleRuleSet> roles, RowFilterParser parser, JsonObject args, CancellationToken ct)
    {
        if (args["confirm"]?.GetValue<bool>() != true)
        {
            throw new McpToolException("Delete requires \"confirm\": true (MCP-6 safety gate).");
        }

        var (table, decision) = AuthorizeWrite(runtime, connector, identity, roles, parser, args, Verb.Delete);
        var key = BindKey(args["key"], table);

        var write = new WriteRequest
        {
            ServiceName = runtime.Name, Table = table.ExposedName, Kind = WriteKind.Delete,
            Key = key, Precondition = decision.RowFilter,
        };
        var result = await connector.Writer!.WriteAsync(
            new WriteExecution(runtime.Connection, runtime.Schema, write,
                new ExecutionOptions { CommandTimeoutSeconds = runtime.Options.CommandTimeoutSeconds }), ct);

        if (result.AffectedCount == 0) throw new McpToolException("Record not found or not visible.");
        return ToolResult(new JsonObject { ["deleted"] = result.AffectedCount });
    }

    // ---- shared helpers ----

    private (TableModel Table, PolicyDecision Decision) AuthorizeWrite(
        ServiceRuntime runtime, ConnectorDescriptor connector, RequestIdentity identity,
        IReadOnlyList<RoleRuleSet> roles, RowFilterParser parser, JsonObject args, Verb verb)
    {
        if (!_options.AllowWrites) throw new McpToolException("Write tools are disabled on this instance.");
        if (connector.Writer is null || runtime.Options.ReadOnly) throw new McpToolException("This service is read-only.");

        var tableName = args["table"]?.GetValue<string>() ?? throw new McpToolException("'table' is required.");
        var table = runtime.Schema.FindTable(tableName) ?? throw new McpToolException($"Unknown table '{tableName}'.");
        if (!table.Writable) throw new McpToolException($"'{tableName}' is read-only.");

        var decision = Authorize(runtime, identity, roles, parser, table, verb);
        if (!decision.Allowed) throw new McpToolException($"You are not allowed to {verb} '{tableName}'.");
        return (table, decision);
    }

    private PolicyDecision Authorize(
        ServiceRuntime runtime, RequestIdentity identity, IReadOnlyList<RoleRuleSet> roles,
        RowFilterParser parser, TableModel table, Verb verb) =>
        _policy.Authorize(identity, roles, runtime.Name, table.ExposedName, verb,
            table.Columns.Select(c => c.ExposedName).ToList(), parser);

    private static RecordPayload BindRecord(JsonNode? recordNode, TableModel table, SchemaSnapshot schema, PolicyDecision decision)
    {
        if (recordNode is null) throw new McpToolException("'record' is required.");
        var element = JsonSerializer.Deserialize<JsonElement>(recordNode.ToJsonString());
        var record = PayloadBinder.Bind(element, table, schema, allowDeepInsert: false);
        foreach (var field in record.Values.Keys)
        {
            if (decision.DeniedFields.Contains(field) || decision.MaskedFields.ContainsKey(field))
            {
                throw new QueryPolicyRewriter.FieldDeniedException(field);
            }
        }

        return record;
    }

    private static KeyPredicate BindKey(JsonNode? keyNode, TableModel table)
    {
        if (keyNode is not JsonObject obj) throw new McpToolException("'key' object is required.");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var keyColumn in table.PrimaryKey)
        {
            if (obj[keyColumn] is null) throw new McpToolException($"Key column '{keyColumn}' is required.");
            values[keyColumn] = obj[keyColumn]!.GetValue<JsonElement>().ValueKind == JsonValueKind.Number
                ? obj[keyColumn]!.GetValue<long>()
                : obj[keyColumn]!.GetValue<string>();
        }

        return new KeyPredicate(values);
    }

    private static JsonNode? RowToJson(IReadOnlyList<Row> rows, PolicyDecision decision)
    {
        if (rows.Count == 0) return null;
        var obj = new JsonObject();
        foreach (var pair in rows[0].Values)
        {
            if (decision.DeniedFields.Contains(pair.Key) || decision.WriteOnlyFields.Contains(pair.Key)) continue;
            obj[pair.Key] = decision.MaskedFields.TryGetValue(pair.Key, out var mask)
                ? mask
                : JsonValue.Create(ToJson(pair.Value));
        }

        return obj;
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

    private static object? ToJson(object? value) => value switch
    {
        null or DBNull => null,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToString("O"),
        DateTimeOffset dto => dto.ToString("O"),
        byte[] bytes => Convert.ToBase64String(bytes),
        Guid g => g.ToString(),
        decimal or double or float or int or long or short or bool => value,
        _ => value.ToString(),
    };

    // ---- JSON-RPC + tool envelope ----

    private static JsonObject Result(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static JsonObject ToolResult(JsonObject structured) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = structured.ToJsonString() },
        },
        ["structuredContent"] = structured,
        ["isError"] = false,
    };

    private static JsonObject ToolError(string message) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = message } },
        ["isError"] = true,
    };

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
    };

    private static JsonObject EmptySchema() => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    private static JsonObject TableArgSchema(string[] tables) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray { "table" },
        ["properties"] = new JsonObject { ["table"] = TableEnum(tables) },
    };

    private static JsonObject QuerySchema(string[] tables) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray { "table" },
        ["properties"] = new JsonObject
        {
            ["table"] = TableEnum(tables),
            ["filter"] = new JsonObject { ["type"] = "string", ["description"] = "SQL-ish filter, e.g. (status='open') and (total>250)" },
            ["fields"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["order"] = new JsonObject { ["type"] = "string", ["description"] = "e.g. 'created_at desc'" },
            ["limit"] = new JsonObject { ["type"] = "integer", ["maximum"] = 200, ["default"] = 25 },
        },
    };

    private static JsonObject CountSchema(string[] tables) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray { "table" },
        ["properties"] = new JsonObject
        {
            ["table"] = TableEnum(tables),
            ["filter"] = new JsonObject { ["type"] = "string" },
        },
    };

    private static JsonObject WriteSchema(string[] tables) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray { "table", "record" },
        ["properties"] = new JsonObject
        {
            ["table"] = TableEnum(tables),
            ["record"] = new JsonObject { ["type"] = "object" },
            ["key"] = new JsonObject { ["type"] = "object" },
        },
    };

    private static JsonObject DeleteSchema(string[] tables) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray { "table", "key", "confirm" },
        ["properties"] = new JsonObject
        {
            ["table"] = TableEnum(tables),
            ["key"] = new JsonObject { ["type"] = "object" },
            ["confirm"] = new JsonObject { ["type"] = "boolean", ["description"] = "Must be true to delete." },
        },
    };

    private static JsonObject TableEnum(string[] tables) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(tables.Select(t => (JsonNode)t!).ToArray()),
    };

    private const string FilterSyntaxHelp =
        "Filter grammar: field op value, combined with 'and'/'or'/'not' and parentheses. " +
        "Operators: = != > >= < <=, 'in (a,b)', 'is null', 'is not null', 'contains', 'starts with', 'ends with'. " +
        "Strings use single quotes (escape ' as ''). Example: (country='US') and (total>100).";
}

public sealed class McpToolException : Exception
{
    public McpToolException(string message) : base(message) { }
}

public sealed class McpOptions
{
    public bool Enabled { get; set; } = true;
    public bool AllowWrites { get; set; }
    public int MaxRowsPerCall { get; set; } = 200;
}
