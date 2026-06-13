using EzOdata.Core.Security;
using EzOdata.Data;
using EzOdata.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.Admin.Controllers;

[ApiController]
[Route("system/setup")]
[AllowAnonymous]
public class SetupController : ControllerBase
{
    private readonly SystemDbContext _db;
    private readonly SetupState _setupState;
    private readonly IPasswordHasher _hasher;
    private readonly PasswordPolicy _passwordPolicy;

    public SetupController(SystemDbContext db, SetupState setupState, IPasswordHasher hasher, PasswordPolicy passwordPolicy)
    {
        _db = db;
        _setupState = setupState;
        _hasher = hasher;
        _passwordPolicy = passwordPolicy;
    }

    [HttpGet]
    public async Task<SetupStatusResponse> Status(CancellationToken ct) =>
        new(Required: !await _setupState.IsCompleteAsync(_db, ct));

    [HttpPost]
    public async Task<IActionResult> Complete([FromBody] SetupRequest request, CancellationToken ct)
    {
        if (await _setupState.IsCompleteAsync(_db, ct))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Setup already completed.");
        }

        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "A valid email is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Display name is required.");
        }

        if (_passwordPolicy.Check(request.Password) is { } passwordError)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: passwordError);
        }

        // Exactly one setup request may win (spec 03 §4): unique email index + recheck under save.
        var user = new UserEntity
        {
            Email = request.Email.Trim().ToLowerInvariant(),
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = _hasher.Hash(request.Password),
            IsSystemAdmin = true,
            IsActive = true,
        };
        _db.Users.Add(user);

        // Seed roles (spec 03 §4): 'admin' (bypass) assigned to the first user;
        // 'read-only-all' present but inactive until an admin opts in.
        var adminRole = new RoleEntity
        {
            Name = "admin",
            Description = "Full administrative and data access (bypasses data rules — audited).",
            IsActive = true,
            IsAdmin = true,
            BypassDataRules = true,
        };
        var readOnlyRole = new RoleEntity
        {
            Name = "read-only-all",
            Description = "GET on every table of every service. Inactive by default.",
            IsActive = false,
            Access =
            [
                new RoleServiceAccessEntity
                {
                    ServiceId = null, ResourcePattern = "*", Verbs = 1 /* GET */, Effect = "allow",
                },
            ],
        };
        _db.Roles.AddRange(adminRole, readOnlyRole);
        user.Roles.Add(new UserRoleEntity { User = user, Role = adminRole });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Setup already completed.");
        }

        _setupState.MarkComplete();
        return CreatedAtAction(nameof(Status), UserResponse.From(user, [adminRole]));
    }
}
