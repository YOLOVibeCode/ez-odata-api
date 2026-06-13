using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace EzOdata.IntegrationTests;

public class ServicesCrudTests : IClassFixture<HostFixture>
{
    private readonly HostFixture _fixture;

    public ServicesCrudTests(HostFixture fixture) => _fixture = fixture;

    private static object ValidCreateBody(string name) => new
    {
        name,
        label = "Sales DB",
        connectorType = "postgresql",
        connection = new
        {
            host = "db.internal",
            port = 5432,
            database = "sales",
            username = "api_reader",
            password = "super-secret-password",
        },
    };

    [Fact]
    public async Task Create_get_list_roundtrip_never_echoes_credentials()
    {
        var client = await _fixture.CreateAdminClientAsync();

        var create = await client.PostAsJsonAsync("/system/services", ValidCreateBody("sales"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var body = await create.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-password", body);

        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("id").GetInt64();
        Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString()!.ToLowerInvariant());
        Assert.Contains("db.internal", doc.RootElement.GetProperty("connection").GetString());

        var get = await client.GetAsync($"/system/services/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.DoesNotContain("super-secret-password", await get.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Bad Name")]
    [InlineData("1bad")]
    [InlineData("")]
    public async Task Invalid_service_names_are_rejected(string name)
    {
        var client = await _fixture.CreateAdminClientAsync();
        var response = await client.PostAsJsonAsync("/system/services", ValidCreateBody(name));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_connector_type_is_rejected_with_known_list()
    {
        var client = await _fixture.CreateAdminClientAsync();
        var response = await client.PostAsJsonAsync("/system/services", new
        {
            name = "oracle1",
            label = "Oracle",
            connectorType = "oracle",
            connection = new { host = "h", database = "d", username = "u" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("postgresql", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Duplicate_name_conflicts()
    {
        var client = await _fixture.CreateAdminClientAsync();
        await client.PostAsJsonAsync("/system/services", ValidCreateBody("dupe"));
        var second = await client.PostAsJsonAsync("/system/services", ValidCreateBody("dupe"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Delete_soft_deletes_and_frees_the_name()
    {
        var client = await _fixture.CreateAdminClientAsync();

        var create = await client.PostAsJsonAsync("/system/services", ValidCreateBody("ephemeral"));
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var delete = await client.DeleteAsync($"/system/services/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/system/services/{id}")).StatusCode);

        // Name is reusable after soft delete (spec 03 §2.1)
        var recreate = await client.PostAsJsonAsync("/system/services", ValidCreateBody("ephemeral"));
        Assert.Equal(HttpStatusCode.Created, recreate.StatusCode);
    }

    [Fact]
    public async Task Disable_and_enable_toggle_status()
    {
        var client = await _fixture.CreateAdminClientAsync();

        var create = await client.PostAsJsonAsync("/system/services", ValidCreateBody("toggler"));
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var disable = await client.PostAsync($"/system/services/{id}/disable", null);
        var disabled = await disable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("disabled", disabled.GetProperty("status").GetString()!.ToLowerInvariant());

        var enable = await client.PostAsync($"/system/services/{id}/enable", null);
        var enabled = await enable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", enabled.GetProperty("status").GetString()!.ToLowerInvariant());
    }

    [Fact]
    public async Task Stale_if_match_gets_412()
    {
        var client = await _fixture.CreateAdminClientAsync();

        var create = await client.PostAsJsonAsync("/system/services", ValidCreateBody("etagged"));
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/system/services/{id}")
        {
            Content = JsonContent.Create(new { label = "New Label" }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"9999\"");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task Connectors_listing_returns_known_types()
    {
        var client = await _fixture.CreateAdminClientAsync();
        var connectors = await client.GetFromJsonAsync<JsonElement>("/system/connectors");

        var types = connectors.EnumerateArray().Select(c => c.GetProperty("type").GetString()).ToList();
        Assert.Equal(["postgresql", "mysql", "sqlserver", "sqlite"], types!);
    }
}
