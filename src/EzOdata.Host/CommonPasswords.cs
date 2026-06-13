namespace EzOdata.Host;

/// <summary>
/// Seed denylist for the password policy (spec 08 §3.2). The full top-10k list ships
/// as an embedded resource in Phase 8 hardening; this covers the most common offenders
/// long enough to make the policy real from day one.
/// </summary>
public static class CommonPasswords
{
    public static readonly string[] List =
    [
        "password", "password1", "password123", "123456789012", "qwertyuiop12",
        "iloveyou1234", "adminadmin12", "letmein12345", "welcome12345", "monkey123456",
        "dragon123456", "sunshine1234", "princess1234", "football1234", "baseball1234",
        "superman1234", "trustno1trust", "passw0rd1234", "p@ssword1234", "changeme1234",
        "qwerty123456", "abc123abc123", "111111111111", "000000000000", "1234567890ab",
    ];
}
