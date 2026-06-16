import type { ConnectionSpec, ConnectionTester, ConnectionTestResult } from "./contracts.js";
import { createKnex, type KnexClient } from "./knex-helpers.js";

/** Maps a Knex error to a ConnectionTestCategory. */
function categorize(err: unknown): ConnectionTestResult {
  const msg = err instanceof Error ? err.message : String(err);
  const lower = msg.toLowerCase();

  if (lower.includes("authentication") || lower.includes("password") || lower.includes("login failed") || lower.includes("access denied")) {
    return { ok: false, category: "authFailed", message: msg };
  }
  if (lower.includes("econnrefused") || lower.includes("connection refused") || lower.includes("timed out") || lower.includes("enotfound")) {
    return { ok: false, category: "unreachable", message: msg };
  }
  if (lower.includes("ssl") || lower.includes("tls") || lower.includes("certificate")) {
    return { ok: false, category: "tlsError", message: msg };
  }
  if (lower.includes("database") && (lower.includes("not exist") || lower.includes("unknown database"))) {
    return { ok: false, category: "databaseMissing", message: msg };
  }
  return { ok: false, category: "other", message: msg };
}

/**
 * ConnectionTester backed by Knex (spec CON-5: must return within 10 seconds).
 * A single SELECT 1 verifies host, port, credentials, and database existence.
 */
export class KnexConnectionTester implements ConnectionTester {
  constructor(private readonly client: KnexClient) {}

  async test(spec: ConnectionSpec, _signal?: AbortSignal): Promise<ConnectionTestResult> {
    const db = createKnex(spec, this.client);
    try {
      await db.raw("SELECT 1");
      const serverVersion = await this.getServerVersion(db);
      return {
        ok: true,
        category: "ok",
        message: "Connection successful.",
        ...(serverVersion !== undefined ? { serverVersion } : {}),
      };
    } catch (err) {
      return categorize(err);
    } finally {
      await db.destroy();
    }
  }

  private async getServerVersion(db: ReturnType<typeof createKnex>): Promise<string | undefined> {
    try {
      if (this.client === "pg") {
        const r = await db.raw("SHOW server_version");
        const rows = (r as { rows: Array<{ server_version: string }> }).rows;
        return rows[0]?.server_version;
      }
      if (this.client === "mysql2") {
        const r = await db.raw("SELECT VERSION() AS v");
        return ((r as [Array<{ v: string }>, unknown])[0][0])?.v;
      }
      if (this.client === "mssql") {
        const r = await db.raw("SELECT @@VERSION AS v");
        const rows = (r as { recordset: Array<{ v: string }> }).recordset;
        return rows[0]?.v?.split("\n")[0];
      }
      if (this.client === "better-sqlite3") {
        const r = await db.raw("SELECT sqlite_version() AS v");
        return (r as Array<{ v: string }>)[0]?.v;
      }
    } catch {
      // version query is best-effort
    }
    return undefined;
  }
}
