using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EzOdata.Core.Time;
using EzOdata.Data.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EzOdata.Admin.Auth;

public sealed class JwtTokenService
{
    public const string AdminClaim = "ez:admin";
    public const string RoleIdsClaim = "ez:role-ids";

    private readonly JwtOptions _options;
    private readonly ISystemClock _clock;

    public JwtTokenService(IOptions<JwtOptions> options, ISystemClock clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(UserEntity user, IReadOnlyList<RoleEntity> roles)
    {
        var now = _clock.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        var isAdmin = user.IsSystemAdmin || roles.Any(r => r is { IsAdmin: true, IsActive: true });

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("typ", "access"),
            new(AdminClaim, isAdmin ? "true" : "false"),
        };
        claims.AddRange(roles.Where(r => r.IsActive).Select(r => new Claim("role", r.Name)));
        var activeRoleIds = roles.Where(r => r.IsActive).Select(r => r.Id).ToList();
        if (activeRoleIds.Count > 0)
        {
            claims.Add(new Claim(RoleIdsClaim, string.Join(",", activeRoleIds)));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _options.Issuer,
        ValidateAudience = true,
        ValidAudience = _options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(60),
        NameClaimType = JwtRegisteredClaimNames.Sub,
    };
}
