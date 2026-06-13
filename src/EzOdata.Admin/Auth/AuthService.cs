using System.Security.Cryptography;
using System.Text;
using EzOdata.Core.Security;
using EzOdata.Core.Time;
using EzOdata.Data;
using EzOdata.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EzOdata.Admin.Auth;

public sealed record LoginResult(bool Succeeded, UserEntity? User, IReadOnlyList<RoleEntity> Roles)
{
    public static readonly LoginResult Failed = new(false, null, []);
}

public sealed record RefreshResult(bool Succeeded, UserEntity? User, IReadOnlyList<RoleEntity> Roles, string? NewRefreshToken);

/// <summary>
/// Login with lockout (spec 08 §3.3) and refresh-token rotation with family
/// revocation on reuse (spec 08 §3.4). Failure responses are uniform: callers
/// must not reveal whether the account exists or is locked.
/// </summary>
public sealed class AuthService
{
    private readonly SystemDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ISystemClock _clock;
    private readonly LockoutOptions _lockout;
    private readonly JwtOptions _jwt;

    public AuthService(
        SystemDbContext db,
        IPasswordHasher hasher,
        ISystemClock clock,
        IOptions<LockoutOptions> lockout,
        IOptions<JwtOptions> jwt)
    {
        _db = db;
        _hasher = hasher;
        _clock = clock;
        _lockout = lockout.Value;
        _jwt = jwt.Value;
    }

    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct)
    {
        var user = await _db.Users
            .Include(u => u.Roles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == email.ToLowerInvariant(), ct);

        if (user is null || !user.IsActive || user.PasswordHash is null)
        {
            return LoginResult.Failed;
        }

        var now = _clock.UtcNow;
        if (user.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            return LoginResult.Failed; // identical to wrong password — no enumeration (spec 08 §3.3)
        }

        var verification = _hasher.Verify(password, user.PasswordHash);
        if (verification == PasswordVerification.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= _lockout.Threshold)
            {
                var lockSeconds = Math.Min(
                    _lockout.BaseSeconds * (1 << Math.Min(user.FailedLoginCount - _lockout.Threshold, 16)),
                    _lockout.MaxSeconds);
                user.LockedUntil = now.AddSeconds(lockSeconds);
            }

            await _db.SaveChangesAsync(ct);
            return LoginResult.Failed;
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.Hash(password);
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;
        await _db.SaveChangesAsync(ct);

        var roles = user.Roles.Where(r => r.Role is not null).Select(r => r.Role!).ToList();
        return new LoginResult(true, user, roles);
    }

    public async Task<string> IssueRefreshTokenAsync(long userId, Guid? familyId, string? ip, string? userAgent, CancellationToken ct)
    {
        var token = GenerateOpaqueToken();
        _db.RefreshTokens.Add(new RefreshTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Sha256(token),
            FamilyId = familyId ?? Guid.NewGuid(),
            ExpiresAt = _clock.UtcNow.AddDays(_jwt.RefreshTokenDays),
            CreatedAt = _clock.UtcNow,
            CreatedByIp = ip,
            UserAgent = userAgent,
        });
        await _db.SaveChangesAsync(ct);
        return token;
    }

    public async Task<RefreshResult> RefreshAsync(string refreshToken, string? ip, string? userAgent, CancellationToken ct)
    {
        var hash = Sha256(refreshToken);
        var stored = await _db.RefreshTokens
            .Include(t => t.User!).ThenInclude(u => u.Roles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null || stored.User is null || !stored.User.IsActive)
        {
            return new RefreshResult(false, null, [], null);
        }

        var now = _clock.UtcNow;
        if (stored.RevokedAt is not null)
        {
            // Reuse of a rotated token ⇒ theft signal: revoke the whole family (spec 08 §3.4).
            await RevokeFamilyAsync(stored.FamilyId, ct);
            return new RefreshResult(false, null, [], null);
        }

        if (stored.ExpiresAt <= now)
        {
            return new RefreshResult(false, null, [], null);
        }

        stored.RevokedAt = now;
        await _db.SaveChangesAsync(ct);

        var newToken = await IssueRefreshTokenAsync(stored.UserId, stored.FamilyId, ip, userAgent, ct);
        var roles = stored.User.Roles.Where(r => r.Role is not null).Select(r => r.Role!).ToList();
        return new RefreshResult(true, stored.User, roles, newToken);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Sha256(refreshToken);
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is { RevokedAt: null })
        {
            stored.RevokedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task RevokeFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var family = await _db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in family)
        {
            token.RevokedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private static string GenerateOpaqueToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    internal static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
