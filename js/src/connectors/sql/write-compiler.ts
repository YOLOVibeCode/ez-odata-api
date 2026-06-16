import { ErrorCodes } from "../../core/errors.js";
import type { QueryRequest } from "../../core/query.js";
import { findColumn, type SchemaSnapshot, type TableModel } from "../../core/schema.js";
import type { RecordPayload, WriteRequest } from "../../core/write.js";
import { QueryValidationError, type SqlDialect } from "../contracts.js";
import { type CompiledQuery, type SqlParam, SqlCompiler } from "./compiler.js";

/**
 * Write-side SQL compilation (port of WriteSqlCompiler.cs, spec 04 §7.3).
 * Shares dialect + parameterization rules with SqlCompiler. RETURNING-style
 * key retrieval is dialect-owned.
 *
 * Safety invariant (NFR-3, CON-3): no client string ever appears in SQL text;
 * every value is a parameter.
 */
export class WriteSqlCompiler {
  private readonly dialect: SqlDialect;
  private readonly readCompiler: SqlCompiler;

  constructor(dialect: SqlDialect) {
    this.dialect = dialect;
    this.readCompiler = new SqlCompiler(dialect);
  }

  compileInsert(_schema: SchemaSnapshot, table: TableModel, record: RecordPayload, returning: boolean): CompiledQuery {
    const entries = Object.entries(record.values);
    const columns = entries.map(([key]) => {
      const column = findColumn(table, key);
      if (column === undefined) {
        throw new QueryValidationError(
          ErrorCodes.ValidationUnknownProperty,
          `Unknown property '${key}' on '${table.exposedName}'.`,
        );
      }
      return column;
    });

    const parameters: SqlParam[] = [];
    const markers: string[] = [];
    for (let i = 0; i < columns.length; i++) {
      const marker = `@p${parameters.length}`;
      parameters.push({ name: marker, value: entries[i]![1] });
      markers.push(marker);
    }

    let sql = `INSERT INTO ${this.qualified(table)}`;
    if (columns.length > 0) {
      const colList = columns.map((c) => this.dialect.quoteIdentifier(c.dbName)).join(", ");
      if (returning && this.dialect.returning === "outputClause") {
        sql += ` (${colList}) ${this.outputClause("INSERTED", table)} VALUES (${markers.join(", ")})`;
      } else {
        sql += ` (${colList}) VALUES (${markers.join(", ")})`;
      }
    } else {
      sql += " DEFAULT VALUES";
    }

    if (returning && this.dialect.returning === "returningSuffix") {
      sql += ` RETURNING ${this.returningList(table)}`;
    }

    return { sql, parameters, projection: table.columns };
  }

  compileUpdate(schema: SchemaSnapshot, table: TableModel, write: WriteRequest, returning: boolean): CompiledQuery {
    if (write.records.length !== 1) {
      throw new QueryValidationError(ErrorCodes.ValidationInvalidValue, "Update requires exactly one record.");
    }
    const record = write.records[0]!;
    const entries = Object.entries(record.values);
    if (entries.length === 0) {
      throw new QueryValidationError(ErrorCodes.ValidationInvalidValue, "Update payload contains no writable values.");
    }

    const parameters: SqlParam[] = [];
    const assignments: string[] = [];
    for (const [key, value] of entries) {
      const column = findColumn(table, key);
      if (column === undefined) {
        throw new QueryValidationError(ErrorCodes.ValidationUnknownProperty, `Unknown property '${key}'.`);
      }
      if (column.isPrimaryKey) {
        throw new QueryValidationError(ErrorCodes.ValidationInvalidValue, `Key property '${key}' cannot be updated.`);
      }
      const marker = `@p${parameters.length}`;
      parameters.push({ name: marker, value });
      assignments.push(`${this.dialect.quoteIdentifier(column.dbName)} = ${marker}`);
    }

    let sql = `UPDATE ${this.qualified(table)} SET ${assignments.join(", ")}`;

    if (returning && this.dialect.returning === "outputClause") {
      sql += ` ${this.outputClause("INSERTED", table)}`;
    }

    sql += this.appendWhere(parameters, schema, table, write);

    if (returning && this.dialect.returning === "returningSuffix") {
      sql += ` RETURNING ${this.returningList(table)}`;
    }

    return { sql, parameters, projection: table.columns };
  }

  compileDelete(schema: SchemaSnapshot, table: TableModel, write: WriteRequest): CompiledQuery {
    const parameters: SqlParam[] = [];
    let sql = `DELETE FROM ${this.qualified(table)}`;
    // For dialects using OUTPUT clause (SQL Server), add OUTPUT DELETED to get affected count
    if (this.dialect.returning === "outputClause" && table.primaryKey.length > 0) {
      const pkCols = table.primaryKey
        .map((k) => `DELETED.${this.dialect.quoteIdentifier(k)}`)
        .join(", ");
      sql += ` OUTPUT ${pkCols}`;
    }
    sql += this.appendWhere(parameters, schema, table, write);
    return { sql, parameters, projection: [] };
  }

  /**
   * Builds the WHERE clause from key + precondition, pushes parameters in place.
   * Throws if neither is provided (defense in depth: unguarded writes touch all rows).
   */
  private appendWhere(
    parameters: SqlParam[],
    schema: SchemaSnapshot,
    table: TableModel,
    write: WriteRequest,
  ): string {
    const clauses: string[] = [];

    if (write.key !== undefined) {
      for (const [key, value] of Object.entries(write.key.values)) {
        const column = findColumn(table, key);
        if (column === undefined) {
          throw new QueryValidationError(ErrorCodes.ValidationUnknownProperty, `Unknown key property '${key}'.`);
        }
        const marker = `@p${parameters.length}`;
        parameters.push({ name: marker, value });
        clauses.push(`${this.dialect.quoteIdentifier(column.dbName)} = ${marker}`);
      }
    }

    if (write.precondition !== undefined) {
      const selectReq: QueryRequest = {
        serviceName: write.serviceName,
        table: table.exposedName,
        filter: write.precondition,
        select:
          table.primaryKey.length > 0 ? [...table.primaryKey] : [table.columns[0]!.exposedName],
        orderBy: [],
        expand: [],
        count: false,
      };
      const probe = this.readCompiler.compileSelect(schema, selectReq);

      const whereIndex = probe.sql.indexOf(" WHERE ");
      if (whereIndex >= 0) {
        const orderIndex = probe.sql.indexOf(" ORDER BY ");
        const end = orderIndex > whereIndex ? orderIndex : probe.sql.length;
        let fragment = probe.sql.substring(whereIndex + 7, end);
        // Remove the t0. alias — UPDATE/DELETE have no alias.
        fragment = fragment.replaceAll("t0.", "");

        // Re-number @pN markers to continue after those already emitted.
        for (const param of probe.parameters) {
          const newMarker = `@p${parameters.length}`;
          fragment = fragment
            .replaceAll(`${param.name})`, `${newMarker})`)
            .replaceAll(`${param.name} `, `${newMarker} `)
            .replaceAll(`${param.name},`, `${newMarker},`);
          if (fragment.endsWith(param.name)) {
            fragment = fragment.slice(0, -param.name.length) + newMarker;
          }
          parameters.push({ name: newMarker, value: param.value });
        }
        clauses.push(`(${fragment})`);
      }
    }

    if (clauses.length === 0) {
      throw new QueryValidationError(
        ErrorCodes.ValidationInvalidValue,
        "Write operations require a key or filter predicate.",
      );
    }

    return ` WHERE ${clauses.join(" AND ")}`;
  }

  private returningList(table: TableModel): string {
    return table.columns
      .map((c) => `${this.dialect.quoteIdentifier(c.dbName)} AS ${this.dialect.quoteIdentifier(c.exposedName)}`)
      .join(", ");
  }

  private outputClause(source: string, table: TableModel): string {
    return (
      "OUTPUT " +
      table.columns
        .map(
          (c) =>
            `${source}.${this.dialect.quoteIdentifier(c.dbName)} AS ${this.dialect.quoteIdentifier(c.exposedName)}`,
        )
        .join(", ")
    );
  }

  private qualified(table: TableModel): string {
    return table.dbSchema === ""
      ? this.dialect.quoteIdentifier(table.dbName)
      : `${this.dialect.quoteIdentifier(table.dbSchema)}.${this.dialect.quoteIdentifier(table.dbName)}`;
  }
}
