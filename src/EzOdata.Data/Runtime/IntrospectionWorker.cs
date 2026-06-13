using System.Text.Json;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Schema;
using EzOdata.Core.Security;
using EzOdata.Core.Services;
using EzOdata.Core.Time;
using EzOdata.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EzOdata.Data.Runtime;

/// <summary>
/// Background introspection (spec 02 §7): picks up services in Pending/Refreshing,
/// runs the connector introspector, persists the snapshot with an atomic is_current
/// swap, and transitions the lifecycle state. In-flight requests keep the old
/// snapshot until the swap (NFR-4).
/// </summary>
public sealed class IntrospectionWorker : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopes;
    private readonly IConnectorRegistry _connectors;
    private readonly ISecretProtector _protector;
    private readonly ISystemClock _clock;
    private readonly ILogger<IntrospectionWorker> _logger;

    public IntrospectionWorker(
        IServiceScopeFactory scopes, IConnectorRegistry connectors,
        ISecretProtector protector, ISystemClock clock, ILogger<IntrospectionWorker> logger)
    {
        _scopes = scopes;
        _connectors = connectors;
        _protector = protector;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Poll interval; tests lower this via reflection-free ctor injection later if needed.</summary>
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Introspection sweep failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();

        // Ordered by Id, not timestamp: SQLite cannot ORDER BY DateTimeOffset columns.
        var pending = await db.Services
            .Where(s => s.Status == ServiceStatus.Pending || s.Status == ServiceStatus.Refreshing)
            .OrderBy(s => s.Id)
            .Take(3)
            .ToListAsync(ct);

        foreach (var service in pending)
        {
            await IntrospectServiceAsync(db, service, ct);
        }
    }

    private async Task IntrospectServiceAsync(SystemDbContext db, ServiceEntity service, CancellationToken ct)
    {
        if (!_connectors.TryGet(service.ConnectorType, out var connector))
        {
            service.Status = ServiceStatus.Failed;
            service.StatusDetail = $"Connector '{service.ConnectorType}' is not registered.";
            await db.SaveChangesAsync(ct);
            return;
        }

        var wasRefreshing = service.Status == ServiceStatus.Refreshing;
        service.Status = ServiceStatus.Introspecting;
        await db.SaveChangesAsync(ct);

        var job = new JobEntity
        {
            Kind = "introspection",
            ServiceId = service.Id,
            Status = "running",
            StartedAt = _clock.UtcNow,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        try
        {
            var connection = JsonSerializer.Deserialize<ConnectionSpec>(
                _protector.Unprotect(service.ConnectionEncrypted), Json)!;
            var options = JsonSerializer.Deserialize<ServiceOptions>(service.OptionsJson, Json) ?? new ServiceOptions();

            var snapshot = await connector.Introspector.IntrospectAsync(connection, new IntrospectionOptions
            {
                IncludeSchemas = options.IncludeSchemas,
                ExcludeTables = options.ExcludeTables,
                IncludeViews = options.IncludeViews,
                ExposedNameStyle = options.ExposedNameStyle,
            }, ct);

            var hash = SnapshotSerializer.ComputeHash(snapshot);

            // Atomic swap: clear previous current flag, insert new current row.
            var previous = await db.SchemaSnapshots
                .Where(s => s.ServiceId == service.Id && s.IsCurrent)
                .ToListAsync(ct);
            foreach (var row in previous) row.IsCurrent = false;

            db.SchemaSnapshots.Add(new SchemaSnapshotEntity
            {
                ServiceId = service.Id,
                VersionHash = hash,
                SnapshotJson = SnapshotSerializer.Serialize(snapshot),
                TableCount = snapshot.TableCount,
                ViewCount = snapshot.ViewCount,
                IntrospectedAt = _clock.UtcNow,
                IsCurrent = true,
            });

            // Retain the last 5 snapshots for diffing (spec 03 §2.2)
            var stale = await db.SchemaSnapshots
                .Where(s => s.ServiceId == service.Id && !s.IsCurrent)
                .OrderByDescending(s => s.Id)
                .Skip(4)
                .ToListAsync(ct);
            db.SchemaSnapshots.RemoveRange(stale);

            service.Status = ServiceStatus.Active;
            service.StatusDetail = null;
            job.Status = "succeeded";
            job.FinishedAt = _clock.UtcNow;
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Introspected service {Service}: {Tables} tables, {Views} views ({Mode}).",
                service.Name, snapshot.TableCount, snapshot.ViewCount, wasRefreshing ? "refresh" : "initial");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            service.Status = ServiceStatus.Failed;
            service.StatusDetail = ex.Message;
            job.Status = "failed";
            job.Error = ex.Message;
            job.FinishedAt = _clock.UtcNow;
            await db.SaveChangesAsync(ct);

            _logger.LogWarning(ex, "Introspection failed for service {Service}.", service.Name);
        }
    }
}
