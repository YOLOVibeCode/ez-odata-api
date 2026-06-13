using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace EzOdata.ConformanceTests;

/// <summary>
/// The spec 08 §11 leak matrix executed over HTTP with a real restricted API key:
/// role = GET on customers/orders only, ssn denied, email masked,
/// customers row-filtered to country eq 'US'.
/// </summary>
[Collection("conformance")]
public class SecurityConformanceTests : IAsyncLifetime
{
    private readonly ConformanceFixture _fixture;
    private HttpClient _keyClient = null!;
    private string _apiKey = "";
    private long _appId;
    private long _keyId;

    public SecurityConformanceTests(ConformanceFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        var admin = _fixture.Client;

        // Role: restricted analyst (idempotent across test runs within the fixture)
        var roles = await admin.GetFromJsonAsync<JsonElement>("/system/roles");
        var existing = roles.EnumerateArray().FirstOrDefault(r => r.GetProperty("name").GetString() == "us-analyst");

        long roleId;
        if (existing.ValueKind == JsonValueKind.Object)
        {
            roleId = existing.GetProperty("id").GetInt64();
        }
        else
        {
            var roleResponse = await admin.PostAsJsonAsync("/system/roles", new
            {
                name = "us-analyst",
                description = "US customers only, no PII",
                isActive = true,
                access = new object[]
                {
                    new
                    {
                        serviceName = ConformanceFixture.ServiceName,
                        resourcePattern = "customers",
                        verbs = new[] { "GET" },
                        effect = "allow",
                        priority = 0,
                        rowFilter = "country eq 'US'",
                        fieldPolicies = new object[]
                        {
                            new { fieldPattern = "ssn", action = "deny", maskValue = (string?)null },
                            new { fieldPattern = "email", action = "mask", maskValue = "***@***" },
                        },
                    },
                    new
                    {
                        serviceName = ConformanceFixture.ServiceName,
                        resourcePattern = "orders",
                        verbs = new[] { "GET" },
                        effect = "allow",
                        priority = 0,
                        rowFilter = (string?)null,
                        fieldPolicies = Array.Empty<object>(),
                    },
                },
            });
            roleResponse.EnsureSuccessStatusCode();
            roleId = (await roleResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        }

        // App + key
        var apps = await admin.GetFromJsonAsync<JsonElement>("/system/apps");
        var existingApp = apps.EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == "analyst-app");
        if (existingApp.ValueKind == JsonValueKind.Object)
        {
            _appId = existingApp.GetProperty("id").GetInt64();
        }
        else
        {
            var appResponse = await admin.PostAsJsonAsync("/system/apps", new
            {
                name = "analyst-app",
                roleId,
                isActive = true,
                requireUserSession = false,
                mcpEnabled = true,
            });
            appResponse.EnsureSuccessStatusCode();
            _appId = (await appResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        }

        var keyResponse = await admin.PostAsJsonAsync($"/system/apps/{_appId}/keys", new { name = "test" });
        keyResponse.EnsureSuccessStatusCode();
        var created = await keyResponse.Content.ReadFromJsonAsync<JsonElement>();
        _apiKey = created.GetProperty("key").GetString()!;
        _keyId = created.GetProperty("id").GetInt64();

        _keyClient = _fixture.CreateClient();
        _keyClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private string Url(string rest) => $"/api/odata/{ConformanceFixture.ServiceName}{rest}";

    [Fact]
    public async Task Anonymous_data_request_is_401()
    {
        var anonymous = _fixture.CreateClient();
        var response = await anonymous.GetAsync(Url("/customers"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Row_filter_limits_collection_to_us_customers()
    {
        var page = await _keyClient.GetFromJsonAsync<JsonElement>(Url("/customers?$count=true"));

        Assert.Equal(5, page.GetProperty("@odata.count").GetInt64()); // 5 US rows of 8
        foreach (var row in page.GetProperty("value").EnumerateArray())
        {
            Assert.Equal("US", row.GetProperty("country").GetString());
        }
    }

    [Fact]
    public async Task Denied_field_absent_and_masked_field_masked_in_rows()
    {
        var page = await _keyClient.GetFromJsonAsync<JsonElement>(Url("/customers?$top=1"));
        var row = page.GetProperty("value")[0];

        Assert.False(row.TryGetProperty("ssn", out _), "ssn must never appear");
        Assert.Equal("***@***", row.GetProperty("email").GetString());
    }

    [Theory]
    [InlineData("/customers?$select=ssn")]
    [InlineData("/customers?$filter=ssn eq '111-22-3333'")]
    [InlineData("/customers?$orderby=ssn")]
    [InlineData("/customers?$filter=contains(ssn,'111')")]
    [InlineData("/customers?$filter=email eq 'ops@acme.example'")]
    public async Task Restricted_field_paths_are_403(string path)
    {
        var response = await _keyClient.GetAsync(Url(path));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Forbidden.FieldDenied", body);
        Assert.DoesNotContain("111-22-3333", body);
    }

    [Fact]
    public async Task Cross_table_filter_via_navigation_to_denied_field_is_403()
    {
        var response = await _keyClient.GetAsync(Url("/orders?$filter=customer/ssn ne null"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Expand_to_ungranted_table_is_403_not_silent()
    {
        // analyst can read orders but the order_items table is not granted
        var response = await _keyClient.GetAsync(Url("/orders?$expand=order_items"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Expand_carries_field_and_row_policy_to_children()
    {
        // orders → customer expansion must mask email and hide ssn, and only US customers appear
        var page = await _keyClient.GetFromJsonAsync<JsonElement>(Url("/orders?$expand=customer&$top=20"));
        foreach (var order in page.GetProperty("value").EnumerateArray())
        {
            if (order.TryGetProperty("customer", out var customer) && customer.ValueKind == JsonValueKind.Object)
            {
                Assert.False(customer.TryGetProperty("ssn", out _));
                Assert.Equal("***@***", customer.GetProperty("email").GetString());
                Assert.Equal("US", customer.GetProperty("country").GetString());
            }
        }
    }

    [Fact]
    public async Task Out_of_scope_row_is_404_by_id_probing_not_403()
    {
        // Customer 2 (Globex, DE) exists but is outside the row filter
        var response = await _keyClient.GetAsync(Url("/customers(2)"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // In-scope row works
        var ok = await _keyClient.GetAsync(Url("/customers(1)"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Ungranted_table_is_invisible_404()
    {
        var response = await _keyClient.GetAsync(Url("/products"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Metadata_and_service_document_are_identity_trimmed()
    {
        var metadata = await _keyClient.GetStringAsync(Url("/$metadata"));
        Assert.DoesNotContain("products", metadata);
        Assert.DoesNotContain("ssn", metadata);
        Assert.Contains("customers", metadata);
        Assert.Contains("email", metadata); // masked fields stay in metadata

        var serviceDoc = await _keyClient.GetStringAsync(Url("/"));
        Assert.DoesNotContain("products", serviceDoc);
        Assert.Contains("orders", serviceDoc);
    }

    [Fact]
    public async Task Admin_api_rejects_data_plane_keys()
    {
        var response = await _keyClient.GetAsync("/system/services");
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Revoked_key_stops_working()
    {
        // Issue a dedicated key, use it, revoke it, use it again.
        var keyResponse = await _fixture.Client.PostAsJsonAsync($"/system/apps/{_appId}/keys", new { name = "revocable" });
        var created = await keyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var key = created.GetProperty("key").GetString()!;
        var keyId = created.GetProperty("id").GetInt64();

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url("/customers?$top=1"))).StatusCode);

        var revoke = await _fixture.Client.DeleteAsync($"/system/apps/{_appId}/keys/{keyId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var after = await client.GetAsync(Url("/customers?$top=1"));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }
}
