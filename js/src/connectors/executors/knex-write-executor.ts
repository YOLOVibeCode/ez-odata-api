import { ErrorCodes } from "../../core/errors.js";
import { comparison, constant, fieldRef, logical } from "../../core/query.js";
import type { FilterNode, QueryRequest } from "../../core/query.js";
import { Row } from "../../core/result.js";
import { ConnectorError } from "../../core/write.js";
import type { WriteExecution, WriteExecutor, SqlDialect } from "../contracts.js";
import type { WriteResult, KeyPredicate, RecordPayload } from "../../core/write.js";
import type { Knex } from "knex";
import { createKnex, extractRawRows, toPositional, type KnexClient } from "../knex-helpers.js";
import { SqlCompiler } from "../sql/compiler.js";
import { WriteSqlCompiler } from "../sql/write-compiler.js";
import { findTable } from "../../core/schema.js";
import type { TableModel, SchemaSnapshot } from "../../core/schema.js";
import type { ExecutionOptions } from "../contracts.js";

/**
 * WriteExecutor backed by Knex with per-dialect returning strategies
 * (port of AdoWriteExecutor logic, spec 04 §7.3).
 */
export class KnexWriteExecutor implements WriteExecutor {
  private readonly readCompiler: SqlCompiler;
  private readonly writeCompiler: WriteSqlCompiler;

  constructor(
    private readonly dialect: SqlDialect,
    private readonly client: KnexClient,
  ) {
    this.readCompiler = new SqlCompiler(dialect);
    this.writeCompiler = new WriteSqlCompiler(dialect);
  }

  async write(execution: WriteExecution, signal?: AbortSignal): Promise<WriteResult> {
    const [result] = await this.writeAtomic([execution], signal);
    return result!;
  }

  async writeAtomic(executions: readonly WriteExecution[], _signal?: AbortSignal): Promise<readonly WriteResult[]> {
    if (executions.length === 0) return [];
    const db = createKnex(executions[0]!.connection, this.client);
    try {
      return await db.transaction(async (trx) => {
        const results: WriteResult[] = [];
        for (const execution of executions) {
          results.push(await this.executeOne(trx, execution));
        }
        return results;
      });
    } finally {
      await db.destroy();
    }
  }

  private async executeOne(trx: Knex.Transaction, execution: WriteExecution): Promise<WriteResult> {
    const { write, schema, options } = execution;
    const table = findTable(schema, write.table);
    if (table === undefined) {
      throw new ConnectorError(ErrorCodes.ValidationInvalidValue, `Unknown table '${write.table}'.`);
    }

    switch (write.kind) {
      case "insert": {
        const rows: Row[] = [];
        for (const record of write.records) {
          rows.push(await this.insertOne(trx, schema, table, options, record, write.serviceName));
        }
        if (write.insertVisibilityFilter !== undefined && table.primaryKey.length > 0) {
          for (const row of rows) {
            await this.ensureVisible(trx, schema, table, options, write.insertVisibilityFilter, write.serviceName, row);
          }
        }
        return { affectedCount: rows.length, records: rows };
      }
      case "update":
      case "replace": {
        const compiled = this.writeCompiler.compileUpdate(schema, table, write, this.dialect.returning !== "none");
        const [sql, bindings] = toPositional(compiled.sql, compiled.parameters);
        if (this.dialect.returning === "none") {
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          const rawResult = await (trx.raw(sql, bindings as any[]) as Promise<unknown>);
          const affectedRows = this.extractAffected(rawResult);
          if (affectedRows === 0) return { affectedCount: 0, records: [] };
          const row = write.key !== undefined
            ? await this.readByKey(trx, schema, table, options, write.key, write.serviceName)
            : undefined;
          return { affectedCount: affectedRows, records: row !== undefined ? [row] : [] };
        }
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const rawResult = await (trx.raw(sql, bindings as any[]) as Promise<unknown>);
        const dbRows = extractRawRows(rawResult, this.client);
        return { affectedCount: dbRows.length, records: dbRows.map((r) => new Row(Object.entries(r))) };
      }
      case "delete": {
        const compiled = this.writeCompiler.compileDelete(schema, table, write);
        const [sql, bindings] = toPositional(compiled.sql, compiled.parameters);
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const rawResult = await (trx.raw(sql, bindings as any[]) as Promise<unknown>);
        return { affectedCount: this.extractAffected(rawResult), records: [] };
      }
      default:
        throw new ConnectorError(ErrorCodes.InternalUnmapped, `Unsupported write kind '${write.kind}'.`);
    }
  }

  private async insertOne(
    trx: Knex.Transaction,
    schema: SchemaSnapshot,
    table: TableModel,
    options: ExecutionOptions,
    record: RecordPayload,
    serviceName: string,
  ): Promise<Row> {
    const compiled = this.writeCompiler.compileInsert(schema, table, record, this.dialect.returning !== "none");
    const [sql, bindings] = toPositional(compiled.sql, compiled.parameters);

    if (this.dialect.returning === "none") {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const rawInsertResult = await (trx.raw(sql, bindings as any[]) as Promise<unknown>);

      // MySQL: extract auto-generated insertId from OkPacket to read back the full row
      if (this.client === "mysql2" && table.primaryKey.length === 1) {
        const okPacket = (rawInsertResult as [{ insertId?: number }, unknown])[0];
        const insertId = okPacket?.insertId;
        if (typeof insertId === "number" && insertId > 0) {
          const pkCol = table.primaryKey[0]!;
          const row = await this.readByKey(trx, schema, table, options, { values: { [pkCol]: insertId } }, serviceName);
          if (row !== undefined) return row;
        }
      }

      const readBack = await this.readBackAfterInsert(trx, schema, table, options, serviceName, record);
      return readBack ?? new Row(Object.entries(record.values));
    }
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const rawResult = await (trx.raw(sql, bindings as any[]) as Promise<unknown>);
    const rows = extractRawRows(rawResult, this.client);
    if (rows.length === 0) throw new ConnectorError(ErrorCodes.InternalUnmapped, "Insert returned no row.");
    return new Row(Object.entries(rows[0]!));
  }

  private async readBackAfterInsert(
    trx: Knex.Transaction,
    schema: SchemaSnapshot,
    table: TableModel,
    options: ExecutionOptions,
    serviceName: string,
    record: RecordPayload,
  ): Promise<Row | undefined> {
    if (table.primaryKey.length > 0 && table.primaryKey.every((k) => k in record.values)) {
      const keyValues = Object.fromEntries(table.primaryKey.map((k) => [k, record.values[k]]));
      return this.readByKey(trx, schema, table, options, { values: keyValues }, serviceName);
    }
    return undefined;
  }

  private async readByKey(
    trx: Knex.Transaction,
    schema: SchemaSnapshot,
    table: TableModel,
    _options: ExecutionOptions,
    key: KeyPredicate,
    serviceName: string,
  ): Promise<Row | undefined> {
    let filter: FilterNode | undefined;
    for (const [k, v] of Object.entries(key.values)) {
      const node = comparison(fieldRef(k), "eq", constant(v));
      filter = filter === undefined ? node : logical("and", [filter, node]);
    }
    const selectReq: QueryRequest = {
      serviceName,
      table: table.exposedName,
      top: 1,
      orderBy: [],
      expand: [],
      count: false,
      ...(filter !== undefined ? { filter } : {}),
    };
    const compiled = this.readCompiler.compileSelect(schema, selectReq);
    const [sql, bindings] = toPositional(compiled.sql, compiled.parameters);
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const rawResult = await (trx.raw(sql, bindings as any[]) as Promise<unknown>);
    const rows = extractRawRows(rawResult, this.client);
    return rows.length > 0 ? new Row(Object.entries(rows[0]!)) : undefined;
  }

  private async ensureVisible(
    trx: Knex.Transaction,
    schema: SchemaSnapshot,
    table: TableModel,
    _options: ExecutionOptions,
    visibility: FilterNode,
    serviceName: string,
    row: Row,
  ): Promise<void> {
    let predicate: FilterNode = visibility;
    for (const key of table.primaryKey) {
      predicate = logical("and", [predicate, comparison(fieldRef(key), "eq", constant(row.get(key)))]);
    }
    const countReq: QueryRequest = {
      serviceName,
      table: table.exposedName,
      filter: predicate,
      orderBy: [],
      expand: [],
      count: false,
    };
    const compiled = this.readCompiler.compileCount(schema, countReq);
    const [sql, bindings] = toPositional(compiled.sql, compiled.parameters);
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const rawResult = await (trx.raw(sql, bindings as any[]) as Promise<unknown>);
    const rows = extractRawRows(rawResult, this.client);
    const firstRow = rows[0] as Record<string, unknown> | undefined;
    const cnt = Number(firstRow !== undefined ? Object.values(firstRow)[0] : 0);
    if (cnt === 0) {
      throw new ConnectorError(ErrorCodes.ForbiddenRowFilter, "Inserted record does not satisfy the role's row filter.");
    }
  }

  private extractAffected(rawResult: unknown): number {
    if (this.client === "pg") {
      return (rawResult as { rowCount?: number | null }).rowCount ?? 0;
    }
    if (this.client === "mysql2") {
      return ((rawResult as [{ affectedRows?: number }, unknown])[0])?.affectedRows ?? 0;
    }
    if (this.client === "better-sqlite3") {
      // better-sqlite3 DML results (via knex): { changes: number, lastInsertRowid: bigint }
      const r = rawResult as { changes?: number } | undefined;
      return r?.changes ?? 0;
    }
    if (this.client === "mssql") {
      // knex v3 + mssql returns an array of rows; for DELETE/UPDATE with OUTPUT, rows = affected records
      if (Array.isArray(rawResult)) return rawResult.length;
    }
    return 0;
  }
}
