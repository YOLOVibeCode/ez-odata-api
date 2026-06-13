using System.Security.Claims;
using System.Text.Encodings.Web;
using EzOdata.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EzOdata.Admin.Auth;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-API-Key";
    public const string QueryName = "api_key";
}

/// <summary>
/// API key authentication (spec 08 §3.5): SHA-256 lookup with a 60 s in-memory cache;
/// revocation/expiry honored; lastUsedAt updated lazily.
/// Claims carry app id + role id so the identity builder can construct RequestIdentity.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    public const string AppIdClaim = "ez:app-id";
    public const string RoleIdClaim = "ez:role-id";
    public const string KeyIdClaim = "ez:key-id";
    public const string McpEnabledClaim = "ez:mcp";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IMemoryCache _cache;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, IMemoryCache cache)
        : base(options, logger, encoder)
    {
        _cache = cache;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[ApiKeyAuthenticationOptions.HeaderName].ToString();
        if (string.IsNullOrEmpty(key))
        {
            key = Request.Query[ApiKeyAuthenticationOptions.QueryName].ToString();
            if (string.IsNullOrEmpty(key))
            {
                return AuthenticateResult.NoResult();
            }
        }

        var hash = AuthService.Sha256(key);
        var cacheKey = "ez:apikey:" + hash;

        if (!_cache.TryGetValue(cacheKey, out KeySnapshot? snapshot))
        {
            snapshot = await LookupAsync(hash);
            _cache.Set(cacheKey, snapshot, CacheTtl);
        }

        if (snapshot is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        if (snapshot.ExpiresAt is { } expires && expires <= TimeProvider.GetUtcNow())
        {
            return AuthenticateResult.Fail("API key expired.");
        }

        var claims = new List<Claim>
        {
            new(AppIdClaim, snapshot.AppId.ToString()),
            new(RoleIdClaim, snapshot.RoleId.ToString()),
            new(KeyIdClaim, snapshot.KeyId.ToString()),
            new(McpEnabledClaim, snapshot.McpEnabled ? "true" : "false"),
            new("ez:app-name", snapshot.AppName),
        };
        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.Scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyAuthenticationOptions.Scheme);
        return AuthenticateResult.Success(ticket);
    }

    private async Task<KeySnapshot?> LookupAsync(string hash)
    {
        using var scope = Context.RequestServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();

        var row = await db.ApiKeys
            .AsNoTracking()
            .Where(k => k.KeyHash == hash && k.RevokedAt == null)
            .Select(k => new { k.Id, k.AppId, k.ExpiresAt, App = k.App! })
            .FirstOrDefaultAsync(Context.RequestAborted);

        if (row is null || !row.App.IsActive)
        {
            return null;
        }

        // lastUsedAt updated lazily, ≥1 min granularity (spec 03 §2.9) — fire and forget.
        _ = UpdateLastUsedAsync(row.Id);

        return new KeySnapshot(row.Id, row.AppId, row.App.RoleId, row.App.Name, row.App.McpEnabled, row.ExpiresAt);
    }

    private async Task UpdateLastUsedAsync(long keyId)
    {
        try
        {
            using var scope = Context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
            await db.ApiKeys
                .Where(k => k.Id == keyId &&
                            (k.LastUsedAt == null || k.LastUsedAt < DateTimeOffset.UtcNow.AddMinutes(-1)))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, DateTimeOffset.UtcNow));
        }
        catch
        {
            // best effort only
        }
    }

    private sealed record KeySnapshot(long KeyId, long AppId, long RoleId, string AppName, bool McpEnabled, DateTimeOffset? ExpiresAt);
}
