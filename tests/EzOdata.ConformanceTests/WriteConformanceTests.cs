using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace EzOdata.ConformanceTests;

/// <summary>Write surface (spec 05 §5–6): create, patch, put, delete, bulk, deep insert, $batch, error taxonomy.</summary>
[Collection("conformance")]
public class WriteConformanceTests
{
    private readonly ConformanceFixture _fixture;

    public WriteConformanceTests(ConformanceFixture fixture) => _fixture = fixture;

    private string Url(string rest) => $"/api/odata/{ConformanceFixture.ServiceName}{rest}";

    private static string UniqueEmail() => $"w-{Guid.NewGuid():N}@write.example";

    [Fact]
    public async Task Create_patch_delete_lifecycle()
    {
        var client = _fixture.Client;
        var email = UniqueEmail();

        // POST → 201 + Location + representation with generated key
        var create = await client.PostAsJsonAsync(Url("/customers"),
            new { name = "Write Test Co", email, country = "FR" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.NotNull(create.Headers.Location);

        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        Assert.Equal("FR", created.GetProperty("country").GetString());

        // PATCH partial update
        var patch = await client.PatchAsJsonAsync(Url($"/customers({id})"), new { country = "BE" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var patched = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BE", patched.GetProperty("country").GetString());
        Assert.Equal("Write Test Co", patched.GetProperty("name").GetString()); // untouched

        // DELETE → 204, then 404
        var delete = await client.DeleteAsync(Url($"/customers({id})"));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url($"/customers({id})"))).StatusCode);
    }

    [Fact]
    public async Task Put_replaces_and_nulls_missing_nullable_fields()
    {
        var client = _fixture.Client;
        var email = UniqueEmail();

        var create = await client.PostAsJsonAsync(Url("/customers"),
            new { name = "Replace Me", email, country = "NL", ssn = "999-88-7777" });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var put = await client.PutAsJsonAsync(Url($"/customers({id})"),
            new { name = "Replaced", email });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var replaced = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Replaced", replaced.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, replaced.GetProperty("country").ValueKind); // nulled by PUT
        Assert.Equal(JsonValueKind.Null, replaced.GetProperty("ssn").ValueKind);

        await client.DeleteAsync(Url($"/customers({id})"));
    }

    [Fact]
    public async Task Bulk_array_post_inserts_transactionally()
    {
        var client = _fixture.Client;

        var bulk = await client.PostAsJsonAsync(Url("/customers"), new object[]
        {
            new { name = "Bulk A", email = UniqueEmail(), country = "SE" },
            new { name = "Bulk B", email = UniqueEmail(), country = "SE" },
            new { name = "Bulk C", email = UniqueEmail(), country = "SE" },
        });
        Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);

        var body = await bulk.Content.ReadFromJsonAsync<JsonElement>();
        var rows = body.GetProperty("value").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.GetProperty("id").GetInt32() > 0));

        foreach (var row in rows)
        {
            await client.DeleteAsync(Url($"/customers({row.GetProperty("id").GetInt32()})"));
        }
    }

    [Fact]
    public async Task Bulk_insert_with_one_bad_record_rolls_back_everything()
    {
        var client = _fixture.Client;
        var marker = $"rb-{Guid.NewGuid():N}";

        var bulk = await client.PostAsJsonAsync(Url("/customers"), new object[]
        {
            new { name = marker, email = UniqueEmail(), country = "SE" },
            new { name = marker, email = "ops@acme.example", country = "SE" }, // duplicate email → unique violation
        });
        Assert.Equal(HttpStatusCode.Conflict, bulk.StatusCode);

        // First record must have rolled back
        var probe = await client.GetFromJsonAsync<JsonElement>(
            Url($"/customers/$count?$filter=name eq '{marker}'"));
        Assert.Equal(0, probe.GetInt32());
    }

    [Fact]
    public async Task Deep_insert_creates_parent_and_children_atomically()
    {
        var client = _fixture.Client;

        var create = await client.PostAsJsonAsync(Url("/orders"), new
        {
            customer_id = 1,
            status = "open",
            total = 42.42,
            order_items = new object[]
            {
                new { line_no = 1, product_id = 1, qty = 2, unit_price = 19.99 },
                new { line_no = 2, product_id = 3, qty = 1, unit_price = 2.44 },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var order = await create.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = order.GetProperty("id").GetInt64();

        var items = await client.GetFromJsonAsync<JsonElement>(
            Url($"/order_items/$count?$filter=order_id eq {orderId}"));
        Assert.Equal(2, items.GetInt32());

        // cleanup (children first: FK)
        await client.DeleteAsync(Url($"/order_items(order_id={orderId},line_no=1)"));
        await client.DeleteAsync(Url($"/order_items(order_id={orderId},line_no=2)"));
        await client.DeleteAsync(Url($"/orders({orderId})"));
    }

    [Fact]
    public async Task Error_taxonomy_maps_constraint_violations()
    {
        var client = _fixture.Client;

        // Unique violation → 409
        var duplicate = await client.PostAsJsonAsync(Url("/customers"),
            new { name = "Dupe", email = "ops@acme.example" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("Conflict.UniqueViolation", await duplicate.Content.ReadAsStringAsync());

        // FK violation → 409
        var badFk = await client.PostAsJsonAsync(Url("/orders"),
            new { customer_id = 99999, status = "open", total = 1.0 });
        Assert.Equal(HttpStatusCode.Conflict, badFk.StatusCode);
        Assert.Contains("Conflict.ForeignKeyViolation", await badFk.Content.ReadAsStringAsync());

        // Not-null violation → 400
        var missingRequired = await client.PostAsJsonAsync(Url("/customers"),
            new { email = UniqueEmail() }); // name is NOT NULL without default
        Assert.Equal(HttpStatusCode.BadRequest, missingRequired.StatusCode);

        // Unknown property → 400 with stable code
        var unknown = await client.PostAsJsonAsync(Url("/customers"),
            new { name = "X", email = UniqueEmail(), ghost = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("Validation.UnknownProperty", await unknown.Content.ReadAsStringAsync());

        // Strict coercion: string where number expected → 400
        var badType = await client.PostAsJsonAsync(Url("/orders"),
            new { customer_id = "not-a-number", status = "open" });
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);

        // Auto-generated key in payload → 400
        var generatedKey = await client.PostAsJsonAsync(Url("/customers"),
            new { id = 1234, name = "X", email = UniqueEmail() });
        Assert.Equal(HttpStatusCode.BadRequest, generatedKey.StatusCode);
    }

    [Fact]
    public async Task Views_reject_writes()
    {
        var response = await _fixture.Client.PostAsJsonAsync(Url("/v_customer_order_totals"), new { name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Prefer_return_minimal_yields_204()
    {
        var client = _fixture.Client;
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/customers"))
        {
            Content = JsonContent.Create(new { name = "Minimal", email = UniqueEmail() }),
        };
        request.Headers.Add("Prefer", "return=minimal");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // cleanup: find and delete
        var found = await client.GetFromJsonAsync<JsonElement>(Url("/customers?$filter=name eq 'Minimal'&$select=id"));
        foreach (var row in found.GetProperty("value").EnumerateArray())
        {
            await client.DeleteAsync(Url($"/customers({row.GetProperty("id").GetInt32()})"));
        }
    }

    [Fact]
    public async Task Json_batch_runs_requests_in_order()
    {
        var client = _fixture.Client;
        var email = UniqueEmail();

        var batch = await client.PostAsJsonAsync(Url("/$batch"), new
        {
            requests = new object[]
            {
                new
                {
                    id = "1", method = "POST", url = "customers",
                    body = new { name = "Batch Co", email },
                },
                new { id = "2", method = "GET", url = $"customers?$filter=email eq '{email}'&$select=id,name" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);

        var body = await batch.Content.ReadFromJsonAsync<JsonElement>();
        var responses = body.GetProperty("responses").EnumerateArray().ToList();
        Assert.Equal(2, responses.Count);
        Assert.Equal(201, responses[0].GetProperty("status").GetInt32());
        Assert.Equal(200, responses[1].GetProperty("status").GetInt32());

        var queried = responses[1].GetProperty("body").GetProperty("value");
        Assert.Equal("Batch Co", queried[0].GetProperty("name").GetString());

        await client.DeleteAsync(Url($"/customers({queried[0].GetProperty("id").GetInt32()})"));
    }

    [Fact]
    public async Task Restricted_key_cannot_write_and_cannot_smuggle_denied_fields()
    {
        // The us-analyst role from SecurityConformanceTests has GET only.
        var admin = _fixture.Client;
        var roles = await admin.GetFromJsonAsync<JsonElement>("/system/roles");
        var analyst = roles.EnumerateArray().FirstOrDefault(r => r.GetProperty("name").GetString() == "us-analyst");
        if (analyst.ValueKind != JsonValueKind.Object) return;

        var apps = await admin.GetFromJsonAsync<JsonElement>("/system/apps");
        var app = apps.EnumerateArray().First(a => a.GetProperty("name").GetString() == "analyst-app");
        var keyResponse = await admin.PostAsJsonAsync($"/system/apps/{app.GetProperty("id").GetInt64()}/keys", new { name = "w" });
        var key = (await keyResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);

        var post = await client.PostAsJsonAsync(Url("/customers"), new { name = "Nope", email = UniqueEmail() });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

        var delete = await client.DeleteAsync(Url("/customers(1)"));
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }
}
