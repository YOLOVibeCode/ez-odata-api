using System.Security.Cryptography;
using EzOdata.Core.Security;
using EzOdata.Data.Security;
using Xunit;

namespace EzOdata.UnitTests.Security;

public class EnvelopeEncryptionTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Protect_then_unprotect_roundtrips()
    {
        var protector = new AesGcmEnvelopeProtector(NewKey());
        const string secret = """{"host":"db","password":"hunter2"}""";

        var envelope = protector.Protect(secret);

        Assert.NotEqual(secret, envelope);
        Assert.Equal(secret, protector.Unprotect(envelope));
    }

    [Fact]
    public void Envelope_is_versioned()
    {
        var protector = new AesGcmEnvelopeProtector(NewKey());
        Assert.StartsWith("v1:", protector.Protect("x"));
    }

    [Fact]
    public void Same_plaintext_produces_different_envelopes()
    {
        var protector = new AesGcmEnvelopeProtector(NewKey());
        Assert.NotEqual(protector.Protect("secret"), protector.Protect("secret"));
    }

    [Fact]
    public void Wrong_master_key_throws()
    {
        var envelope = new AesGcmEnvelopeProtector(NewKey()).Protect("secret");
        var other = new AesGcmEnvelopeProtector(NewKey());

        Assert.Throws<SecretProtectionException>(() => other.Unprotect(envelope));
    }

    [Fact]
    public void Tampered_ciphertext_throws()
    {
        var key = NewKey();
        var protector = new AesGcmEnvelopeProtector(key);
        var envelope = protector.Protect("secret");

        var parts = envelope.Split(':');
        var cipher = Convert.FromBase64String(parts[3]);
        cipher[0] ^= 0xFF;
        parts[3] = Convert.ToBase64String(cipher);

        Assert.Throws<SecretProtectionException>(() => protector.Unprotect(string.Join(":", parts)));
    }

    [Fact]
    public void Malformed_envelope_throws()
    {
        var protector = new AesGcmEnvelopeProtector(NewKey());
        Assert.Throws<SecretProtectionException>(() => protector.Unprotect("v9:zzz"));
        Assert.Throws<SecretProtectionException>(() => protector.Unprotect("garbage"));
    }

    [Fact]
    public void Master_key_must_be_32_bytes()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmEnvelopeProtector(new byte[16]));
    }

    [Fact]
    public void Probe_value_detects_wrong_key()
    {
        // Spec 08 §9: startup fails if data exists but the key is wrong (probe value check).
        var key = NewKey();
        var probe = new AesGcmEnvelopeProtector(key).Protect(AesGcmEnvelopeProtector.ProbePlaintext);

        Assert.True(new AesGcmEnvelopeProtector(key).ValidateProbe(probe));
        Assert.False(new AesGcmEnvelopeProtector(NewKey()).ValidateProbe(probe));
    }
}
