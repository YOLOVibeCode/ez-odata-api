/**
 * The shared Query IR (port of src/EzOdata.Core/Query/QueryIr.cs, spec 02 §6):
 * OData, REST, and MCP all parse into these types; the policy engine rewrites
 * them; connectors compile them to parameterized SQL.
 */

export type ComparisonOp = "eq" | "ne" | "gt" | "ge" | "lt" | "le";
export type LogicalOp = "and" | "or";
export type LambdaKind = "any" | "all";
export type AggregateOp = "sum" | "average" | "min" | "max" | "countDistinct" | "count";

export type FilterFunction =
  | "contains"
  | "startsWith"
  | "endsWith"
  | "toLower"
  | "toUpper"
  | "trim"
  | "length"
  | "indexOf"
  | "substring"
  | "concat"
  | "year"
  | "month"
  | "day"
  | "hour"
  | "minute"
  | "second"
  | "date"
  | "time"
  | "now"
  | "round"
  | "floor"
  | "ceiling"
  | "add"
  | "sub"
  | "mul"
  | "div"
  | "mod";

/** A field path: bare column, or to-one navigation path of depth <= 2 (e.g. customer/country). */
export interface FieldRef {
  readonly path: readonly string[];
}

export function fieldRef(...path: string[]): FieldRef {
  return { path };
}

export function leaf(ref: FieldRef): string {
  return ref.path[ref.path.length - 1]!;
}

export function isNavigated(ref: FieldRef): boolean {
  return ref.path.length > 1;
}

export function fieldRefToString(ref: FieldRef): string {
  return ref.path.join("/");
}

export function fieldRefEquals(a: FieldRef, b: FieldRef): boolean {
  return a.path.length === b.path.length && a.path.every((p, i) => p === b.path[i]);
}

/** Typed constant; value is null for the null literal. */
export interface ConstantValue {
  readonly value: unknown;
}

export const NULL_CONSTANT: ConstantValue = { value: null };

export function constant(value: unknown): ConstantValue {
  return { value };
}

// ---- Filter expression tree ----

export interface ComparisonNode {
  readonly kind: "comparison";
  readonly field: FieldRef;
  readonly op: ComparisonOp;
  readonly value: ConstantValue;
}

export interface LogicalNode {
  readonly kind: "logical";
  readonly op: LogicalOp;
  readonly operands: readonly FilterNode[];
}

export interface NotNode {
  readonly kind: "not";
  readonly operand: FilterNode;
}

export interface InNode {
  readonly kind: "in";
  readonly field: FieldRef;
  readonly values: readonly ConstantValue[];
}

/** String/date/math function comparisons, e.g. contains(name,'x'), year(created) eq 2026. */
export interface FunctionNode {
  readonly kind: "function";
  readonly fn: FilterFunction;
  readonly args: readonly FilterArg[];
  readonly op?: ComparisonOp;
  readonly comparand?: ConstantValue;
}

/** any/all over a to-many navigation (spec 05 §4.3); predicate is undefined for bare any(). */
export interface LambdaNode {
  readonly kind: "lambda";
  readonly navigation: string;
  readonly lambdaKind: LambdaKind;
  readonly predicate?: FilterNode;
}

export type FilterNode =
  | ComparisonNode
  | LogicalNode
  | NotNode
  | InNode
  | FunctionNode
  | LambdaNode;

export interface FieldArg {
  readonly kind: "field";
  readonly field: FieldRef;
}

export interface ConstantArg {
  readonly kind: "constant";
  readonly value: ConstantValue;
}

export type FilterArg = FieldArg | ConstantArg;

// ---- Node constructors (mirror the C# record ctors) ----

export function comparison(field: FieldRef, op: ComparisonOp, value: ConstantValue): ComparisonNode {
  return { kind: "comparison", field, op, value };
}

export function logical(op: LogicalOp, operands: readonly FilterNode[]): LogicalNode {
  return { kind: "logical", op, operands };
}

export function not(operand: FilterNode): NotNode {
  return { kind: "not", operand };
}

export function inList(field: FieldRef, values: readonly ConstantValue[]): InNode {
  return { kind: "in", field, values };
}

export function fn(
  func: FilterFunction,
  args: readonly FilterArg[],
  op?: ComparisonOp,
  comparand?: ConstantValue,
): FunctionNode {
  return {
    kind: "function",
    fn: func,
    args,
    ...(op !== undefined ? { op } : {}),
    ...(comparand !== undefined ? { comparand } : {}),
  };
}

export function lambda(navigation: string, kind: LambdaKind, predicate?: FilterNode): LambdaNode {
  return {
    kind: "lambda",
    navigation,
    lambdaKind: kind,
    ...(predicate !== undefined ? { predicate } : {}),
  };
}

export function fieldArg(field: FieldRef): FieldArg {
  return { kind: "field", field };
}

export function constantArg(value: ConstantValue): ConstantArg {
  return { kind: "constant", value };
}

// ---- Query request shape ----

export interface OrderByItem {
  readonly field: string;
  readonly descending: boolean;
}

export interface ExpandNode {
  readonly navigation: string;
  readonly filter?: FilterNode;
  readonly select?: readonly string[];
  readonly orderBy: readonly OrderByItem[];
  readonly expand: readonly ExpandNode[];
  readonly top?: number;
  readonly skip?: number;
}

/** Opaque keyset pagination state (spec 05 §4.2), decoded from a signed skiptoken. */
export interface KeysetCursor {
  readonly lastOrderByValues: readonly unknown[];
  readonly lastKeyValues: readonly unknown[];
}

export interface Aggregation {
  readonly op: AggregateOp;
  readonly field?: string;
  readonly alias: string;
}

/** Supported $apply subset (spec 05 §4.5): groupby with aggregates, or a bare aggregate. */
export interface ApplyClause {
  readonly groupBy: readonly string[];
  readonly aggregations: readonly Aggregation[];
}

export interface QueryRequest {
  readonly serviceName: string;
  readonly table: string;
  readonly filter?: FilterNode;
  readonly orderBy: readonly OrderByItem[];
  /** undefined = all permitted fields. */
  readonly select?: readonly string[];
  readonly expand: readonly ExpandNode[];
  readonly top?: number;
  readonly skip?: number;
  readonly count: boolean;
  readonly cursor?: KeysetCursor;
  /** $apply transformation (spec 05 §4.5); undefined = normal query. */
  readonly apply?: ApplyClause;
  /** $search term compiled to OR-ed contains across searchable columns (spec 05 §4.6). */
  readonly search?: string;
}
