using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Security;
using EzOdata.Core.Services;

namespace EzOdata.Admin.Services;

/// <summary>
/// Converts admin connection input to the encrypted-at-rest representation
/// (envelope + fingerprint + display-safe summary). Spec 03 §2.1, 08 §9.
/// </summary>
public sealed class ServiceConnectionCodec
{
    private readonly ISecretProtector _protector;

    public ServiceConnectionCodec(ISecretProtector protector) => _protector = protector;

    public (string Encrypted, string Fingerprint, string Display) Encode(string connectorType, ConnectionInput input)
    {
        var spec = new ConnectionSpec
        {
            Host = input.Host,
            Port = input.Port,
            Database = input.Database,
            Username = input.Username,
            Password = input.Password,
            FilePath = input.FilePath,
            Tls = new TlsSpec
            {
                Mode = input.Tls?.Mode ?? "prefer",
                CaCertPem = input.Tls?.CaCertPem,
                AllowInvalid = input.Tls?.AllowInvalid ?? false,
            },
            Extra = input.Extra ?? new Dictionary<string, string>(),
        };

        var json = JsonSerializer.Serialize(spec, JsonDefaults.Options);
        var normalized = connectorType.Equals(ConnectorTypes.Sqlite, StringComparison.OrdinalIgnoreCase)
            ? spec.FilePath ?? ""
            : $"{spec.Host}:{spec.Port}/{spec.Database}";

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        var display = connectorType.Equals(ConnectorTypes.Sqlite, StringComparison.OrdinalIgnoreCase)
            ? spec.FilePath ?? "(no file)"
            : $"{spec.Host}:{spec.Port?.ToString() ?? "default"}/{spec.Database} (user {spec.Username})";

        return (_protector.Protect(json), fingerprint, display);
    }

    public ConnectionSpec Decode(string encrypted) =>
        JsonSerializer.Deserialize<ConnectionSpec>(_protector.Unprotect(encrypted), JsonDefaults.Options)
        ?? throw new InvalidOperationException("Stored connection envelope decoded to null.");

    public string? Validate(string connectorType, ConnectionInput input)
    {
        if (connectorType.Equals(ConnectorTypes.Sqlite, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(input.FilePath) ? "filePath is required for sqlite." : null;
        }

        if (string.IsNullOrWhiteSpace(input.Host)) return "host is required.";
        if (string.IsNullOrWhiteSpace(input.Database)) return "database is required.";
        if (string.IsNullOrWhiteSpace(input.Username)) return "username is required.";
        if (input.Port is < 1 or > 65535) return "port must be between 1 and 65535.";
        return null;
    }
}
