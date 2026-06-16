/** Service identity, options, and lifecycle (port of src/EzOdata.Core/Services). */

/** Connector type keys (spec 03 §2.1). */
export const ConnectorTypes = {
  PostgreSql: "postgresql",
  MySql: "mysql",
  SqlServer: "sqlserver",
  Sqlite: "sqlite",
} as const;

export type ConnectorType = (typeof ConnectorTypes)[keyof typeof ConnectorTypes];

export const ALL_CONNECTOR_TYPES: readonly string[] = Object.values(ConnectorTypes);

export function isKnownConnector(type: string | undefined): boolean {
  return type !== undefined && ALL_CONNECTOR_TYPES.some((t) => t.toLowerCase() === type.toLowerCase());
}

/** Schema cache / service lifecycle states (spec 02 §7). */
export type ServiceStatus =
  | "pending"
  | "introspecting"
  | "active"
  | "failed"
  | "refreshing"
  | "disabled";

/** Per-service options persisted as services.options_json (spec 03 §2.1). */
export interface ServiceOptions {
  readonly includeSchemas: readonly string[];
  readonly excludeTables: readonly string[];
  readonly includeViews: boolean;
  readonly readOnly: boolean;
  readonly defaultPageSize: number;
  readonly maxPageSize: number;
  readonly commandTimeoutSeconds: number;
  readonly maxPoolSize: number;
  /** "original" (default) or "pascal" (spec 04 §4.5). */
  readonly exposedNameStyle: string;
  /** Columns that drive ETag optimistic concurrency (spec 05 §7). */
  readonly concurrencyColumns: readonly string[];
  /** $expand limits (spec 05 §4.4). */
  readonly maxExpandDepth: number;
  readonly maxExpandWidth: number;
  /** $search opt-in (spec 05 §4.6); off by default. */
  readonly enableSearch: boolean;
}

export const DEFAULT_SERVICE_OPTIONS: ServiceOptions = {
  includeSchemas: [],
  excludeTables: [],
  includeViews: true,
  readOnly: false,
  defaultPageSize: 25,
  maxPageSize: 1000,
  commandTimeoutSeconds: 30,
  maxPoolSize: 50,
  exposedNameStyle: "original",
  concurrencyColumns: [],
  maxExpandDepth: 3,
  maxExpandWidth: 10,
  enableSearch: false,
};

/** Validation (port of ServiceOptions.Error); null = valid. Kept a function to keep the type pure-data. */
export function serviceOptionsError(options: ServiceOptions): string | null {
  if (options.defaultPageSize < 1 || options.defaultPageSize > 10_000) {
    return "defaultPageSize must be between 1 and 10000";
  }
  if (options.maxPageSize < options.defaultPageSize || options.maxPageSize > 100_000) {
    return "maxPageSize must be >= defaultPageSize and <= 100000";
  }
  if (options.commandTimeoutSeconds < 1 || options.commandTimeoutSeconds > 3600) {
    return "commandTimeoutSeconds must be between 1 and 3600";
  }
  if (options.maxPoolSize < 1 || options.maxPoolSize > 1000) {
    return "maxPoolSize must be between 1 and 1000";
  }
  if (options.exposedNameStyle !== "original" && options.exposedNameStyle !== "pascal") {
    return "exposedNameStyle must be 'original' or 'pascal'";
  }
  return null;
}
