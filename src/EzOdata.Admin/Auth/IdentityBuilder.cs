using System.Security.Claims;
using EzOdata.Core.Policy;

namespace EzOdata.Admin.Auth;

/// <summary>
/// ClaimsPrincipal → RequestIdentity (spec 08 §2): merges API-key (app) and JWT (user)
/// identities when both are present — role union, user claims dominant.
/// </summary>
public static class IdentityBuilder
{
    public static RequestIdentity Build(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return RequestIdentity.Anonymous;
        }

        var roleIds = new List<long>();
        long? appId = null;
        long? userId = null;
        string? email = null;
        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var claim in principal.Claims)
        {
            switch (claim.Type)
            {
                case ApiKeyAuthenticationHandler.AppIdClaim when long.TryParse(claim.Value, out var a):
                    appId = a;
                    claims["appId"] = claim.Value;
                    break;
                case ApiKeyAuthenticationHandler.RoleIdClaim when long.TryParse(claim.Value, out var r):
                    roleIds.Add(r);
                    break;
                case JwtTokenService.RoleIdsClaim:
                    foreach (var part in claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (long.TryParse(part, out var rid)) roleIds.Add(rid);
                    }

                    break;
                case "sub" or ClaimTypes.NameIdentifier when long.TryParse(claim.Value, out var u):
                    userId = u;
                    claims["userId"] = claim.Value;
                    claims["sub"] = claim.Value;
                    break;
                case "email" or ClaimTypes.Email:
                    email = claim.Value;
                    claims["email"] = claim.Value;
                    break;
            }
        }

        var isAdmin = principal.HasClaim(JwtTokenService.AdminClaim, "true");

        return new RequestIdentity
        {
            AppId = appId,
            UserId = userId,
            Email = email,
            IsAdmin = isAdmin,
            RoleIds = roleIds.Distinct().ToList(),
            Claims = claims,
        };
    }
}
