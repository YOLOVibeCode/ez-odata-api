using EzOdata.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.Admin.Controllers;

public sealed record AuditEventResponse(
    long Id, DateTimeOffset OccurredAt, string RequestId, string Category, string Action,
    string Outcome, long? ServiceId, long? AppId, long? UserId, string? Resource,
    string DetailJson, int? DurationMs);

[ApiController]
[Route("system/audit")]
[Authorize(Policy = AdminPolicy.Name)]
public class AuditController : ControllerBase
{
    private readonly SystemDbContext _db;

    public AuditController(SystemDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] string? category,
        [FromQuery] string? action,
        [FromQuery] string? outcome,
        [FromQuery] long? serviceId,
        [FromQuery] long? appId,
        [FromQuery] long? userId,
        [FromQuery] string? requestId,
        [FromQuery] long? beforeId,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 1000);

        var query = _db.AuditEvents.AsNoTracking().AsQueryable();
        if (category is not null) query = query.Where(e => e.Category == category);
        if (action is not null) query = query.Where(e => e.Action == action);
        if (outcome is not null) query = query.Where(e => e.Outcome == outcome);
        if (serviceId is not null) query = query.Where(e => e.ServiceId == serviceId);
        if (appId is not null) query = query.Where(e => e.AppId == appId);
        if (userId is not null) query = query.Where(e => e.UserId == userId);
        if (requestId is not null) query = query.Where(e => e.RequestId == requestId);
        if (beforeId is not null) query = query.Where(e => e.Id < beforeId); // keyset paging

        var events = await query
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .Select(e => new AuditEventResponse(
                e.Id, e.OccurredAt, e.RequestId, e.Category, e.Action, e.Outcome,
                e.ServiceId, e.AppId, e.UserId, e.Resource, e.DetailJson, e.DurationMs))
            .ToListAsync(ct);

        return Ok(new
        {
            resource = events,
            meta = new { next = events.Count == limit ? (long?)events[^1].Id : null },
        });
    }
}
