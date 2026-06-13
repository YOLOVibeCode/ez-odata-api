using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace EzOdata.ConformanceTests;

/// <summary>OpenAPI 3.1 generation (spec 11), identity-trimmed and ETag-cached.</summary>
[Collection("conformance")]
public class OpenApiDocsTests
{
    private readonly ConformanceFixture _fixture;

    public OpenApiDocsTests(ConformanceFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Odata_openapi_describes_entity_sets_and_schemas()
    {
        var doc = await _fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/odata/{ConformanceFixture.ServiceName}/openapi.json");

        Assert.Equal("3.1.0", doc.GetProperty("openapi").GetString());

        var paths = doc.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/customers", out _));
        Assert.True(paths.TryGetProperty("/customers({id})", out _));

        var schemas = doc.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("customers", out var customers));
        Assert.True(schemas.TryGetProperty("customersCreate", out _));

        // decimals serialized as string with x-ez-decimal (spec 11 §2)
        var orders = schemas.GetProperty("orders").GetProperty("properties");
        Assert.Equal("string", orders.GetProperty("total").GetProperty("type").GetString());
        Assert.True(orders.GetProperty("total").GetProperty("x-ez-decimal").GetBoolean());
    }

    [Fact]
    public async Task Rest_openapi_uses_table_paths()
    {
        var doc = await _fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/rest/{ConformanceFixture.ServiceName}/openapi.json");

        var paths = doc.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/_table/customers", out _));
        Assert.True(paths.TryGetProperty("/_table/customers/{id}", out _));
    }

    [Fact]
    public async Task Openapi_is_etag_cached()
    {
        var first = await _fixture.Client.GetAsync($"/api/odata/{ConformanceFixture.ServiceName}/openapi.json");
        var etag = first.Headers.ETag?.ToString() ?? first.Headers.GetValues("ETag").First();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/odata/{ConformanceFixture.ServiceName}/openapi.json");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task Openapi_is_identity_trimmed_for_restricted_keys()
    {
        var roles = await _fixture.Client.GetFromJsonAsync<JsonElement>("/system/roles");
        var analyst = roles.EnumerateArray().FirstOrDefault(r => r.GetProperty("name").GetString() == "us-analyst");
        if (analyst.ValueKind != JsonValueKind.Object) return;

        var apps = await _fixture.Client.GetFromJsonAsync<JsonElement>("/system/apps");
        var app = apps.EnumerateArray().First(a => a.GetProperty("name").GetString() == "analyst-app");
        var keyResp = await _fixture.Client.PostAsJsonAsync($"/system/apps/{app.GetProperty("id").GetInt64()}/keys", new { name = "doc" });
        var key = (await keyResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);

        var doc = await client.GetFromJsonAsync<JsonElement>($"/api/odata/{ConformanceFixture.ServiceName}/openapi.json");
        var schemas = doc.GetProperty("components").GetProperty("schemas");

        Assert.False(schemas.TryGetProperty("products", out _));  // ungranted table absent
        var customers = schemas.GetProperty("customers").GetProperty("properties");
        Assert.False(customers.TryGetProperty("ssn", out _));      // denied field absent
        Assert.True(customers.TryGetProperty("email", out _));     // masked field present
    }
}
