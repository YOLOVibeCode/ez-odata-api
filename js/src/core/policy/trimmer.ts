import { findColumn, type SchemaSnapshot, type TableModel } from "../schema.js";
import type { RowFilterParser } from "./contracts.js";
import type { PolicyEngine } from "./engine.js";
import { Verb, type RequestIdentity, type RoleRuleSet } from "./model.js";

/**
 * Produces the identity-trimmed view of a schema (port of
 * src/EzOdata.Core/Policy/SnapshotTrimmer.cs, spec 05 §3.9 / OD-8): tables the
 * identity cannot GET vanish entirely; denied and write-only columns vanish from
 * types; foreign keys touching hidden tables vanish with them. The trimmed
 * snapshot feeds $metadata, the service document, docs generation, and MCP.
 */
export function trimSnapshot(
  snapshot: SchemaSnapshot,
  identity: RequestIdentity,
  roleRules: readonly RoleRuleSet[],
  serviceName: string,
  engine: PolicyEngine,
  rowFilterParser: RowFilterParser,
): SchemaSnapshot {
  // Bypass identity (dev no-auth) and bypass-data-rules roles see the full schema.
  if (identity.bypass || roleRules.some((r) => r.bypassDataRules)) {
    return snapshot;
  }

  const visibleTables: TableModel[] = [];
  for (const table of snapshot.tables) {
    const columns = table.columns.map((c) => c.exposedName);
    const decision = engine.authorize(
      identity,
      roleRules,
      serviceName,
      table.exposedName,
      Verb.Get,
      columns,
      rowFilterParser,
    );
    if (!decision.allowed) continue;

    const hidden = new Set<string>(decision.deniedFields);
    for (const f of decision.writeOnlyFields) hidden.add(f);

    const trimmedColumns = table.columns.filter((c) => !hidden.has(c.exposedName));
    const trimmedPk = table.primaryKey.filter((k) => !hidden.has(k));

    visibleTables.push({
      ...table,
      columns: trimmedColumns,
      // A PK that lost a column is no longer a usable key - expose keyless (read-only collection).
      primaryKey: trimmedPk.length === table.primaryKey.length ? trimmedPk : [],
    });
  }

  const visibleNames = new Set(visibleTables.map((t) => t.exposedName));

  for (let i = 0; i < visibleTables.length; i++) {
    const table = visibleTables[i]!;
    const keptFks = table.foreignKeys
      .filter((fk) => visibleNames.has(fk.refTable))
      .filter((fk) => fk.columns.every((c) => findColumn(table, c) !== undefined));
    if (keptFks.length !== table.foreignKeys.length) {
      visibleTables[i] = { ...table, foreignKeys: keptFks };
    }
  }

  return { ...snapshot, tables: visibleTables };
}
