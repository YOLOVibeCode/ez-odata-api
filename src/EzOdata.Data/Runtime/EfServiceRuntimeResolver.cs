using System.Collections.Concurrent;
using System.Text.Json;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Schema;
using EzOdata.Core.Security;
using EzOdata.Core.Services;
using EzOdata.Core.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EzOdata.Data.Runtime;

/// <summary>
/// EF-backed <see cref="IServiceRuntimeResolver"/>: loads the service row + current
/// snapshot, decrypts the connection, and caches the result briefly (refresh swaps
/// are picked up within the TTL; pub/sub invalidation arrives with Redis in Phase 8).
/// </summary>
public sealed class EfServiceRuntimeResolver : IServiceRuntimeResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ISecretProtector _protector;
    private readonly ISystemClock _clock;
    private readonly ConcurrentDictionary<string, (ServiceRuntime? Runtime, DateTimeOffset At)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public EfServiceRuntimeResolver(IServiceScopeFactory scopes, ISecretProtector protector, ISystemClock clock)
    {
        _scopes = scopes;
        _protector = protector;
        _clock = clock;
    }

    public async Task<ServiceRuntime?> ResolveAsync(string serviceName, CancellationToken ct)
    {
        if (_cache.TryGetValue(serviceName, out var cached) && _clock.UtcNow - cached.At < CacheTtl)
        {
            return cached.Runtime;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();

        var service = await db.Services
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Name == serviceName.ToLowerInvariant(), ct);

        ServiceRuntime? runtime = null;
        if (service is not null)
        {
            var snapshotRow = await db.SchemaSnapshots
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.ServiceId == service.Id && s.IsCurrent, ct);

            if (snapshotRow is not null)
            {
                var connection = JsonSerializer.Deserialize<ConnectionSpec>(
                    _protector.Unprotect(service.ConnectionEncrypted), Json)!;
                var options = JsonSerializer.Deserialize<ServiceOptions>(service.OptionsJson, Json) ?? new ServiceOptions();
                var schema = SnapshotSerializer.Deserialize(snapshotRow.SnapshotJson);

                runtime = new ServiceRuntime(
                    service.Name, service.ConnectorType, connection, schema,
                    options, snapshotRow.VersionHash, service.Status);
            }
            else if (service.Status is not ServiceStatus.Disabled)
            {
                // Known service, no snapshot yet → engine reports 503 "not ready"
                runtime = new ServiceRuntime(
                    service.Name, service.ConnectorType, new ConnectionSpec(),
                    new SchemaSnapshot { Engine = service.ConnectorType },
                    new ServiceOptions(), "pending",
                    service.Status == ServiceStatus.Failed ? ServiceStatus.Failed : ServiceStatus.Pending);
            }
        }

        _cache[serviceName] = (runtime, _clock.UtcNow);
        return runtime;
    }

    public void Invalidate(string serviceName) => _cache.TryRemove(serviceName, out _);
}
