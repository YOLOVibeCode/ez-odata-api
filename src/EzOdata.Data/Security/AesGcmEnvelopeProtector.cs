using System.Security.Cryptography;
using System.Text;
using EzOdata.Core.Security;

namespace EzOdata.Data.Security;

/// <summary>
/// Envelope encryption (spec 08 §9): each secret gets a fresh random DEK (AES-256-GCM);
/// the DEK is wrapped by the master key (also AES-256-GCM).
/// Format: v1:{b64(dekNonce + wrappedDek + dekTag)}:{b64(nonce)}:{b64(ciphertext)}:{b64(tag)}
/// Wrapping the DEK rather than encrypting directly with the master key enables
/// master-key rotation by re-wrapping DEKs without re-encrypting payloads.
/// </summary>
public sealed class AesGcmEnvelopeProtector : ISecretProtector
{
    public const string ProbePlaintext = "ez-odata-api:master-key-probe";

    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _masterKey;

    public AesGcmEnvelopeProtector(byte[] masterKey)
    {
        if (masterKey is not { Length: KeyBytes })
        {
            throw new ArgumentException($"Master key must be exactly {KeyBytes} bytes.", nameof(masterKey));
        }

        _masterKey = masterKey;
    }

    public string Protect(string plaintext)
    {
        var dek = RandomNumberGenerator.GetBytes(KeyBytes);
        try
        {
            // Wrap DEK under master key
            var dekNonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var wrappedDek = new byte[KeyBytes];
            var dekTag = new byte[TagBytes];
            using (var master = new AesGcm(_masterKey, TagBytes))
            {
                master.Encrypt(dekNonce, dek, wrappedDek, dekTag);
            }

            // Encrypt payload under DEK
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var cipher = new byte[plainBytes.Length];
            var tag = new byte[TagBytes];
            using (var aes = new AesGcm(dek, TagBytes))
            {
                aes.Encrypt(nonce, plainBytes, cipher, tag);
            }

            var wrapped = new byte[NonceBytes + KeyBytes + TagBytes];
            dekNonce.CopyTo(wrapped, 0);
            wrappedDek.CopyTo(wrapped, NonceBytes);
            dekTag.CopyTo(wrapped, NonceBytes + KeyBytes);

            return $"v1:{Convert.ToBase64String(wrapped)}:{Convert.ToBase64String(nonce)}" +
                   $":{Convert.ToBase64String(cipher)}:{Convert.ToBase64String(tag)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public string Unprotect(string envelope)
    {
        string[] parts;
        byte[] wrapped, nonce, cipher, tag;
        try
        {
            parts = envelope.Split(':');
            if (parts.Length != 5 || parts[0] != "v1")
            {
                throw new SecretProtectionException("Unrecognized envelope format.");
            }

            wrapped = Convert.FromBase64String(parts[1]);
            nonce = Convert.FromBase64String(parts[2]);
            cipher = Convert.FromBase64String(parts[3]);
            tag = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException ex)
        {
            throw new SecretProtectionException("Malformed envelope encoding.", ex);
        }

        if (wrapped.Length != NonceBytes + KeyBytes + TagBytes || nonce.Length != NonceBytes || tag.Length != TagBytes)
        {
            throw new SecretProtectionException("Malformed envelope structure.");
        }

        var dek = new byte[KeyBytes];
        try
        {
            var dekNonce = wrapped.AsSpan(0, NonceBytes);
            var wrappedDek = wrapped.AsSpan(NonceBytes, KeyBytes);
            var dekTag = wrapped.AsSpan(NonceBytes + KeyBytes, TagBytes);

            try
            {
                using var master = new AesGcm(_masterKey, TagBytes);
                master.Decrypt(dekNonce, wrappedDek, dekTag, dek);

                var plain = new byte[cipher.Length];
                using var aes = new AesGcm(dek, TagBytes);
                aes.Decrypt(nonce, cipher, tag, plain);
                return Encoding.UTF8.GetString(plain);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new SecretProtectionException("Decryption failed: wrong key or tampered data.", ex);
            }
            catch (CryptographicException ex)
            {
                throw new SecretProtectionException("Decryption failed: wrong key or tampered data.", ex);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Startup probe (spec 08 §9): verifies the configured master key can decrypt existing data.</summary>
    public bool ValidateProbe(string probeEnvelope)
    {
        try
        {
            return Unprotect(probeEnvelope) == ProbePlaintext;
        }
        catch (SecretProtectionException)
        {
            return false;
        }
    }
}
