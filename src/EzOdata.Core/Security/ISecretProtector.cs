namespace EzOdata.Core.Security;

/// <summary>
/// Envelope encryption for secrets at rest (spec 08 §9):
/// AES-256-GCM with per-secret DEK wrapped by the master key.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts plaintext, returning the versioned envelope string (format "v1:...").</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts an envelope. Throws <see cref="SecretProtectionException"/> on tamper/wrong key.</summary>
    string Unprotect(string envelope);
}

public sealed class SecretProtectionException : Exception
{
    public SecretProtectionException(string message, Exception? inner = null) : base(message, inner) { }
}
