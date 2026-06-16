import type { FilterNode } from "./query.js";
import type { Row } from "./result.js";

/** Write IR (port of src/EzOdata.Core/Query/WriteIr.cs, spec 02 §6). */
export type WriteKind = "insert" | "update" | "replace" | "delete";

/** One record's writable values, already validated/coerced (spec 02 §6). */
export interface RecordPayload {
  /** Column -> typed value (null = explicit SQL NULL). */
  readonly values: Readonly<Record<string, unknown>>;
  /** Deep insert (spec 05 §5.1): to-many navigation -> child records, one level deep. */
  readonly children: Readonly<Record<string, readonly RecordPayload[]>>;
}

/** Key column -> value for single-record operations. */
export interface KeyPredicate {
  readonly values: Readonly<Record<string, unknown>>;
}

export interface WriteRequest {
  readonly serviceName: string;
  readonly table: string;
  readonly kind: WriteKind;
  readonly records: readonly RecordPayload[];
  readonly key?: KeyPredicate;
  /** Row filter and/or concurrency predicate AND-ed into UPDATE/DELETE (spec 08 §5.4). */
  readonly precondition?: FilterNode;
  /** Insert-time row filter check (spec 08 §5.4): inserted rows must satisfy it or roll back. */
  readonly insertVisibilityFilter?: FilterNode;
}

export interface WriteResult {
  readonly affectedCount: number;
  readonly records: readonly Row[];
  readonly errorCode?: string;
  readonly errorDetail?: string;
}

export function writeSucceeded(result: WriteResult): boolean {
  return result.errorCode === undefined;
}

/** Connector error taxonomy (spec 04 §8): provider exceptions map to these. */
export class ConnectorError extends Error {
  readonly code: string;
  readonly isTransient: boolean;

  constructor(code: string, safeMessage: string, options: { isTransient?: boolean; cause?: unknown } = {}) {
    super(safeMessage, options.cause !== undefined ? { cause: options.cause } : undefined);
    this.name = "ConnectorError";
    this.code = code;
    this.isTransient = options.isTransient ?? false;
  }
}
