using System.Text.Json;
using EzOdata.Admin.Auth;
using EzOdata.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace EzOdata.Host.Middleware;

/// <summary>
/// Default-deny CORS (spec 08 §7). The allowed-origin list is dynamic — it comes from the
/// App row behind the presented API key — so this replaces the static UseCors policy.
/// Preflight carries no custom headers, so an unkeyed request falls back to the
/// instance-level <c>Cors:DefaultAllowedOrigins</c> list.
/// Runs before authentication: a preflight must succeed while still anonymous.
/// </summary>
public sealed class DynamicCorsMiddleware
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    // OData + RateLimit headers must be readable by browser clients (spec 08 §7).
    private const string ExposedHeaders =
        "OData-Version,RateLimit-Limit,RateLimit-Remaining,RateLimit-Reset,Retry-After,X-Request-Id";

    private const string AllowedMethods = "GET,POST,PATCH,PUT,DELETE,OPTIONS";
    private const string FallbackAllowedHeaders = "Authorization,Content-Type,X-API-Key,Prefer";

    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CorsOptions _options;

    public DynamicCorsMiddleware(
        RequestDelegate next, IMemoryCache cache, IServiceScopeFactory scopeFactory, IOptions<CorsOptions> options)
    {
        _next = next;
        _cache = cache;
        _scopeFactory = scopeFactory;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            await _next(context); // same-origin or non-browser: nothing to negotiate
            return;
        }

        var allowed = await ResolveAllowedOriginsAsync(context);
        var isPreflight = HttpMethods.IsOptions(context.Request.Method) &&
                          !string.IsNullOrEmpty(context.Request.Headers.AccessControlRequestMethod);

        if (!Matches(origin, allowed))
        {
            // Default deny: emit no CORS headers. The browser blocks it; a preflight
            // is still answered 204 so the failure surfaces as CORS, not as a 401.
            if (isPreflight)
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await _next(context);
            return;
        }

        var headers = context.Response.Headers;
        headers.AccessControlAllowOrigin = origin;
        headers.Append("Vary", "Origin");
        headers.AccessControlExposeHeaders = ExposedHeaders;

        if (isPreflight)
        {
            var requested = context.Request.Headers.AccessControlRequestHeaders.ToString();
            headers.AccessControlAllowMethods = AllowedMethods;
            headers.AccessControlAllowHeaders =
                string.IsNullOrEmpty(requested) ? FallbackAllowedHeaders : requested;
            headers.AccessControlMaxAge = "600";
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return; // never let a preflight reach authentication
        }

        await _next(context);
    }

    private async Task<IReadOnlyList<string>> ResolveAllowedOriginsAsync(HttpContext context)
    {
        var key = context.Request.Headers[ApiKeyAuthenticationOptions.HeaderName].ToString();
        if (string.IsNullOrEmpty(key))
        {
            key = context.Request.Query[ApiKeyAuthenticationOptions.QueryName].ToString();
        }

        if (string.IsNullOrEmpty(key))
        {
            return _options.DefaultAllowedOrigins;
        }

        var hash = AuthService.Sha256(key);
        var cacheKey = "ez:cors:" + hash;
        if (_cache.TryGetValue(cacheKey, out string[]? cached) && cached is not null)
        {
            return cached;
        }

        var origins = await LookupAsync(hash, context.RequestAborted);
        _cache.Set(cacheKey, origins, CacheTtl);
        return origins;
    }

    private async Task<string[]> LookupAsync(string hash, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();

        var json = await db.ApiKeys
            .AsNoTracking()
            .Where(k => k.KeyHash == hash && k.RevokedAt == null && k.App!.IsActive)
            .Select(k => k.App!.AllowedOriginsJson)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(json))
        {
            // Known key with no per-app list, or an unknown key: fall back to the
            // instance default rather than leaking which keys exist.
            return _options.DefaultAllowedOrigins;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? _options.DefaultAllowedOrigins;
        }
        catch (JsonException)
        {
            return _options.DefaultAllowedOrigins;
        }
    }

    /// <summary>Exact match, or a single leading wildcard label (<c>https://*.example.com</c>).</summary>
    public static bool Matches(string origin, IReadOnlyList<string> allowed)
    {
        foreach (var candidate in allowed)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (string.Equals(candidate, origin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var star = candidate.IndexOf("://*.", StringComparison.Ordinal);
            if (star < 0)
            {
                continue;
            }

            // "https://*.example.com" -> scheme "https://", suffix ".example.com"
            var scheme = candidate[..(star + 3)];
            var suffix = candidate[(star + 4)..];
            if (!origin.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ||
                !origin.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Require a non-empty label in place of the wildcard, and no extra dots in it.
            var label = origin[scheme.Length..^suffix.Length];
            if (label.Length > 0 && !label.Contains('.'))
            {
                return true;
            }
        }

        return false;
    }
}
