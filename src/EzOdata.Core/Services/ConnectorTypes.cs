namespace EzOdata.Core.Services;

/// <summary>Connector type keys (spec 03 §2.1).</summary>
public static class ConnectorTypes
{
    public const string PostgreSql = "postgresql";
    public const string MySql = "mysql";
    public const string SqlServer = "sqlserver";
    public const string Sqlite = "sqlite";

    public static readonly IReadOnlyList<string> All = [PostgreSql, MySql, SqlServer, Sqlite];

    public static bool IsKnown(string? type) =>
        type is not null && All.Contains(type, StringComparer.OrdinalIgnoreCase);
}
