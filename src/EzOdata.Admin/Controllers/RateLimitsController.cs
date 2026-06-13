using EzOdata.Data;
using EzOdata.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.Admin.Controllers;

public sealed record RateLimitInput(string ScopeType, long? ScopeId, int WindowSeconds, int MaxRequests);

public sealed record RateLimitResponse(long Id, string ScopeType, long? ScopeId, int WindowSeconds, int MaxRequests);

[ApiController]
[Route("system/rate-limits")]
[Authorize(Policy = AdminPolicy.Name)]
public class RateLimitsController : ControllerBase
{
    private static readonly string[] ScopeTypes = ["app", "role", "user", "service", "instance"];

    private readonly SystemDbContext _db;

    public RateLimitsController(SystemDbContext db) => _db = db;

    [HttpGet]
    public async Task<IReadOnlyList<RateLimitResponse>> List(CancellationToken ct) =>
        await _db.RateLimitPolicies.AsNoTracking()
            .OrderBy(p => p.ScopeType).ThenBy(p => p.ScopeId)
            .Select(p => new RateLimitResponse(p.Id, p.ScopeType, p.ScopeId, p.WindowSeconds, p.MaxRequests))
            .ToListAsync(ct);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RateLimitInput input, CancellationToken ct)
    {
        if (!ScopeTypes.Contains(input.ScopeType))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: $"scopeType must be one of: {string.Join(", ", ScopeTypes)}.");
        }

        if (input.ScopeType != "instance" && input.ScopeId is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "scopeId is required for non-instance scopes.");
        }

        if (input.WindowSeconds is < 1 or > 86_400 || input.MaxRequests < 1)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "windowSeconds must be 1..86400 and maxRequests >= 1.");
        }

        var policy = new RateLimitPolicyEntity
        {
            ScopeType = input.ScopeType,
            ScopeId = input.ScopeType == "instance" ? null : input.ScopeId,
            WindowSeconds = input.WindowSeconds,
            MaxRequests = input.MaxRequests,
        };
        _db.RateLimitPolicies.Add(policy);
        await _db.SaveChangesAsync(ct);

        return Created($"/system/rate-limits/{policy.Id}",
            new RateLimitResponse(policy.Id, policy.ScopeType, policy.ScopeId, policy.WindowSeconds, policy.MaxRequests));
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var policy = await _db.RateLimitPolicies.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null) return NotFound();

        _db.RateLimitPolicies.Remove(policy);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
