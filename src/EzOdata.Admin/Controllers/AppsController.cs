using System.Security.Cryptography;
using System.Text.Json;
using EzOdata.Admin.Auth;
using EzOdata.Data;
using EzOdata.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EzOdata.Admin.Controllers;

public sealed record AppInput(
    string Name, string? Description, long RoleId, bool IsActive,
    string[]? AllowedOrigins, bool RequireUserSession, bool McpEnabled);

public sealed record AppResponse(
    long Id, string Name, string? Description, long RoleId, string RoleName, bool IsActive,
    string[] AllowedOrigins, bool RequireUserSession, bool McpEnabled, long RowVersion)
{
    public static AppResponse From(AppEntity app) => new(
        app.Id, app.Name, app.Description, app.RoleId, app.Role?.Name ?? "", app.IsActive,
        app.AllowedOriginsJson is { } json ? JsonSerializer.Deserialize<string[]>(json) ?? [] : [],
        app.RequireUserSession, app.McpEnabled, app.RowVersion);
}

public sealed record CreateKeyRequest(string Name, DateTimeOffset? ExpiresAt);

public sealed record KeyResponse(
    long Id, string KeyPrefix, string Name, DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt, DateTimeOffset CreatedAt);

/// <summary>Returned exactly once at creation (spec 07 §6); the full key is never stored.</summary>
public sealed record CreatedKeyResponse(long Id, string Key, string KeyPrefix, string Name, DateTimeOffset? ExpiresAt);

[ApiController]
[Route("system/apps")]
[Authorize(Policy = AdminPolicy.Name)]
public class AppsController : ControllerBase
{
    private readonly SystemDbContext _db;
    private readonly IMemoryCache _cache;

    public AppsController(SystemDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    [HttpGet]
    public async Task<IReadOnlyList<AppResponse>> List(CancellationToken ct) =>
        (await _db.Apps.AsNoTracking().Include(a => a.Role).OrderBy(a => a.Name).ToListAsync(ct))
        .Select(AppResponse.From).ToList();

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var app = await _db.Apps.AsNoTracking().Include(a => a.Role).FirstOrDefaultAsync(a => a.Id == id, ct);
        return app is null ? NotFound() : Ok(AppResponse.From(app));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AppInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 64)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "App name is required (max 64 chars).");
        }

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == input.RoleId, ct);
        if (role is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unknown role id {input.RoleId}.");
        }

        if (await _db.Apps.AnyAsync(a => a.Name == input.Name, ct))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "An app with this name already exists.");
        }

        var app = new AppEntity
        {
            Name = input.Name,
            Description = input.Description,
            RoleId = input.RoleId,
            Role = role,
            IsActive = input.IsActive,
            AllowedOriginsJson = input.AllowedOrigins is { Length: > 0 }
                ? JsonSerializer.Serialize(input.AllowedOrigins)
                : null,
            RequireUserSession = input.RequireUserSession,
            McpEnabled = input.McpEnabled,
        };
        _db.Apps.Add(app);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = app.Id }, AppResponse.From(app));
    }

    [HttpPatch("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] AppInput input, CancellationToken ct)
    {
        var app = await _db.Apps.Include(a => a.Role).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();

        if (input.RoleId != app.RoleId)
        {
            var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == input.RoleId, ct);
            if (role is null)
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Unknown role id {input.RoleId}.");
            }

            app.RoleId = input.RoleId;
            app.Role = role;
        }

        app.Description = input.Description;
        app.IsActive = input.IsActive;
        app.AllowedOriginsJson = input.AllowedOrigins is { Length: > 0 }
            ? JsonSerializer.Serialize(input.AllowedOrigins)
            : null;
        app.RequireUserSession = input.RequireUserSession;
        app.McpEnabled = input.McpEnabled;

        await _db.SaveChangesAsync(ct);
        InvalidateKeyCacheForApp(app.Id);
        return Ok(AppResponse.From(app));
    }

    // ---- keys ----

    [HttpGet("{id:long}/keys")]
    public async Task<IReadOnlyList<KeyResponse>> ListKeys(long id, CancellationToken ct) =>
        await _db.ApiKeys.AsNoTracking()
            .Where(k => k.AppId == id)
            .OrderByDescending(k => k.Id)
            .Select(k => new KeyResponse(k.Id, k.KeyPrefix, k.Name, k.ExpiresAt, k.RevokedAt, k.LastUsedAt, k.CreatedAt))
            .ToListAsync(ct);

    [HttpPost("{id:long}/keys")]
    public async Task<IActionResult> CreateKey(long id, [FromBody] CreateKeyRequest request, CancellationToken ct)
    {
        var app = await _db.Apps.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();

        var key = GenerateKey();
        var entity = new ApiKeyEntity
        {
            AppId = id,
            KeyPrefix = key.Substring(0, 12),
            KeyHash = AuthService.Sha256(key),
            Name = string.IsNullOrWhiteSpace(request.Name) ? "default" : request.Name.Trim(),
            ExpiresAt = request.ExpiresAt,
        };
        _db.ApiKeys.Add(entity);
        await _db.SaveChangesAsync(ct);

        // Full key returned exactly once — never retrievable again (spec 07 §6)
        return Created($"/system/apps/{id}/keys/{entity.Id}",
            new CreatedKeyResponse(entity.Id, key, entity.KeyPrefix, entity.Name, entity.ExpiresAt));
    }

    [HttpDelete("{appId:long}/keys/{keyId:long}")]
    public async Task<IActionResult> RevokeKey(long appId, long keyId, CancellationToken ct)
    {
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.AppId == appId, ct);
        if (key is null) return NotFound();

        if (key.RevokedAt is null)
        {
            key.RevokedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _cache.Remove("ez:apikey:" + key.KeyHash); // revocation effective immediately on this node
        }

        return NoContent();
    }

    private void InvalidateKeyCacheForApp(long appId)
    {
        // Key cache entries are per-hash; app-level changes (role swap, deactivation)
        // propagate within the 60 s TTL. Instant cross-node invalidation lands with Redis (Phase 8).
    }

    private static string GenerateKey()
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var bytes = RandomNumberGenerator.GetBytes(22);
        var chars = new char[22];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return "ez_live_" + new string(chars);
    }
}
