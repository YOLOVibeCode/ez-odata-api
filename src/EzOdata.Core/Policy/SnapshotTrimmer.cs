using EzOdata.Core.Schema;

namespace EzOdata.Core.Policy;

/// <summary>
/// Produces the identity-trimmed view of a schema (spec 05 §3.9 / OD-8): tables the
/// identity cannot GET vanish entirely; denied and write-only columns vanish from
/// types; foreign keys touching hidden tables vanish with them. The trimmed snapshot
/// feeds $metadata, the service document, docs generation, and MCP.
/// </summary>
public static class SnapshotTrimmer
{
    public static SchemaSnapshot Trim(
        SchemaSnapshot snapshot,
        RequestIdentity identity,
        IReadOnlyList<RoleRuleSet> roleRules,
        string serviceName,
        PolicyEngine engine,
        RowFilterParser rowFilterParser)
    {
        // Bypass identity (dev no-auth) and bypass-data-rules roles see the full schema.
        if (identity.Bypass || roleRules.Any(r => r.BypassDataRules))
        {
            return snapshot;
        }

        var visibleTables = new List<TableModel>();
        foreach (var table in snapshot.Tables)
        {
            var columns = table.Columns.Select(c => c.ExposedName).ToList();
            var decision = engine.Authorize(
                identity, roleRules, serviceName, table.ExposedName, Verb.Get, columns, rowFilterParser);
            if (!decision.Allowed) continue;

            var hidden = new HashSet<string>(decision.DeniedFields, StringComparer.Ordinal);
            hidden.UnionWith(decision.WriteOnlyFields);

            var trimmedColumns = table.Columns.Where(c => !hidden.Contains(c.ExposedName)).ToList();
            var trimmedPk = table.PrimaryKey.Where(k => !hidden.Contains(k)).ToList();

            visibleTables.Add(table with
            {
                Columns = trimmedColumns,
                // A PK that lost a column is no longer a usable key — expose keyless (read-only collection).
                PrimaryKey = trimmedPk.Count == table.PrimaryKey.Count ? trimmedPk : [],
            });
        }

        var visibleNames = new HashSet<string>(visibleTables.Select(t => t.ExposedName), StringComparer.Ordinal);

        for (var i = 0; i < visibleTables.Count; i++)
        {
            var table = visibleTables[i];
            var keptFks = table.ForeignKeys
                .Where(fk => visibleNames.Contains(fk.RefTable))
                .Where(fk => fk.Columns.All(c => table.FindColumn(c) is not null))
                .ToList();
            if (keptFks.Count != table.ForeignKeys.Count)
            {
                visibleTables[i] = table with { ForeignKeys = keptFks };
            }
        }

        return snapshot with { Tables = visibleTables };
    }
}
