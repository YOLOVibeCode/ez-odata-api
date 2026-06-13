using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using EzOdata.AspNetCore;
using EzOdata.AspNetCore.Embedded;
using EzOdata.Embedded;
using EzOdata.Core.Policy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EzOdata.IntegrationTests;

/// <summary>
/// Embedded-host fixture (spec 15 §6): a minimal ASP.NET Core app with NO system database,
/// code-declared service + roles, proving the engine runs as a bolt-on with the host-auth bridge.
/// </summary>
public class EmbeddedHostTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ez-embed-{Guid.NewGuid():N}.db");
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE widgets (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, secret TEXT, region TEXT);
                INSERT INTO widgets (name, secret, region) VALUES
                  ('Alpha','s1','US'), ('Beta','s2','US'), ('Gamma','s3','EU');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization();
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });

                    services.AddEzOData(ez =>
                    {
                        ez.AddService("inventory", s => s.UseSqlite(_dbPath).Options(o => o.DefaultPageSize(50)));
                        ez.AddRole("reader", r => r
                            .Allow("inventory", "*", Verb.Get,
                                rowFilter: "region eq 'US'",
                                fieldRules: [new FieldRule("secret", FieldAction.Deny, null)]));
                        ez.ResolveRolesBy(principal =>
                            principal.IsInRole("reader") ? ["reader"] : []);
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapEzOData("/api/odata"));
                });
            })
            .StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Embedded_engine_serves_metadata_and_governed_data()
    {
        // metadata works without a system DB
        var metadata = await _client.GetStringAsync("/api/odata/inventory/$metadata");
        Assert.Contains("EntityType Name=\"widgets\"", metadata);
        Assert.DoesNotContain("secret", metadata); // denied field trimmed

        // row filter + field policy enforced through the host-auth bridge
        var page = await _client.GetFromJsonAsync<JsonElement>("/api/odata/inventory/widgets?$count=true");
        Assert.Equal(2, page.GetProperty("@odata.count").GetInt64()); // US only
        foreach (var row in page.GetProperty("value").EnumerateArray())
        {
            Assert.False(row.TryGetProperty("secret", out _));
            Assert.Equal("US", row.GetProperty("region").GetString());
        }
    }

    [Fact]
    public async Task Embedded_denied_field_in_filter_is_403()
    {
        var response = await _client.GetAsync("/api/odata/inventory/widgets?$filter=secret eq 's1'");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Test auth handler that grants the 'reader' role to every request.</summary>
    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> o,
            Microsoft.Extensions.Logging.ILoggerFactory l, System.Text.Encodings.Web.UrlEncoder e) : base(o, l, e) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, "reader"), new Claim("sub", "42")], "Test");
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
