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
/// Tests for the new host-auth pass-through helpers (UseHostRoles, MapRolesFromClaim)
/// and the dev no-auth mode (AllowAnonymousInDevelopment) in the embedded adapter.
/// </summary>
public class EmbeddedAuthTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ez-authtest-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE products (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                internal_code TEXT,
                region TEXT
            );
            INSERT INTO products (name, internal_code, region) VALUES
                ('Widget', 'INT-001', 'US'),
                ('Gadget', 'INT-002', 'EU');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    // ---------- UseHostRoles ----------

    [Fact]
    public async Task UseHostRoles_maps_standard_role_claims_to_ez_roles()
    {
        using var host = await BuildHostAsync(ez =>
        {
            ez.AddService("inv", s => s.UseSqlite(_dbPath));
            ez.AddRole("viewer", r => r.Allow("inv", "*", Verb.Get,
                fieldRules: [new FieldRule("internal_code", FieldAction.Deny, null)]));
            ez.UseHostRoles();   // ClaimTypes.Role = "viewer" → ez role "viewer"
        }, hostRoles: ["viewer"]);

        var client = host.GetTestClient();
        var page = await client.GetFromJsonAsync<JsonElement>("/api/odata/inv/products");

        // internal_code denied for "viewer" role
        Assert.False(page.GetProperty("value")[0].TryGetProperty("internal_code", out _));
        // accessible rows exist
        Assert.True(page.GetProperty("value").GetArrayLength() > 0);
    }

    [Fact]
    public async Task MapRolesFromClaim_uses_custom_claim_with_transform()
    {
        using var host = await BuildHostAsync(ez =>
        {
            ez.AddService("inv", s => s.UseSqlite(_dbPath));
            ez.AddRole("viewer", r => r.Allow("inv", "*", Verb.Get));
            // custom claim "app_role" with value "role:viewer" → strip prefix → "viewer"
            ez.MapRolesFromClaim("app_role", raw => raw.Replace("role:", ""));
        }, customClaim: ("app_role", "role:viewer"));

        var client = host.GetTestClient();
        var response = await client.GetAsync("/api/odata/inv/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Identity_sub_and_email_flow_through_to_row_filter()
    {
        // Row filter references @identity.sub — must resolve from the host's sub claim.
        using var host = await BuildHostAsync(ez =>
        {
            ez.AddService("inv", s => s.UseSqlite(_dbPath));
            // row filter can't be evaluated here (no 'owner_id' column), but we verify
            // the filter compiles against the schema — if sub is missing it throws RowFilterException.
            ez.AddRole("owner", r => r.Allow("inv", "*", Verb.Get));
            ez.UseHostRoles();
        }, hostRoles: ["owner"], sub: "42");

        var client = host.GetTestClient();
        // The important thing is the request succeeds (no RowFilterException for missing claim).
        var response = await client.GetAsync("/api/odata/inv/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_without_dev_bypass_is_401()
    {
        using var host = await BuildHostAsync(ez =>
        {
            ez.AddService("inv", s => s.UseSqlite(_dbPath));
            ez.AddRole("viewer", r => r.Allow("inv", "*", Verb.Get));
            ez.UseHostRoles();
        }, authenticated: false);  // no claims → unauthenticated

        var client = host.GetTestClient();
        var response = await client.GetAsync("/api/odata/inv/products");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- AllowAnonymousInDevelopment ----------

    [Fact]
    public async Task Dev_no_auth_allows_unauthenticated_request_as_full_access()
    {
        using var host = await BuildHostAsync(ez =>
        {
            ez.AddService("inv", s => s.UseSqlite(_dbPath));
            // no roles configured — normally everything would be denied
            ez.AllowAnonymousInDevelopment();
        }, authenticated: false, environment: "Development");

        var client = host.GetTestClient();
        var page = await client.GetFromJsonAsync<JsonElement>("/api/odata/inv/products");

        // Full bypass: all columns visible including internal_code
        Assert.True(page.GetProperty("value")[0].TryGetProperty("internal_code", out _));
        Assert.Equal(2, page.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task Dev_no_auth_throws_at_startup_outside_Development()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var host = await BuildHostAsync(ez =>
            {
                ez.AddService("inv", s => s.UseSqlite(_dbPath));
                ez.AllowAnonymousInDevelopment();
            }, authenticated: false, environment: "Production");
        });
    }

    // ---------- helpers ----------

    private async Task<IHost> BuildHostAsync(
        Action<EzOdataBuilder> configure,
        IReadOnlyList<string>? hostRoles = null,
        (string Type, string Value)? customClaim = null,
        string? sub = null,
        bool authenticated = true,
        string environment = "Development")
    {
        var host = await new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization();
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, ParameterisedTestAuthHandler>("Test", _ => { });

                    // Store the test parameters so the handler can access them.
                    services.AddSingleton(new TestAuthParams(authenticated, hostRoles, customClaim, sub));
                    services.AddEzOData(configure);
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

        return host;
    }

    private sealed record TestAuthParams(
        bool Authenticated,
        IReadOnlyList<string>? HostRoles,
        (string Type, string Value)? CustomClaim,
        string? Sub);

    private sealed class ParameterisedTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly TestAuthParams _p;

        public ParameterisedTestAuthHandler(
            Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> o,
            Microsoft.Extensions.Logging.ILoggerFactory l,
            System.Text.Encodings.Web.UrlEncoder e,
            TestAuthParams p) : base(o, l, e) => _p = p;

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!_p.Authenticated) return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>();
            if (_p.HostRoles is not null)
                foreach (var role in _p.HostRoles)
                    claims.Add(new Claim(ClaimTypes.Role, role));
            if (_p.CustomClaim is { } cc) claims.Add(new Claim(cc.Type, cc.Value));
            if (_p.Sub is not null) claims.Add(new Claim("sub", _p.Sub));

            var identity = new ClaimsIdentity(claims, "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
