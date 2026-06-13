namespace EzOdata.Connectors.PostgreSql;

/// <summary>PostgreSQL udt_name → EDM type (the total mapping of spec 04 §6).</summary>
internal static class PostgreSqlTypeMap
{
    public static (string EdmType, bool IsFallback) Map(string udtName)
    {
        // Arrays arrive as "_int4" etc.
        if (udtName.StartsWith("_", StringComparison.Ordinal))
        {
            var (element, fallback) = Map(udtName.Substring(1));
            return fallback ? ("Edm.String", true) : ($"Collection({element})", false);
        }

        return udtName switch
        {
            "int2" => ("Edm.Int16", false),
            "int4" => ("Edm.Int32", false),
            "int8" => ("Edm.Int64", false),
            "numeric" or "money" => ("Edm.Decimal", false),
            "float4" => ("Edm.Single", false),
            "float8" => ("Edm.Double", false),
            "bool" => ("Edm.Boolean", false),
            "text" or "varchar" or "bpchar" or "citext" or "name" => ("Edm.String", false),
            "uuid" => ("Edm.Guid", false),
            "timestamptz" or "timestamp" => ("Edm.DateTimeOffset", false),
            "date" => ("Edm.Date", false),
            "time" or "timetz" => ("Edm.TimeOfDay", false),
            "interval" => ("Edm.Duration", false),
            "bytea" => ("Edm.Binary", false),
            "json" or "jsonb" => ("Edm.Untyped", false),
            _ => ("Edm.String", true), // CON-8: explicit fallback, excluded from typed filters
        };
    }
}
