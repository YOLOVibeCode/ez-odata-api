namespace EzOdata.Admin.Auth;

/// <summary>Bound from configuration section "Auth:Jwt" (spec 12 §4).</summary>
public sealed class JwtOptions
{
    public const string SectionPath = "Auth:Jwt";

    public string SigningKey { get; set; } = "";
    public string Issuer { get; set; } = "ez-odata-api";
    public string Audience { get; set; } = "ez-odata-api";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;

    public string? Error()
    {
        if (string.IsNullOrWhiteSpace(SigningKey) || SigningKey.Length < 32)
        {
            return "Auth:Jwt:SigningKey must be set and at least 32 characters (256 bits).";
        }

        if (AccessTokenMinutes is < 1 or > 1440) return "Auth:Jwt:AccessTokenMinutes must be 1..1440.";
        if (RefreshTokenDays is < 1 or > 365) return "Auth:Jwt:RefreshTokenDays must be 1..365.";
        return null;
    }
}

/// <summary>Bound from configuration section "Auth:Lockout" (spec 08 §3.3).</summary>
public sealed class LockoutOptions
{
    public const string SectionPath = "Auth:Lockout";

    public int Threshold { get; set; } = 5;
    public int BaseSeconds { get; set; } = 60;
    public int MaxSeconds { get; set; } = 1800;
}
