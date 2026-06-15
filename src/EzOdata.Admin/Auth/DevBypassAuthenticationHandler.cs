using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EzOdata.Admin.Auth;

/// <summary>
/// Development-only authentication scheme: every request (including anonymous ones)
/// is authenticated as a synthetic bypass principal carrying the <c>ez:bypass=true</c> claim.
///
/// Registered ONLY when <c>Auth:DevNoAuth=true</c> AND
/// <c>ASPNETCORE_ENVIRONMENT=Development</c>. The host refuses to start if
/// <c>Auth:DevNoAuth</c> is set in any other environment.
/// </summary>
public sealed class DevBypassAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string Scheme = "EzDevBypass";
}

public sealed class DevBypassAuthenticationHandler : AuthenticationHandler<DevBypassAuthenticationOptions>
{
    public const string BypassClaim = "ez:bypass";

    public DevBypassAuthenticationHandler(
        IOptionsMonitor<DevBypassAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(BypassClaim, "true"),
            new Claim(ClaimTypes.Name, "dev-bypass"),
            new Claim(JwtTokenService.AdminClaim, "true"),
        };
        var identity = new ClaimsIdentity(claims, DevBypassAuthenticationOptions.Scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), DevBypassAuthenticationOptions.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
