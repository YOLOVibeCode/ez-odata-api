using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EzOdata.IntegrationTests;

/// <summary>
/// Boots the real Host against a fresh SQLite system DB per fixture instance.
/// Tests within a class share state intentionally (xUnit class fixture semantics).
/// </summary>
public sealed class HostFixture : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ez-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("SystemDatabase:Provider", "sqlite");
        builder.UseSetting("SystemDatabase:ConnectionString", $"Data Source={_dbPath}");
        builder.UseSetting("Auth:Jwt:SigningKey", "integration-test-signing-key-32-chars!!");
        builder.UseSetting("Encryption:MasterKey", Convert.ToBase64String("integration-test-master-key-32b!"u8.ToArray()));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    public async Task<HttpClient> CreateAdminClientAsync(
        string email = "admin@example.com", string password = "a-strong-test-password-1")
    {
        var client = CreateClient();

        // Idempotent: complete setup if needed, then log in.
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

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    public sealed record SetupStatusDto(bool Required);

    public sealed record AuthDto(string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken, UserDto User);

    public sealed record UserDto(long Id, string Email, string DisplayName, bool IsSystemAdmin, string[] Roles);
}
