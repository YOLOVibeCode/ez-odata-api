using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace EzOdata.ConformanceTests;

/// <summary>MCP server (spec 09): per-identity tools, governed calls, safety gating.</summary>
[Collection("conformance")]
public class McpServerTests : IAsyncLifetime
{
    private readonly ConformanceFixture _fixture;
    private HttpClient _client = null!;
    private long _appId;

    public McpServerTests(ConformanceFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        // A broad read-only role over the whole sales service for MCP.
        var admin = _fixture.Client;
        var roles = await admin.GetFromJsonAsync<JsonElement>("/system/roles");
        var existing = roles.EnumerateArray().FirstOrDefault(r => r.GetProperty("name").GetString() == "mcp-reader");

        long roleId;
        if (existing.ValueKind == JsonValueKind.Object)
        {
            roleId = existing.GetProperty("id").GetInt64();
        }
        else
        {
            var roleResp = await admin.PostAsJsonAsync("/system/roles", new
            {
                name = "mcp-reader",
                isActive = true,
                access = new object[]
                {
                    new { serviceName = ConformanceFixture.ServiceName, resourcePattern = "*", verbs = new[] { "GET" }, effect = "allow", priority = 0 },
                },
            });
            roleId = (await roleResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        }

        var appResp = await admin.PostAsJsonAsync("/system/apps", new
        {
            name = $"mcp-app-{Guid.NewGuid():N}"[..28], roleId, isActive = true, requireUserSession = false, mcpEnabled = true,
        });
        _appId = (await appResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var keyResp = await admin.PostAsJsonAsync($"/system/apps/{_appId}/keys", new { name = "mcp" });
        var key = (await keyResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;

        _client = _fixture.CreateClient();
        _client.DefaultRequestHeaders.Add("X-API-Key", key);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonElement> RpcAsync(string method, object? @params = null, int id = 1)
    {
        var body = @params is null
            ? (object)new { jsonrpc = "2.0", id, method }
            : new { jsonrpc = "2.0", id, method, @params };
        var response = await _client.PostAsJsonAsync("/mcp", body);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement ToolStructured(JsonElement rpc) =>
        rpc.GetProperty("result").GetProperty("structuredContent");

    [Fact]
    public async Task Initialize_reports_capabilities()
    {
        var result = await RpcAsync("initialize");
        Assert.Equal("ez-odata-api", result.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Tools_list_includes_per_service_tools()
    {
        var tools = (await RpcAsync("tools/list")).GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();

        Assert.Contains("list_services", tools);
        Assert.Contains("sales_list_tables", tools);
        Assert.Contains("sales_describe_table", tools);
        Assert.Contains("sales_query", tools);
        Assert.Contains("sales_count", tools);
        // writes off by default (MCP-6)
        Assert.DoesNotContain("sales_insert", tools);
    }

    [Fact]
    public async Task Query_tool_returns_governed_rows()
    {
        var result = await RpcAsync("tools/call", new
        {
            name = "sales_query",
            arguments = new { table = "orders", filter = "status='open'", limit = 50 },
        });

        Assert.False(result.GetProperty("result").GetProperty("isError").GetBoolean());
        var structured = ToolStructured(result);
        Assert.Equal(5, structured.GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public async Task Count_tool()
    {
        var result = await RpcAsync("tools/call", new { name = "sales_count", arguments = new { table = "customers" } });
        Assert.Equal(8, ToolStructured(result).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Describe_table_lists_columns()
    {
        var result = await RpcAsync("tools/call", new { name = "sales_describe_table", arguments = new { table = "customers" } });
        var columns = ToolStructured(result).GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("email", columns);
        Assert.Contains("id", columns);
    }

    [Fact]
    public async Task Bad_filter_returns_tool_error_not_rpc_error()
    {
        var result = await RpcAsync("tools/call", new
        {
            name = "sales_query", arguments = new { table = "customers", filter = "name =" },
        });

        // Tool errors surface as isError content, not JSON-RPC protocol errors (spec 09 §2.2)
        Assert.True(result.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.False(result.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Write_tools_absent_and_call_rejected_when_disabled()
    {
        var result = await RpcAsync("tools/call", new
        {
            name = "sales_insert", arguments = new { table = "customers", record = new { name = "x", email = "y@z.co" } },
        });
        Assert.True(result.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Anonymous_mcp_call_is_401()
    {
        var anon = _fixture.CreateClient();
        var response = await anon.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_reports_tool_count()
    {
        var response = await _client.GetFromJsonAsync<JsonElement>("/mcp/health");
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.True(response.GetProperty("toolsAvailableForKey").GetInt32() > 0);
    }

    [Fact]
    public async Task Restricted_analyst_key_only_sees_permitted_tables_in_mcp()
    {
        var roles = await _fixture.Client.GetFromJsonAsync<JsonElement>("/system/roles");
        var analyst = roles.EnumerateArray().FirstOrDefault(r => r.GetProperty("name").GetString() == "us-analyst");
        if (analyst.ValueKind != JsonValueKind.Object) return;

        var apps = await _fixture.Client.GetFromJsonAsync<JsonElement>("/system/apps");
        var app = apps.EnumerateArray().First(a => a.GetProperty("name").GetString() == "analyst-app");
        var keyResp = await _fixture.Client.PostAsJsonAsync($"/system/apps/{app.GetProperty("id").GetInt64()}/keys", new { name = "mcp" });
        var key = (await keyResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);

        var listResp = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "sales_list_tables", arguments = new { } },
        });
        var structured = (await listResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("result").GetProperty("structuredContent");
        var tables = structured.GetProperty("tables").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();

        Assert.Contains("customers", tables);
        Assert.DoesNotContain("products", tables); // ungranted

        // describing customers must not reveal ssn
        var describeResp = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = "sales_describe_table", arguments = new { table = "customers" } },
        });
        var cols = (await describeResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("result").GetProperty("structuredContent")
            .GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("ssn", cols);
    }
}
