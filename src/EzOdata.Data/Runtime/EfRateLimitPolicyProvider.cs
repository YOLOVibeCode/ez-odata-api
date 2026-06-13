using EzOdata.Core.Policy;
using EzOdata.Core.RateLimiting;
using EzOdata.Core.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EzOdata.Data.Runtime;

/// <summary>
/// Resolves the rate-limit policies applicable to an identity (spec 08 §6):
/// app + user + role + instance scopes are ALL enforced simultaneously.
/// Policies cached briefly; per-bucket state lives in the limiter.
/// </summary>
public sealed class EfRateLimitPolicyProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemClock _clock;
    private (List<PolicyRow> Rows, DateTimeOffset At)? _cache;

    public EfRateLimitPolicyProvider(IServiceScopeFactory scopes, ISystemClock clock)
    {
        _scopes = scopes;
        _clock = clock;
    }

    public async Task<IReadOnlyList<RateLimitPolicy>> ResolveAsync(RequestIdentity identity, CancellationToken ct)
    {
        var rows = await LoadAsync(ct);
        var applicable = new List<RateLimitPolicy>();

        foreach (var row in rows)
        {
            var scopeKey = row.ScopeType switch
            {
                "instance" => $"instance:{row.Id}",
                "app" when identity.AppId == row.ScopeId => $"app:{row.ScopeId}:{row.Id}",
                "user" when identity.UserId == row.ScopeId => $"user:{row.ScopeId}:{row.Id}",
                "role" when row.ScopeId is { } rid && identity.RoleIds.Contains(rid) => $"role:{rid}:{row.Id}",
                _ => null,
            };

            if (scopeKey is not null)
            {
                applicable.Add(new RateLimitPolicy(scopeKey, row.WindowSeconds, row.MaxRequests));
            }
        }

        return applicable;
    }

    public void Invalidate() => _cache = null;

    private async Task<List<PolicyRow>> LoadAsync(CancellationToken ct)
    {
        if (_cache is { } cached && _clock.UtcNow - cached.At < CacheTtl)
        {
            return cached.Rows;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
        var rows = await db.RateLimitPolicies
            .AsNoTracking()
            .Select(p => new PolicyRow(p.Id, p.ScopeType, p.ScopeId, p.WindowSeconds, p.MaxRequests))
            .ToListAsync(ct);

        _cache = (rows, _clock.UtcNow);
        return rows;
    }

    internal sealed record PolicyRow(long Id, string ScopeType, long? ScopeId, int WindowSeconds, int MaxRequests);
}
