using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EzOdata.OData;

/// <summary>
/// Opaque, HMAC-signed pagination tokens (spec 05 §4.2). Phase 1 encodes the next
/// offset; keyset cursors are a planned optimization behind the same opaque format.
/// Tampered tokens → 400, never an altered query.
/// </summary>
public sealed class SkipTokenCodec
{
    private readonly byte[] _key;

    public SkipTokenCodec(byte[] signingKey)
    {
        if (signingKey is not { Length: >= 16 })
        {
            throw new ArgumentException("Skip token signing key must be at least 16 bytes.", nameof(signingKey));
        }

        _key = signingKey;
    }

    public string Encode(int nextSkip)
    {
        var payload = nextSkip.ToString(CultureInfo.InvariantCulture);
        var signature = Sign(payload);
        return Base64Url($"{payload}.{signature}");
    }

    public bool TryDecode(string token, out int nextSkip)
    {
        nextSkip = 0;
        string decoded;
        try
        {
            decoded = FromBase64Url(token);
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.LastIndexOf('.');
        if (separator <= 0) return false;

        var payload = decoded.Substring(0, separator);
        var signature = decoded.Substring(separator + 1);
        if (!FixedTimeEquals(Sign(payload), signature)) return false;

        return int.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out nextSkip)
               && nextSkip >= 0;
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(_key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var hex = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) hex.Append(b.ToString("x2"));
        return hex.ToString();
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
