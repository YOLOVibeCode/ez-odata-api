import type { FilterFunction, QueryRequest } from "../core/query.js";
import type { QueryResult } from "../core/result.js";
import type { SchemaSnapshot } from "../core/schema.js";
import type { ServiceOptions, ServiceStatus } from "../core/services.js";
import type { WriteRequest, WriteResult } from "../core/write.js";

/**
 * Segregated connector role interfaces (ISP, spec 02 §3.1) - port of
 * EzOdata.Connectors.Abstractions. Each capability is its own interface so a
 * read path can depend on QueryExecutor alone and never see write surface.
 */

export interface TlsSpec {
  /** "disable" | "prefer" | "require". */
  readonly mode: string;
  readonly caCertPem?: string;
  readonly allowInvalid: boolean;
}

/** Decrypted connection details (spec 04 §3). Never serialized to responses/logs/audit. */
export interface ConnectionSpec {
  readonly host?: string;
  readonly port?: number;
  readonly database?: string;
  readonly username?: string;
  readonly password?: string;
  readonly tls: TlsSpec;
  /** SQLite only. */
  readonly filePath?: string;
  readonly readOnlyFile?: boolean;
  /** Whitelisted provider keywords only - validated per connector (spec 04 §3). */
  readonly extra: Readonly<Record<string, string>>;
}

export type ConnectionTestCategory =
  | "ok"
  | "authFailed"
  | "unreachable"
  | "tlsError"
  | "databaseMissing"
  | "other";

export interface ConnectionTestResult {
  readonly ok: boolean;
  readonly category: ConnectionTestCategory;
  readonly message: string;
  readonly serverVersion?: string;
}

/** Derived from ServiceOptions for introspection runs (spec 04 §4). */
export interface IntrospectionOptions {
  readonly includeSchemas: readonly string[];
  readonly excludeTables: readonly string[];
  readonly includeViews: boolean;
  /** "original" or "pascal" (spec 04 §4.5). */
  readonly exposedNameStyle: string;
}

/** Per-call execution settings, sourced from service options. */
export interface ExecutionOptions {
  readonly commandTimeoutSeconds: number;
  /** Rows fetched = limit + 1 to detect hasMore without COUNT. */
  readonly rowLimit: number;
}

/** Everything a connector needs to run one read operation. */
export interface QueryExecution {
  readonly connection: ConnectionSpec;
  readonly schema: SchemaSnapshot;
  readonly query: QueryRequest;
  readonly options: ExecutionOptions;
}

/** Everything a connector needs to run one write operation. */
export interface WriteExecution {
  readonly connection: ConnectionSpec;
  readonly schema: SchemaSnapshot;
  readonly write: WriteRequest;
  readonly options: ExecutionOptions;
}

/** How generated keys/rows come back from INSERT/UPDATE (spec 04 §7.3). */
export type ReturningMode = "returningSuffix" | "outputClause" | "none";

// ---- Segregated capability interfaces ----

/** Connection testing only. Must return within 10 seconds (CON-5). */
export interface ConnectionTester {
  test(spec: ConnectionSpec, signal?: AbortSignal): Promise<ConnectionTestResult>;
}

/** Schema discovery only (spec 04 §2). */
export interface SchemaIntrospector {
  introspect(spec: ConnectionSpec, options: IntrospectionOptions, signal?: AbortSignal): Promise<SchemaSnapshot>;
}

/** Reads only. The ONLY connector surface read paths see. */
export interface QueryExecutor {
  query(execution: QueryExecution, signal?: AbortSignal): Promise<QueryResult>;
  count(execution: QueryExecution, signal?: AbortSignal): Promise<number>;
}

/**
 * Writes only (spec 02 §3.1). Absent for read-only engines/configurations -
 * unable to write by type, not by guard.
 */
export interface WriteExecutor {
  /** One write (single or bulk records); transactional per call (spec 04 §7.3). */
  write(execution: WriteExecution, signal?: AbortSignal): Promise<WriteResult>;
  /** Multiple writes in ONE transaction - $batch changesets (spec 05 §6). */
  writeAtomic(executions: readonly WriteExecution[], signal?: AbortSignal): Promise<readonly WriteResult[]>;
}

/**
 * Engine-specific SQL differences (spec 04 §2, §7). The shared SQL compiler owns
 * structure and parameterization; dialects own syntax.
 */
export interface SqlDialect {
  quoteIdentifier(identifier: string): string;
  /** Appends LIMIT/OFFSET (PG/MySQL/SQLite) or OFFSET..FETCH (MSSQL). */
  paginate(sql: string, limit: number | undefined, offset: number | undefined): string;
  /** Renders one filter function call, e.g. contains -> ILIKE/LIKE. Args are rendered SQL fragments. */
  mapFunction(fn: FilterFunction, args: readonly string[]): string;
  /** True when string LIKE comparisons are case-insensitive for this service (spec 04 §7.2). */
  readonly caseInsensitiveLike: boolean;
  readonly returning: ReturningMode;
}

/** Everything the protocol engines need to serve one service (port of ServiceRuntime). */
export interface ServiceRuntime {
  readonly name: string;
  readonly connectorType: string;
  readonly connection: ConnectionSpec;
  readonly schema: SchemaSnapshot;
  readonly options: ServiceOptions;
  readonly schemaVersion: string;
  readonly status: ServiceStatus;
}

/** Resolves a service by URL slug; undefined = unknown service (404 upstream). */
export interface ServiceRuntimeResolver {
  resolve(serviceName: string, signal?: AbortSignal): Promise<ServiceRuntime | undefined>;
}

// ---- Connector-layer error taxonomy ----

/** Raised when a request references unknown tables/fields or unsupported constructs. */
export class QueryValidationError extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = "QueryValidationError";
    this.code = code;
  }
}

/** Raised for constructs the platform intentionally does not support (spec 05 OD-9: fail loudly). */
export class NotSupportedQueryError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "NotSupportedQueryError";
  }
}
