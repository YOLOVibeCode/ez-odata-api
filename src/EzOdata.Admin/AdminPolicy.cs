using EzOdata.Admin.Auth;
using Microsoft.AspNetCore.Authorization;

namespace EzOdata.Admin;

/// <summary>Authorization policy for /system/* management endpoints (spec 07).</summary>
public static class AdminPolicy
{
    public const string Name = "SystemAdmin";

    public static AuthorizationPolicy Build() =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireClaim(JwtTokenService.AdminClaim, "true")
            .Build();
}
