using System.Net;
using System.Net.Http.Json;
using EzOdata.Host.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EzOdata.IntegrationTests;

/// <summary>spec 08 §7 — default-deny CORS with per-app origin whitelists.</summary>
public class CorsTests : IClassFixture<CorsTests.CorsHostFixture>
{
    private readonly CorsHostFixture _fixture;

    public CorsTests(CorsHostFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Standalone host fixture — HostFixture is sealed, and this one additionally needs
    /// an instance-level default origin configured before the host boots.
    /// </summary>
    public sealed class CorsHostFixture : WebApplicationFactory<Program>
    {
        public const string DefaultOrigin = "http://localhost:8081";

        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ez-cors-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("SystemDatabase:Provider", "sqlite");
            builder.UseSetting("SystemDatabase:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Auth:Jwt:SigningKey", "integration-test-signing-key-32-chars!!");
            builder.UseSetting("Encryption:MasterKey",
                Convert.ToBase64String("integration-test-master-key-32b!"u8.ToArray()));
            builder.UseSetting("Cors:DefaultAllowedOrigins:0", DefaultOrigin);
        }

        public async Task<HttpClient> CreateAdminClientAsync()
        {
            const string email = "admin@example.com";
            const string password = "a-strong-test-password-1";
            var client = CreateClient();

            var status = await client.GetFromJsonAsync<SetupStatusDto>("/system/setup");
            if (status!.Required)
            {
                var setup = await client.PostAsJsonAsync("/system/setup",
                    new { email, displayName = "Test Admin", password });
                setup.EnsureSuccessStatusCode();
            }

            var login = await client.PostAsJsonAsync("/system/auth/login", new { email, password });
            login.EnsureSuccessStatusCode();
            var auth = await login.Content.ReadFromJsonAsync<AuthDto>();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth!.AccessToken);
            return client;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }

        private sealed record SetupStatusDto(bool Required);

        private sealed record AuthDto(string AccessToken);
    }

    private static HttpRequestMessage Preflight(string origin, string path = "/api/odata/x/customers")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return request;
    }

    [Fact]
    public async Task Preflight_from_default_origin_is_allowed_while_anonymous()
    {
        var client = _fixture.CreateClient();

        var response = await client.SendAsync(Preflight(CorsHostFixture.DefaultOrigin));

        // Must not be 401: a browser preflight never carries credentials.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(CorsHostFixture.DefaultOrigin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("OData-Version",
            Assert.Single(response.Headers.GetValues("Access-Control-Expose-Headers")));
    }

    [Fact]
    public async Task Preflight_from_unknown_origin_gets_no_cors_headers()
    {
        var client = _fixture.CreateClient();

        var response = await client.SendAsync(Preflight("https://evil.example"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Request_without_origin_is_untouched()
    {
        var client = _fixture.CreateClient();

        var response = await client.GetAsync("/healthz/live");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Per_app_origin_is_honored_for_keyed_request()
    {
        var admin = await _fixture.CreateAdminClientAsync();
        var roles = await admin.GetFromJsonAsync<List<RoleDto>>("/system/roles");
        var created = await admin.PostAsJsonAsync("/system/apps", new
        {
            name = "cors-app",
            roleId = roles![0].Id,
            isActive = true,
            allowedOrigins = new[] { "https://*.example.com" },
        });
        created.EnsureSuccessStatusCode();
        var app = await created.Content.ReadFromJsonAsync<AppDto>();
        var keyResponse = await admin.PostAsJsonAsync($"/system/apps/{app!.Id}/keys", new { name = "k" });
        var key = (await keyResponse.Content.ReadFromJsonAsync<KeyDto>())!.Key;

        var client = _fixture.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/odata/x/customers");
        request.Headers.Add("Origin", "https://tenant.example.com");
        request.Headers.Add("X-API-Key", key);

        var response = await client.SendAsync(request);

        Assert.Equal("https://tenant.example.com",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Theory]
    // exact
    [InlineData("https://app.example.com", "https://app.example.com", true)]
    [InlineData("https://APP.example.com", "https://app.example.com", true)] // origin compare is case-insensitive
    [InlineData("https://other.example.com", "https://app.example.com", false)]
    // single-label wildcard
    [InlineData("https://*.example.com", "https://tenant.example.com", true)]
    [InlineData("https://*.example.com", "https://a.b.example.com", false)]  // wildcard spans one label only
    [InlineData("https://*.example.com", "http://tenant.example.com", false)] // scheme must match
    [InlineData("https://*.example.com", "https://example.com", false)]       // wildcard needs a label
    [InlineData("https://*.example.com", "https://evilexample.com", false)]   // suffix forgery
    [InlineData("https://*.example.com", "https://evil.com/?x=.example.com", false)]
    public void Matches_follows_spec_08_section_7(string allowed, string origin, bool expected)
        => Assert.Equal(expected, DynamicCorsMiddleware.Matches(origin, new[] { allowed }));

    [Fact]
    public void Empty_allow_list_denies_everything()
        => Assert.False(DynamicCorsMiddleware.Matches("https://app.example.com", Array.Empty<string>()));

    private sealed record RoleDto(long Id);
    private sealed record AppDto(long Id);
    private sealed record KeyDto(string Key);
}
