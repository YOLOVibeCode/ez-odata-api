import type { QueryResult } from "../../core/result.js";
import { Row } from "../../core/result.js";
import type { QueryExecution, QueryExecutor, SqlDialect } from "../contracts.js";
import { createKnex, extractRawRows, toPositional, type KnexClient } from "../knex-helpers.js";
import { SqlCompiler } from "../sql/compiler.js";

/**
 * QueryExecutor backed by Knex (port of AdoQueryExecutor logic, spec 04 §7.2).
 * Runs parameterized SQL produced by SqlCompiler against any supported engine.
 */
export class KnexQueryExecutor implements QueryExecutor {
  private readonly compiler: SqlCompiler;

  constructor(
    dialect: SqlDialect,
    private readonly client: KnexClient,
  ) {
    this.compiler = new SqlCompiler(dialect);
  }

  async query(execution: QueryExecution, _signal?: AbortSignal): Promise<QueryResult> {
    const compiled =
      execution.query.apply !== undefined
        ? this.compiler.compileApply(execution.schema, execution.query)
        : this.compiler.compileSelect(execution.schema, execution.query);

    const limit = execution.query.apply !== undefined ? undefined : execution.query.top;
    const db = createKnex(execution.connection, this.client);
    try {
      const [sql, bindings] = toPositional(compiled.sql, compiled.parameters);
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const rawResult = await (db.raw(sql, bindings as any[]) as Promise<unknown>);
      const dbRows = extractRawRows(rawResult, this.client);

      const hasMore = limit !== undefined && dbRows.length > limit;
      const finalRows = hasMore ? dbRows.slice(0, limit) : dbRows;

      return {
        rows: finalRows.map((r) => new Row(Object.entries(r))),
        hasMore,
      };
    } finally {
      await db.destroy();
    }
  }

  async count(execution: QueryExecution, _signal?: AbortSignal): Promise<number> {
    const compiled = this.compiler.compileCount(execution.schema, execution.query);
    const db = createKnex(execution.connection, this.client);
    try {
      const [sql, bindings] = toPositional(compiled.sql, compiled.parameters);
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const rawResult = await (db.raw(sql, bindings as any[]) as Promise<unknown>);
      const rows = extractRawRows(rawResult, this.client);
      const firstRow = rows[0] as Record<string, unknown> | undefined;
      const value = firstRow !== undefined ? Object.values(firstRow)[0] : 0;
      return Number(value ?? 0);
    } finally {
      await db.destroy();
    }
  }
}
