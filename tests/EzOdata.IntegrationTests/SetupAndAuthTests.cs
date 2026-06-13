using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace EzOdata.IntegrationTests;

public class SetupAndAuthTests : IClassFixture<HostFixture>
{
    private readonly HostFixture _fixture;

    public SetupAndAuthTests(HostFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Fresh_instance_requires_setup_and_blocks_other_routes()
    {
        using var factory = new HostFixture(); // isolated instance: must observe pre-setup state
        var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<HostFixture.SetupStatusDto>("/system/setup");
        Assert.True(status!.Required);

        // Everything except setup + health is 503 in setup mode (spec 03 §4)
        var blocked = await client.GetAsync("/system/services");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);

        var health = await client.GetAsync("/healthz/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Setup_rejects_weak_password()
    {
        using var factory = new HostFixture();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/system/setup",
            new { email = "a@b.co", displayName = "A", password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Setup_can_complete_exactly_once()
    {
        using var factory = new HostFixture();
        var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync("/system/setup",
            new { email = "admin@example.com", displayName = "Admin", password = "a-strong-test-password-1" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/system/setup",
            new { email = "other@example.com", displayName = "Other", password = "a-strong-test-password-2" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Login_returns_tokens_and_me_works()
    {
        var client = await _fixture.CreateAdminClientAsync();

        var me = await client.GetFromJsonAsync<HostFixture.UserDto>("/system/auth/me");
        Assert.Equal("admin@example.com", me!.Email);
        Assert.True(me.IsSystemAdmin);
    }

    [Fact]
    public async Task Wrong_password_gets_uniform_401()
    {
        await _fixture.CreateAdminClientAsync();
        var anonymous = _fixture.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/system/auth/login",
            new { email = "admin@example.com", password = "wrong-password-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var unknownUser = await anonymous.PostAsJsonAsync("/system/auth/login",
            new { email = "ghost@example.com", password = "wrong-password-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);

        // Same status + same title: no user enumeration (spec 08 §3.3).
        // traceId legitimately differs per request, so compare semantic fields only.
        Assert.Equal(await Title(response), await Title(unknownUser));

        static async Task<string?> Title(HttpResponseMessage r)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("title").GetString();
        }
    }

    [Fact]
    public async Task Refresh_rotates_and_old_token_is_rejected_with_family_revocation()
    {
        using var factory = new HostFixture();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/system/setup",
            new { email = "admin@example.com", displayName = "Admin", password = "a-strong-test-password-1" });
        var login = await client.PostAsJsonAsync("/system/auth/login",
            new { email = "admin@example.com", password = "a-strong-test-password-1" });
        var auth = await login.Content.ReadFromJsonAsync<HostFixture.AuthDto>();

        // First refresh succeeds and rotates
        var refresh1 = await client.PostAsJsonAsync("/system/auth/refresh", new { refreshToken = auth!.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refresh1.StatusCode);
        var rotated = await refresh1.Content.ReadFromJsonAsync<HostFixture.AuthDto>();

        // Reusing the consumed token fails AND revokes the family (spec 08 §3.4)
        var reuse = await client.PostAsJsonAsync("/system/auth/refresh", new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        var afterTheft = await client.PostAsJsonAsync("/system/auth/refresh", new { refreshToken = rotated!.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterTheft.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_request_to_admin_api_is_401()
    {
        await _fixture.CreateAdminClientAsync(); // ensure setup complete
        var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync("/system/services");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
