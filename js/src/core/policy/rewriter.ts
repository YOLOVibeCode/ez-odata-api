import { logical, type FieldArg, type FilterNode, type QueryRequest } from "../query.js";
import type { TableModel } from "../schema.js";
import type { PolicyDecision } from "./decision.js";

/**
 * Applies a policy decision to a QueryRequest (port of
 * src/EzOdata.Core/Policy/QueryPolicyRewriter.cs, spec 08 §4 steps 6–8).
 * Security is enforced by construction: restricted fields are removed from
 * projection and the row filter is always AND-ed in.
 */

export class FieldDeniedError extends Error {
  readonly field: string;

  constructor(field: string) {
    super(`Access to field '${field}' is denied.`);
    this.name = "FieldDeniedError";
    this.field = field;
  }
}

export function rewrite(query: QueryRequest, decision: PolicyDecision, table: TableModel): QueryRequest {
  if (decision.bypass) return query;

  const unreadable = new Set<string>([...decision.deniedFields, ...decision.writeOnlyFields]);

  // Masked fields are not filterable/sortable — also removed from SQL projection.
  const unfilterable = new Set<string>([...unreadable, ...decision.maskedFields.keys()]);

  // 1. Validate client filter — every root field must be readable/filterable.
  if (query.filter !== undefined) {
    for (const field of rootFields(query.filter)) {
      if (unfilterable.has(field)) throw new FieldDeniedError(field);
    }
  }

  // 2. Validate orderby.
  for (const item of query.orderBy) {
    if (unfilterable.has(item.field)) throw new FieldDeniedError(item.field);
  }

  // 3. Projection: explicit $select of a denied field → error; masked fields are
  //    allowed in $select but stripped from SQL. Implicit select = all readable.
  let select: readonly string[];
  if (query.select === undefined) {
    select = table.columns
      .map((c) => c.exposedName)
      .filter((c) => !unreadable.has(c) && !decision.maskedFields.has(c));
  } else {
    for (const field of query.select) {
      if (unreadable.has(field)) throw new FieldDeniedError(field);
    }
    select = query.select.filter((f) => !decision.maskedFields.has(f));
    if (select.length === 0) {
      select = table.primaryKey.length > 0 ? table.primaryKey : [table.columns[0]!.exposedName];
    }
  }

  // 4. Row filter AND-ed into the client filter.
  const combinedFilter =
    query.filter === undefined && decision.rowFilter === undefined
      ? undefined
      : query.filter === undefined
        ? decision.rowFilter
        : decision.rowFilter === undefined
          ? query.filter
          : logical("and", [query.filter, decision.rowFilter]);

  return {
    ...query,
    select,
    ...(combinedFilter !== undefined ? { filter: combinedFilter } : {}),
  } as QueryRequest;
}

/** Fields the serializer must inject as mask literals for this request. */
export function masksToApply(
  originalQuery: QueryRequest,
  decision: PolicyDecision,
): ReadonlyMap<string, string> {
  if (decision.maskedFields.size === 0) return new Map();

  if (originalQuery.select === undefined) return decision.maskedFields;

  const result = new Map<string, string>();
  for (const [key, val] of decision.maskedFields) {
    if (originalQuery.select.includes(key)) result.set(key, val);
  }
  return result;
}

// ---- FilterFieldWalker (used by rewrite and cross-table validation) ----

/** Yields root-table field names referenced in a filter tree. */
export function* rootFields(node: FilterNode): Iterable<string> {
  switch (node.kind) {
    case "comparison":
      if (node.field.path.length === 1) yield node.field.path[0]!;
      break;
    case "in":
      if (node.field.path.length === 1) yield node.field.path[0]!;
      break;
    case "function":
      for (const arg of node.args) {
        if (arg.kind === "field" && arg.field.path.length === 1) yield arg.field.path[0]!;
      }
      break;
    case "logical":
      for (const operand of node.operands) yield* rootFields(operand);
      break;
    case "not":
      yield* rootFields(node.operand);
      break;
    case "lambda":
      // Lambda navigations are not root fields; handled separately.
      break;
  }
}

/** Yields navigation names referenced in the filter (for cross-table checks). */
export function* navigations(node: FilterNode): Iterable<string> {
  switch (node.kind) {
    case "comparison":
      if (node.field.path.length > 1) yield node.field.path[0]!;
      break;
    case "lambda":
      yield node.navigation;
      break;
    case "logical":
      for (const operand of node.operands) yield* navigations(operand);
      break;
    case "not":
      yield* navigations(node.operand);
      break;
    case "function":
      for (const arg of node.args as readonly FieldArg[]) {
        if (arg.kind === "field" && arg.field.path.length > 1) yield arg.field.path[0]!;
      }
      break;
  }
}
