using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace EzOdata.ConformanceTests;

[Collection("conformance")]
public class RateLimitAndAuditTests
{
    private readonly ConformanceFixture _fixture;

    public RateLimitAndAuditTests(ConformanceFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task App_scoped_rate_limit_returns_429_with_retry_after()
    {
        var admin = _fixture.Client;

        // Dedicated app + role so other tests' traffic doesn't interfere
        var roleResponse = await admin.PostAsJsonAsync("/system/roles", new
        {
            name = $"ratelimited-{Guid.NewGuid():N}",
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
                },
            },
        });
        var roleId = (await roleResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var appResponse = await admin.PostAsJsonAsync("/system/apps", new
        {
            name = $"ratelimited-app-{Guid.NewGuid():N}"[..30],
            roleId,
            isActive = true,
            requireUserSession = false,
            mcpEnabled = false,
        });
        var appId = (await appResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var limit = await admin.PostAsJsonAsync("/system/rate-limits", new
        {
            scopeType = "app", scopeId = appId, windowSeconds = 60, maxRequests = 3,
        });
        limit.EnsureSuccessStatusCode();

        var keyResponse = await admin.PostAsJsonAsync($"/system/apps/{appId}/keys", new { name = "rl" });
        var key = (await keyResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);

        // Wait out the policy provider cache so the new limit is live
        await Task.Delay(TimeSpan.FromSeconds(16));

        var url = $"/api/odata/{ConformanceFixture.ServiceName}/customers?$top=1";
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var limited = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Data_requests_and_logins_are_audited()
    {
        var admin = _fixture.Client;

        // Generate one data read + one failed login
        await admin.GetAsync($"/api/odata/{ConformanceFixture.ServiceName}/customers?$top=1");
        var anonymous = _fixture.CreateClient();
        await anonymous.PostAsJsonAsync("/system/auth/login", new { email = "ghost@x.co", password = "wrong-password-1" });

        // The audit pipeline flushes every ≤2 s
        await Task.Delay(TimeSpan.FromSeconds(3));

        var audit = await admin.GetFromJsonAsync<JsonElement>("/system/audit?limit=200");
        var events = audit.GetProperty("resource").EnumerateArray().ToList();

        Assert.Contains(events, e =>
            e.GetProperty("category").GetString() == "data.read"
            && e.GetProperty("outcome").GetString() == "ok"
            && e.GetProperty("resource").GetString()!.Contains("/customers"));

        Assert.Contains(events, e =>
            e.GetProperty("category").GetString() == "auth"
            && e.GetProperty("action").GetString() == "login.failed"
            && e.GetProperty("outcome").GetString() == "denied");
    }

    [Fact]
    public async Task Role_simulator_reports_decision_without_querying_data()
    {
        var admin = _fixture.Client;

        var roles = await admin.GetFromJsonAsync<JsonElement>("/system/roles");
        var analyst = roles.EnumerateArray().FirstOrDefault(r => r.GetProperty("name").GetString() == "us-analyst");
        if (analyst.ValueKind != JsonValueKind.Object)
        {
            return; // analyst role created by SecurityConformanceTests; skip standalone runs
        }

        var roleId = analyst.GetProperty("id").GetInt64();

        var allowed = await admin.PostAsJsonAsync($"/system/roles/{roleId}/simulate", new
        {
            serviceName = ConformanceFixture.ServiceName, table = "customers", verb = "GET",
        });
        allowed.EnsureSuccessStatusCode();
        var verdict = await allowed.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(verdict.GetProperty("allowed").GetBoolean());
        Assert.Contains("ssn", verdict.GetProperty("deniedFields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal("(country eq 'US')", verdict.GetProperty("effectiveRowFilter").GetString());

        var denied = await admin.PostAsJsonAsync($"/system/roles/{roleId}/simulate", new
        {
            serviceName = ConformanceFixture.ServiceName, table = "products", verb = "GET",
        });
        var deniedVerdict = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(deniedVerdict.GetProperty("allowed").GetBoolean());
        Assert.True(deniedVerdict.GetProperty("hidden").GetBoolean());

        var writeVerdict = await (await admin.PostAsJsonAsync($"/system/roles/{roleId}/simulate", new
        {
            serviceName = ConformanceFixture.ServiceName, table = "customers", verb = "DELETE",
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(writeVerdict.GetProperty("allowed").GetBoolean());
        Assert.False(writeVerdict.GetProperty("hidden").GetBoolean());
    }
}
