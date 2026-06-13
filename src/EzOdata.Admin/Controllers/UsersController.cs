using System.Security.Cryptography;
using EzOdata.Core.Security;
using EzOdata.Data;
using EzOdata.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.Admin.Controllers;

public sealed record UserInput(string Email, string DisplayName, bool IsActive, long[]? RoleIds, string? Password);

public sealed record UserDetailResponse(
    long Id, string Email, string DisplayName, bool IsActive, bool IsSystemAdmin,
    IReadOnlyList<long> RoleIds, DateTimeOffset? LastLoginAt, long RowVersion);

[ApiController]
[Route("system/users")]
[Authorize(Policy = AdminPolicy.Name)]
public class UsersController : ControllerBase
{
    private readonly SystemDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly PasswordPolicy _passwordPolicy;

    public UsersController(SystemDbContext db, IPasswordHasher hasher, PasswordPolicy passwordPolicy)
    {
        _db = db;
        _hasher = hasher;
        _passwordPolicy = passwordPolicy;
    }

    [HttpGet]
    public async Task<IReadOnlyList<UserDetailResponse>> List(CancellationToken ct) =>
        (await _db.Users.AsNoTracking().Include(u => u.Roles).OrderBy(u => u.Email).ToListAsync(ct))
        .Select(From).ToList();

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == id, ct);
        return user is null ? NotFound() : Ok(From(user));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Email) || !input.Email.Contains('@'))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "A valid email is required.");
        }

        if (input.Password is { } password && _passwordPolicy.Check(password) is { } error)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: error);
        }

        var email = input.Email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "A user with this email already exists.");
        }

        if (await ValidateRolesAsync(input.RoleIds, ct) is { } roleProblem) return roleProblem;

        var user = new UserEntity
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? email : input.DisplayName.Trim(),
            IsActive = input.IsActive,
            PasswordHash = input.Password is { } pw ? _hasher.Hash(pw) : null,
        };
        foreach (var roleId in input.RoleIds ?? [])
        {
            user.Roles.Add(new UserRoleEntity { User = user, RoleId = roleId });
        }

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, From(user));
    }

    [HttpPatch("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] UserInput input, CancellationToken ct)
    {
        var user = await _db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        // Last-admin protection (spec 07 §7)
        if (!input.IsActive && user.IsSystemAdmin)
        {
            var otherAdmins = await _db.Users.CountAsync(u => u.Id != id && u.IsSystemAdmin && u.IsActive, ct);
            if (otherAdmins == 0)
            {
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "Cannot deactivate the last system administrator.");
            }
        }

        if (!string.IsNullOrWhiteSpace(input.DisplayName)) user.DisplayName = input.DisplayName.Trim();
        user.IsActive = input.IsActive;

        if (input.RoleIds is not null)
        {
            if (await ValidateRolesAsync(input.RoleIds, ct) is { } roleProblem) return roleProblem;
            _db.UserRoles.RemoveRange(user.Roles);
            user.Roles.Clear();
            foreach (var roleId in input.RoleIds)
            {
                user.Roles.Add(new UserRoleEntity { UserId = user.Id, RoleId = roleId });
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(From(user));
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        if (user.IsSystemAdmin)
        {
            var otherAdmins = await _db.Users.CountAsync(u => u.Id != id && u.IsSystemAdmin && u.IsActive, ct);
            if (otherAdmins == 0)
            {
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "Cannot remove the last system administrator.");
            }
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>One-time password reset token, returned to the admin (spec 07 §7).</summary>
    [HttpPost("{id:long}/password-reset")]
    public async Task<IActionResult> PasswordReset(long id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        // The reset token IS the new temporary password (forced change lands with the UI).
        var temporary = "Ez1-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        user.PasswordHash = _hasher.Hash(temporary);
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        await _db.SaveChangesAsync(ct);

        return Ok(new { temporaryPassword = temporary });
    }

    private async Task<IActionResult?> ValidateRolesAsync(long[]? roleIds, CancellationToken ct)
    {
        if (roleIds is not { Length: > 0 }) return null;

        var known = await _db.Roles.Where(r => roleIds.Contains(r.Id)).CountAsync(ct);
        return known == roleIds.Distinct().Count()
            ? null
            : Problem(statusCode: StatusCodes.Status400BadRequest, title: "One or more role ids are unknown.");
    }

    private static UserDetailResponse From(UserEntity user) => new(
        user.Id, user.Email, user.DisplayName, user.IsActive, user.IsSystemAdmin,
        user.Roles.Select(r => r.RoleId).ToList(), user.LastLoginAt, user.RowVersion);
}
