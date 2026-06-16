import knex, { type Knex } from "knex";
import type { ConnectionSpec } from "./contracts.js";

/** Knex client identifier strings. */
export type KnexClient = "pg" | "mysql2" | "mssql" | "better-sqlite3";

/** Build a Knex connection config object from a ConnectionSpec. */
function buildKnexConnection(spec: ConnectionSpec, client: KnexClient): object {
  if (client === "better-sqlite3") {
    return { filename: spec.filePath ?? ":memory:" };
  }
  if (client === "mssql") {
    return {
      server: spec.host,
      port: spec.port,
      database: spec.database,
      user: spec.username,
      password: spec.password,
      options: {
        trustServerCertificate: spec.tls.mode === "disable" || spec.tls.allowInvalid,
        encrypt: spec.tls.mode !== "disable",
      },
    };
  }
  // pg / mysql2
  const sslDisabled = spec.tls.mode === "disable";
  return {
    host: spec.host,
    port: spec.port,
    database: spec.database,
    user: spec.username,
    password: spec.password,
    ssl: sslDisabled
      ? false
      : { ca: spec.tls.caCertPem, rejectUnauthorized: !spec.tls.allowInvalid },
  };
}

/** Create a Knex instance; caller must call `.destroy()` after use. */
export function createKnex(spec: ConnectionSpec, client: KnexClient): Knex {
  const useNullAsDefault = client === "better-sqlite3";
  return knex({
    client,
    connection: buildKnexConnection(spec, client) as Knex.StaticConnectionConfig,
    useNullAsDefault,
    pool: { min: 0, max: 1 },
  });
}

/** Extract rows from a knex.raw() result across all supported clients. */
export function extractRawRows(rawResult: unknown, client: KnexClient): Record<string, unknown>[] {
  if (client === "pg") {
    const r = rawResult as { rows?: Record<string, unknown>[] };
    return r.rows ?? [];
  }
  if (client === "mysql2") {
    const r = rawResult as [Record<string, unknown>[], unknown];
    return Array.isArray(r[0]) ? (r[0] as Record<string, unknown>[]) : [];
  }
  if (client === "mssql") {
    // knex v3 + mssql (tedious) returns rows directly as an array from db.raw()
    if (Array.isArray(rawResult)) {
      return rawResult as Record<string, unknown>[];
    }
    // Legacy fallback: older format wrapped in { recordset: [...] }
    const r = rawResult as { recordset?: Record<string, unknown>[] };
    return r.recordset ?? [];
  }
  // better-sqlite3: rows directly
  if (Array.isArray(rawResult)) {
    return rawResult as Record<string, unknown>[];
  }
  return [];
}

/** Convert @pN placeholders to positional ? bindings for knex.raw. */
export function toPositional(sql: string, params: readonly { name: string; value: unknown }[]): [string, unknown[]] {
  const sorted = [...params].sort((a, b) => parseInt(a.name.slice(2), 10) - parseInt(b.name.slice(2), 10));
  const values = sorted.map((p) => (p.value === undefined ? null : p.value));
  const rawSql = sql.replace(/@p\d+/g, "?");
  return [rawSql, values];
}
