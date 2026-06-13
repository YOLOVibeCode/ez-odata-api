using EzOdata.Connectors.Abstractions;
using Npgsql;

namespace EzOdata.Connectors.PostgreSql;

internal static class PostgreSqlConnectionStrings
{
    /// <summary>Whitelisted passthrough keywords (spec 04 §3): arbitrary keywords are rejected upstream.</summary>
    private static readonly HashSet<string> AllowedExtras = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationName", "SearchPath", "Timezone", "CommandTimeout", "KeepAlive",
    };

    public static string Build(ConnectionSpec spec, int connectTimeoutSeconds = 5)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = spec.Host,
            Database = spec.Database,
            Username = spec.Username,
            Password = spec.Password,
            Timeout = connectTimeoutSeconds,
            ApplicationName = "ez-odata-api",
            // Npgsql 8: Require encrypts without certificate validation; VerifyFull validates.
            SslMode = spec.Tls.Mode switch
            {
                "disable" => SslMode.Disable,
                "require" when spec.Tls.AllowInvalid => SslMode.Require,
                "require" => SslMode.VerifyFull,
                _ => SslMode.Prefer,
            },
        };
        if (spec.Port is { } port) builder.Port = port;

        foreach (var extra in spec.Extra)
        {
            if (AllowedExtras.Contains(extra.Key))
            {
                builder[extra.Key] = extra.Value;
            }
        }

        return builder.ConnectionString;
    }
}
