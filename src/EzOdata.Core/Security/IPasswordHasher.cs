namespace EzOdata.Core.Security;

/// <summary>
/// Password hashing contract (spec 08 §3.2): Argon2id with versioned parameters,
/// rehash-on-login when parameters strengthen.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerification Verify(string password, string storedHash);
}

public enum PasswordVerification
{
    Failed = 0,
    Success = 1,

    /// <summary>Correct password, but stored hash uses outdated parameters — rehash and persist.</summary>
    SuccessRehashNeeded = 2,
}
