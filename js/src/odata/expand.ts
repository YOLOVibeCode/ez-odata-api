import { inList, logical, comparison, constant, fieldRef, type ExpandNode, type FilterNode, type QueryRequest } from "../core/query.js";
import { Row, type QueryResult } from "../core/result.js";
import type { SchemaSnapshot, TableModel, ForeignKeyModel } from "../core/schema.js";
import type { ConnectionSpec, ExecutionOptions, QueryExecutor } from "../connectors/contracts.js";
import { QueryValidationError } from "../connectors/contracts.js";
import { ErrorCodes } from "../core/errors.js";

/**
 * Executes $expand by issuing batched follow-up queries per level (port of
 * ExpansionExecutor.cs, spec 04 §7.1). Stitches results in memory.
 */

export interface ExpandPolicyDecision {
  readonly allowed: boolean;
  readonly hidden: boolean;
  readonly deniedFields: ReadonlySet<string>;
  readonly maskedFields: ReadonlyMap<string, string>;
  readonly rowFilter?: FilterNode;
}

export class FieldDeniedByExpandError extends Error {
  constructor(navigation: string) {
    super(`Access to field '${navigation}' is denied.`);
    this.name = "FieldDeniedByExpandError";
  }
}

export class ExpansionExecutor {
  constructor(
    private readonly executor: QueryExecutor,
    private readonly connection: ConnectionSpec,
    private readonly schema: SchemaSnapshot,
    private readonly options: ExecutionOptions,
    private readonly serviceName: string,
    private readonly authorize: (table: TableModel) => ExpandPolicyDecision,
  ) {}

  async expand(
    parentTable: TableModel,
    parentRows: readonly Row[],
    expands: readonly ExpandNode[],
    signal?: AbortSignal,
  ): Promise<void> {
    if (parentRows.length === 0) return;
    for (const expand of expands) {
      await this.expandOne(parentTable, parentRows, expand, signal);
    }
  }

  private async expandOne(
    parentTable: TableModel,
    parentRows: readonly Row[],
    expand: ExpandNode,
    signal?: AbortSignal,
  ): Promise<void> {
    const resolved = resolveNavigation(this.schema, parentTable, expand.navigation);
    if (resolved === undefined) {
      throw new QueryValidationError(
        ErrorCodes.ValidationBadFilter,
        `Unknown navigation '${expand.navigation}' on '${parentTable.exposedName}'.`,
      );
    }

    const { childTable, fk, isToMany } = resolved;

    const decision = this.authorize(childTable);
    if (!decision.allowed) {
      throw new FieldDeniedByExpandError(expand.navigation);
    }

    const parentKeyColumns = isToMany ? fk.refColumns : fk.columns;
    const childKeyColumns = isToMany ? fk.columns : fk.refColumns;

    const parentKeyValues = parentRows
      .map((r) => parentKeyColumns.map((c) => r.get(c)))
      .filter((vals) => vals.every((v) => v !== null && v !== undefined));

    if (parentKeyValues.length === 0) {
      for (const row of parentRows) {
        row.set(expand.navigation, isToMany ? [] : null);
      }
      return;
    }

    const childFilter = buildKeyInFilter(childKeyColumns, parentKeyValues);
    let combined: FilterNode = expand.filter
      ? logical("and", [expand.filter, childFilter])
      : childFilter;
    if (decision.rowFilter !== undefined) {
      combined = logical("and", [combined, decision.rowFilter]);
    }

    const denied = decision.deniedFields;
    const masked = decision.maskedFields;
    const baseSelect = expand.select
      ? expand.select.filter((f) => !denied.has(f) && !masked.has(f))
      : childTable.columns.map((c) => c.exposedName).filter((c) => !denied.has(c) && !masked.has(c));

    // Ensure child key columns are present for stitching
    const select = [...baseSelect];
    for (const k of childKeyColumns) {
      if (!select.includes(k)) select.push(k);
    }

    const childResult: QueryResult = await this.executor.query(
      {
        connection: this.connection,
        schema: this.schema,
        query: {
          serviceName: this.serviceName,
          table: childTable.exposedName,
          filter: combined,
          select,
          orderBy: expand.orderBy,
          expand: [],
          count: false,
        } as QueryRequest,
        options: this.options,
      },
      signal,
    );

    // Apply masks to child rows
    if (masked.size > 0) {
      for (const row of childResult.rows) {
        for (const [key, val] of masked) row.set(key, val);
      }
    }

    // Recurse for nested expands before grouping
    if (expand.expand.length > 0) {
      await this.expand(childTable, childResult.rows as Row[], expand.expand, signal);
    }

    stitch(parentRows, parentKeyColumns, childResult.rows, childKeyColumns, expand, isToMany);
  }
}

function resolveNavigation(
  schema: SchemaSnapshot,
  parent: TableModel,
  navigation: string,
): { childTable: TableModel; fk: ForeignKeyModel; isToMany: boolean } | undefined {
  // To-one: FK declared on the parent
  const toOne = parent.foreignKeys.find((f) => f.navToOne === navigation);
  if (toOne !== undefined) {
    const target = schema.tables.find((t) => t.exposedName === toOne.refTable);
    if (target) return { childTable: target, fk: toOne, isToMany: false };
  }

  // To-many: FK on some child referencing the parent
  for (const candidate of schema.tables) {
    const fk = candidate.foreignKeys.find(
      (f) => f.refTable === parent.exposedName && f.navToMany === navigation,
    );
    if (fk !== undefined) return { childTable: candidate, fk, isToMany: true };
  }

  return undefined;
}

function buildKeyInFilter(keyColumns: readonly string[], keyValues: unknown[][]): FilterNode {
  if (keyColumns.length === 1) {
    const distinct = [...new Set(keyValues.map((v) => v[0]))];
    return inList(
      fieldRef(keyColumns[0]!),
      distinct.map((v) => constant(v)),
    );
  }

  // Composite key: OR of AND-equality tuples
  const ors = keyValues.map((tuple) => {
    let and: FilterNode | undefined;
    for (let i = 0; i < keyColumns.length; i++) {
      const cmp = comparison(fieldRef(keyColumns[i]!), "eq", constant(tuple[i]));
      and = and === undefined ? cmp : logical("and", [and, cmp]);
    }
    return and!;
  });

  return ors.length === 1 ? ors[0]! : logical("or", ors);
}

function stitch(
  parentRows: readonly Row[],
  parentKeyColumns: readonly string[],
  childRows: readonly Row[],
  childKeyColumns: readonly string[],
  expand: ExpandNode,
  isToMany: boolean,
): void {
  const grouped = new Map<string, Row[]>();
  for (const child of childRows) {
    const key = keyString(childKeyColumns.map((c) => child.get(c)));
    let list = grouped.get(key);
    if (list === undefined) {
      list = [];
      grouped.set(key, list);
    }
    list.push(child);
  }

  for (const parent of parentRows) {
    const key = keyString(parentKeyColumns.map((c) => parent.get(c)));
    let children = grouped.get(key) ?? [];

    if (isToMany) {
      let slice: Row[] = children;
      if (expand.skip !== undefined) slice = slice.slice(expand.skip);
      if (expand.top !== undefined) slice = slice.slice(0, expand.top);
      parent.set(expand.navigation, slice);
    } else {
      parent.set(expand.navigation, children.length > 0 ? children[0]! : null);
    }
  }
}

function keyString(values: unknown[]): string {
  return values.map((v) => (v === null || v === undefined ? "\0" : String(v))).join("\x01");
}
