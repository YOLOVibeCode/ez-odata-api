using EzOdata.Core.Security;
using EzOdata.Data.Security;

namespace EzOdata.Host;

/// <summary>Startup configuration loading + fail-fast validation (spec 12 §4).</summary>
public static class HostConfig
{
    public static byte[] LoadMasterKey(IConfiguration config)
    {
        var inline = config["Encryption:MasterKey"];
        var file = config["Encryption:MasterKeyFile"];

        string? base64 = null;
        if (!string.IsNullOrWhiteSpace(inline))
        {
            base64 = inline.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
        {
            base64 = File.ReadAllText(file).Trim();
        }

        if (base64 is null)
        {
            throw new InvalidOperationException(
                "Encryption master key is required: set Encryption:MasterKey (base64, 32 bytes) " +
                "or Encryption:MasterKeyFile (spec 08 §9).");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Encryption master key must be valid base64.");
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"Encryption master key must be exactly 32 bytes (got {key.Length}).");
        }

        return key;
    }

    /// <summary>spec 08 §9: refuse to start when existing secrets cannot be decrypted with the configured key.</summary>
    public static async Task ValidateMasterKeyProbeAsync(IServiceProvider services, CancellationToken ct)
    {
        const string probeKey = "encryption.probe";

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.SystemDbContext>();
        var protector = (AesGcmEnvelopeProtector)scope.ServiceProvider.GetRequiredService<ISecretProtector>();

        var probe = await db.SystemSettings.FindAsync([probeKey], ct);
        if (probe is null)
        {
            db.SystemSettings.Add(new Data.Entities.SystemSettingEntity
            {
                Key = probeKey,
                ValueJson = System.Text.Json.JsonSerializer.Serialize(
                    protector.Protect(AesGcmEnvelopeProtector.ProbePlaintext)),
            });
            await db.SaveChangesAsync(ct);
            return;
        }

        var envelope = System.Text.Json.JsonSerializer.Deserialize<string>(probe.ValueJson);
        if (envelope is null || !protector.ValidateProbe(envelope))
        {
            throw new InvalidOperationException(
                "Encryption master key does not match existing encrypted data. " +
                "Refusing to start (spec 08 §9). Restore the original key or rotate via ez-admin.");
        }
    }
}
