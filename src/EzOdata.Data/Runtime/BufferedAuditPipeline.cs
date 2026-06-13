using System.Threading.Channels;
using EzOdata.Core.Audit;
using EzOdata.Core.Time;
using EzOdata.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EzOdata.Data.Runtime;

/// <summary>
/// Buffered audit writer (spec 08 §8): bounded channel (capacity 10k), batched flush
/// (≤500 per batch, ≤2 s latency), drop-with-counter on overflow — the data path is
/// never blocked (NFR-8).
/// </summary>
public sealed class BufferedAuditPipeline : BackgroundService, IAuditSink
{
    private const int Capacity = 10_000;
    private const int MaxBatch = 500;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly Channel<AuditEvent> _channel = Channel.CreateBounded<AuditEvent>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    private readonly IServiceScopeFactory _scopes;
    private readonly ISystemClock _clock;
    private readonly ILogger<BufferedAuditPipeline> _logger;
    private long _dropped;

    public BufferedAuditPipeline(IServiceScopeFactory scopes, ISystemClock clock, ILogger<BufferedAuditPipeline> logger)
    {
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void Record(AuditEvent auditEvent)
    {
        var stamped = auditEvent.OccurredAt == default
            ? auditEvent with { OccurredAt = _clock.UtcNow }
            : auditEvent;

        if (!_channel.Writer.TryWrite(stamped))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AuditEvent>(MaxBatch);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                batch.Clear();
                var deadline = _clock.UtcNow + FlushInterval;

                // Block for the first event, then drain up to the batch limit / deadline.
                if (!await _channel.Reader.WaitToReadAsync(stoppingToken)) break;

                while (batch.Count < MaxBatch && _channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                while (batch.Count < MaxBatch && _clock.UtcNow < deadline)
                {
                    if (!_channel.Reader.TryRead(out var more)) break;
                    batch.Add(more);
                }

                if (batch.Count > 0)
                {
                    await FlushAsync(batch, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit flush failed; {Count} events lost.", batch.Count);
            }
        }

        // Final drain on shutdown (spec 12 OPS-7)
        var remaining = new List<AuditEvent>();
        while (_channel.Reader.TryRead(out var lastItem) && remaining.Count < Capacity)
        {
            remaining.Add(lastItem);
        }

        if (remaining.Count > 0)
        {
            try
            {
                await FlushAsync(remaining, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Final audit drain failed; {Count} events lost.", remaining.Count);
            }
        }
    }

    private async Task FlushAsync(List<AuditEvent> batch, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();

        db.AuditEvents.AddRange(batch.Select(e => new AuditEventEntity
        {
            OccurredAt = e.OccurredAt,
            RequestId = e.RequestId,
            Category = e.Category,
            Action = e.Action,
            Outcome = e.Outcome,
            ServiceId = e.ServiceId,
            AppId = e.AppId,
            UserId = e.UserId,
            RoleId = e.RoleId,
            Resource = e.Resource,
            DetailJson = e.DetailJson,
            DurationMs = e.DurationMs,
        }));
        await db.SaveChangesAsync(ct);
    }
}
