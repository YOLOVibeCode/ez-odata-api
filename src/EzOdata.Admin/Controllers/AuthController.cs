using System.Security.Claims;
using System.Text.Json;
using EzOdata.Admin.Auth;
using EzOdata.Core.Audit;
using EzOdata.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.Admin.Controllers;

[ApiController]
[Route("system/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly JwtTokenService _tokens;
    private readonly SystemDbContext _db;
    private readonly IAuditSink _audit;

    public AuthController(AuthService auth, JwtTokenService tokens, SystemDbContext db, IAuditSink audit)
    {
        _auth = auth;
        _tokens = tokens;
        _db = db;
        _audit = audit;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request.Email ?? "", request.Password ?? "", ct);

        _audit.Record(new AuditEvent
        {
            RequestId = HttpContext.Items["ez:request-id"] as string ?? "unknown",
            Category = "auth",
            Action = result.Succeeded ? "login" : "login.failed",
            Outcome = result.Succeeded ? "ok" : "denied",
            UserId = result.User?.Id,
            DetailJson = JsonSerializer.Serialize(new { ip = ClientIp() }),
        });

        if (!result.Succeeded)
        {
            // Uniform message: no user-enumeration or lockout disclosure (spec 08 §3.3)
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials.");
        }

        return Ok(await BuildAuthResponseAsync(result.User!, result.Roles, ct));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        var result = await _auth.RefreshAsync(request.RefreshToken ?? "", ClientIp(), UserAgent(), ct);
        if (!result.Succeeded)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid refresh token.");
        }

        var (token, expires) = _tokens.IssueAccessToken(result.User!, result.Roles);
        return Ok(new AuthResponse(token, expires, result.NewRefreshToken!, UserResponse.From(result.User!, result.Roles)));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest request, CancellationToken ct)
    {
        await _auth.RevokeAsync(request.RefreshToken ?? "", ct);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        if (!long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId))
        {
            return Unauthorized();
        }

        var user = await _db.Users
            .Include(u => u.Roles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
        {
            return Unauthorized();
        }

        var roles = user.Roles.Where(r => r.Role is not null).Select(r => r.Role!).ToList();
        return Ok(UserResponse.From(user, roles));
    }

    private async Task<AuthResponse> BuildAuthResponseAsync(
        Data.Entities.UserEntity user, IReadOnlyList<Data.Entities.RoleEntity> roles, CancellationToken ct)
    {
        var (token, expires) = _tokens.IssueAccessToken(user, roles);
        var refresh = await _auth.IssueRefreshTokenAsync(user.Id, null, ClientIp(), UserAgent(), ct);
        return new AuthResponse(token, expires, refresh, UserResponse.From(user, roles));
    }

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? UserAgent() => Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;
}
