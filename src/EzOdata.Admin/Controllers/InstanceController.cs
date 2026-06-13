using System.Reflection;
using EzOdata.Core.Services;
using EzOdata.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.Admin.Controllers;

[ApiController]
[Route("system/instance")]
[Authorize(Policy = AdminPolicy.Name)]
public class InstanceController : ControllerBase
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    private readonly SystemDbContext _db;

    public InstanceController(SystemDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        var dbOk = await _db.Database.CanConnectAsync(ct);

        return Ok(new
        {
            version,
            uptimeSeconds = (int)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
            systemDatabase = new { provider = _db.Database.ProviderName, connected = dbOk },
            features = new
            {
                connectors = ConnectorTypes.All,
                odata = true,
                rest = true,
                mcp = true,
            },
        });
    }

    [HttpGet("metrics-summary")]
    public async Task<IActionResult> MetricsSummary(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        // Materialize first, then aggregate in memory (avoids provider-specific quirks).
        var events = await _db.AuditEvents
            .AsNoTracking()
            .Where(e => e.OccurredAt >= since)
            .Select(e => new { e.Category, e.Outcome, e.DurationMs })
            .ToListAsync(ct);

        var data = events.Where(e => e.Category == "data.read" || e.Category == "data.write").ToList();
        var durations = data.Where(e => e.DurationMs.HasValue).Select(e => e.DurationMs!.Value).ToList();

        return Ok(new
        {
            windowMinutes = 60,
            requests = data.Count,
            errors = data.Count(e => e.Outcome == "error"),
            denied = data.Count(e => e.Outcome == "denied"),
            avgDurationMs = durations.Count > 0 ? (int)durations.Average() : 0,
        });
    }
}
