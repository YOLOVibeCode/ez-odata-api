namespace EzOdata.Core.Security;

/// <summary>
/// Password acceptance rules (spec 08 §3.2): min length 12, no composition rules,
/// deny common-password list.
/// </summary>
public sealed class PasswordPolicy
{
    public const int MinLength = 12;

    private readonly HashSet<string> _denied;

    public PasswordPolicy(IEnumerable<string>? deniedPasswords = null)
    {
        _denied = new HashSet<string>(deniedPasswords ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Returns null when acceptable, otherwise a human-readable reason.</summary>
    public string? Check(string? password)
    {
        if (password is null || password.Length < MinLength)
        {
            return $"Password must be at least {MinLength} characters.";
        }

        if (_denied.Contains(password))
        {
            return "Password is too common.";
        }

        return null;
    }
}
