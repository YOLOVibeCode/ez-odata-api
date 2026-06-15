using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EzOdata.IntegrationTests;

/// <summary>
/// Standalone server: Auth:DevNoAuth in Development allows unauthenticated data requests;
/// setting the flag outside Development causes the host to refuse to start.
/// </summary>
public class StandaloneDevBypassTests
{
    [Fact]
    public async Task DevNoAuth_in_Development_lets_anonymous_request_access_data()
    {
        using var factory = new DevBypassHostFixture("Development");
        var client = factory.CreateClient();

        // Complete setup so the service-gate middleware passes.
        var setup = await client.PostAsJsonAsync("/system/setup",
            new { email = "dev@example.com", displayName = "Dev", password = "dev-password-bypass-1" });
        setup.EnsureSuccessStatusCode();

        // Anonymous data request — no Authorization header
        // Returns 404 (no service configured yet) rather than 401, proving the bypass let it through.
        var response = await client.GetAsync("/api/odata/nonexistent/customers");
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        // Confirm it got through auth (engine gave a 404 for unknown service, not auth rejection)
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DevNoAuth_in_Production_throws_at_startup()
    {
        // The host throws during startup configuration; the exact exception type varies by
        // how the TestServer boots, so we catch InvalidOperationException or AggregateException.
        Exception? captured = null;
        try
        {
            using var factory = new DevBypassHostFixture("Production");
            _ = factory.CreateClient();
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        Assert.NotNull(captured);
        Assert.Contains("DevNoAuth", captured!.ToString());
    }
}

internal sealed class DevBypassHostFixture : WebApplicationFactory<Program>
{
    private readonly string _env;
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"ez-devbypass-{Guid.NewGuid():N}.db");

    public DevBypassHostFixture(string environment) => _env = environment;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_env);
        builder.UseSetting("SystemDatabase:Provider", "sqlite");
        builder.UseSetting("SystemDatabase:ConnectionString", $"Data Source={_db}");
        builder.UseSetting("Auth:Jwt:SigningKey", "devbypass-test-signing-key-32-chars!!");
        builder.UseSetting("Encryption:MasterKey", "ZGV2YnlwYXNzLXRlc3QtbWFzdGVyLWtleS0zMmJ5dGU="); // exactly 32 bytes
        builder.UseSetting("Auth:DevNoAuth", "true");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (File.Exists(_db)) File.Delete(_db);
    }
}
