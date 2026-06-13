using System.Collections.Concurrent;
using EzOdata.Core.Policy;
using EzOdata.Core.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EzOdata.Data.Runtime;

/// <summary>
/// EF-backed rule retrieval with a short cache (policy edits visible within TTL;
/// pub/sub invalidation arrives with Redis in Phase 8).
/// </summary>
public sealed class EfPolicySource : IPolicySource
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemClock _clock;
    private readonly ConcurrentDictionary<long, (RoleRuleSet? Rules, DateTimeOffset At)> _cache = new();
    private (string Version, DateTimeOffset At)? _versionCache;

    public EfPolicySource(IServiceScopeFactory scopes, ISystemClock clock)
    {
        _scopes = scopes;
        _clock = clock;
    }

    public async Task<IReadOnlyList<RoleRuleSet>> GetRoleRulesAsync(IReadOnlyList<long> roleIds, CancellationToken ct)
    {
        var result = new List<RoleRuleSet>(roleIds.Count);
        var missing = new List<long>();

        foreach (var roleId in roleIds)
        {
            if (_cache.TryGetValue(roleId, out var cached) && _clock.UtcNow - cached.At < CacheTtl)
            {
                if (cached.Rules is not null) result.Add(cached.Rules);
            }
            else
            {
                missing.Add(roleId);
            }
        }

        if (missing.Count > 0)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();

            var roles = await db.Roles
                .AsNoTracking()
                .Include(r => r.Access).ThenInclude(a => a.FieldPolicies)
                .Where(r => missing.Contains(r.Id))
                .ToListAsync(ct);

            var serviceNames = await db.Services
                .AsNoTracking()
                .Select(s => new { s.Id, s.Name })
                .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

            foreach (var roleId in missing)
            {
                var role = roles.FirstOrDefault(r => r.Id == roleId && r.IsActive);
                RoleRuleSet? ruleSet = null;
                if (role is not null)
                {
                    ruleSet = RoleRuleMapper.ToRuleSet(role, serviceNames);
                    result.Add(ruleSet);
                }

                _cache[roleId] = (ruleSet, _clock.UtcNow);
            }
        }

        return result;
    }

    public async Task<string> GetPolicyVersionAsync(CancellationToken ct)
    {
        if (_versionCache is { } cached && _clock.UtcNow - cached.At < CacheTtl)
        {
            return cached.Version;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();

        // Cheap monotonic fingerprint: count + max row version over roles
        var stats = await db.Roles
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), MaxVersion = g.Max(r => r.RowVersion), MaxId = g.Max(r => r.Id) })
            .FirstOrDefaultAsync(ct);
        var accessCount = await db.RoleServiceAccess.CountAsync(ct);
        var fieldCount = await db.FieldPolicies.CountAsync(ct);

        var version = $"{stats?.Count ?? 0}-{stats?.MaxVersion ?? 0}-{stats?.MaxId ?? 0}-{accessCount}-{fieldCount}";
        _versionCache = (version, _clock.UtcNow);
        return version;
    }

    public void Invalidate()
    {
        _cache.Clear();
        _versionCache = null;
    }
}
