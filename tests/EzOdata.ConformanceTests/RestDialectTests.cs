using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace EzOdata.ConformanceTests;

/// <summary>REST/JSON dialect (spec 06) over the same engine as OData, against the northwind fixture.</summary>
[Collection("conformance")]
public class RestDialectTests
{
    private readonly ConformanceFixture _fixture;

    public RestDialectTests(ConformanceFixture fixture) => _fixture = fixture;

    private string Url(string rest) => $"/api/rest/{ConformanceFixture.ServiceName}{rest}";

    [Fact]
    public async Task List_tables_and_schema_discovery()
    {
        var tables = await _fixture.Client.GetFromJsonAsync<JsonElement>(Url("/_table"));
        var names = tables.GetProperty("resource").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("customers", names);
        Assert.Contains("v_customer_order_totals", names);

        var schema = await _fixture.Client.GetFromJsonAsync<JsonElement>(Url("/_table/customers/_schema"));
        var fields = schema.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()).ToList();
        Assert.Contains("email", fields);
        Assert.Contains("id", fields);
    }

    [Fact]
    public async Task Query_with_sqlish_filter_order_fields_count()
    {
        var response = await _fixture.Client.GetFromJsonAsync<JsonElement>(
            Url("/_table/orders?filter=(status='open') and (total>250)&order=total desc&fields=id,total&include_count=true"));

        // open orders over 250: #6 (519.96) and #8 (279.49)
        Assert.Equal(2, response.GetProperty("meta").GetProperty("count").GetInt64());
        var first = response.GetProperty("resource")[0];
        Assert.Equal(6, first.GetProperty("id").GetInt32()); // highest total first
        Assert.False(first.TryGetProperty("status", out _)); // projected out
    }

    [Fact]
    public async Task Filter_grammar_features()
    {
        var inList = await _fixture.Client.GetFromJsonAsync<JsonElement>(
            Url("/_table/customers?filter=country in ('DE','JP')&include_count=true"));
        Assert.Equal(2, inList.GetProperty("meta").GetProperty("count").GetInt64());

        var contains = await _fixture.Client.GetFromJsonAsync<JsonElement>(
            Url("/_table/customers?filter=name contains 'Corp'&include_count=true"));
        Assert.Equal(2, contains.GetProperty("meta").GetProperty("count").GetInt64());

        var nullCheck = await _fixture.Client.GetFromJsonAsync<JsonElement>(
            Url("/_table/customers?filter=ssn is null&include_count=true"));
        Assert.Equal(3, nullCheck.GetProperty("meta").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task Get_by_id_and_composite_key()
    {
        var single = await _fixture.Client.GetFromJsonAsync<JsonElement>(Url("/_table/customers/1"));
        Assert.Equal("Acme Corp", single.GetProperty("name").GetString());

        var composite = await _fixture.Client.GetFromJsonAsync<JsonElement>(Url("/_table/order_items/1,2"));
        Assert.Equal(1, composite.GetProperty("qty").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await _fixture.Client.GetAsync(Url("/_table/customers/99999"))).StatusCode);
    }

    [Fact]
    public async Task Crud_lifecycle()
    {
        var email = $"rest-{Guid.NewGuid():N}@x.example";

        var create = await _fixture.Client.PostAsJsonAsync(Url("/_table/customers"),
            new { name = "REST Co", email, country = "IE" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var patch = await _fixture.Client.PatchAsJsonAsync(Url($"/_table/customers/{id}"), new { country = "FR" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("FR", (await patch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("country").GetString());

        var delete = await _fixture.Client.DeleteAsync(Url($"/_table/customers/{id}"));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Bulk_insert_via_resource_envelope()
    {
        var body = new { resource = new object[]
        {
            new { name = "Bulk REST 1", email = $"br1-{Guid.NewGuid():N}@x.example" },
            new { name = "Bulk REST 2", email = $"br2-{Guid.NewGuid():N}@x.example" },
        }};
        var response = await _fixture.Client.PostAsJsonAsync(Url("/_table/customers"), body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = created.GetProperty("resource").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            await _fixture.Client.DeleteAsync(Url($"/_table/customers/{row.GetProperty("id").GetInt32()}"));
        }
    }

    [Fact]
    public async Task Error_taxonomy_uses_problem_json()
    {
        var dup = await _fixture.Client.PostAsJsonAsync(Url("/_table/customers"),
            new { name = "Dupe", email = "ops@acme.example" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var body = await dup.Content.ReadAsStringAsync();
        Assert.Contains("Conflict.UniqueViolation", body);
    }

    [Fact]
    public async Task Malformed_filter_is_400()
    {
        var response = await _fixture.Client.GetAsync(Url("/_table/customers?filter=name ="));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task View_rejects_writes()
    {
        var response = await _fixture.Client.PostAsJsonAsync(Url("/_table/v_customer_order_totals"), new { name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Restricted_key_sees_rest_policy_too()
    {
        // Reuse the analyst role from SecurityConformanceTests if present
        var roles = await _fixture.Client.GetFromJsonAsync<JsonElement>("/system/roles");
        var analyst = roles.EnumerateArray().FirstOrDefault(r => r.GetProperty("name").GetString() == "us-analyst");
        if (analyst.ValueKind != JsonValueKind.Object) return;

        var apps = await _fixture.Client.GetFromJsonAsync<JsonElement>("/system/apps");
        var app = apps.EnumerateArray().First(a => a.GetProperty("name").GetString() == "analyst-app");
        var keyResp = await _fixture.Client.PostAsJsonAsync($"/system/apps/{app.GetProperty("id").GetInt64()}/keys", new { name = "rest" });
        var key = (await keyResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);

        // products not granted → 404; ssn denied + email masked + US-only on customers
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url("/_table/products"))).StatusCode);

        var customers = await client.GetFromJsonAsync<JsonElement>(Url("/_table/customers?include_count=true"));
        Assert.Equal(5, customers.GetProperty("meta").GetProperty("count").GetInt64());
        var row = customers.GetProperty("resource")[0];
        Assert.False(row.TryGetProperty("ssn", out _));
        Assert.Equal("***@***", row.GetProperty("email").GetString());

        // denied field in filter → 403
        var denied = await client.GetAsync(Url("/_table/customers?filter=ssn is not null"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
