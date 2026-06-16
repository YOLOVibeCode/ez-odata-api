/**
 * MCP server (port of McpServer.cs, spec 09): JSON-RPC 2.0 over HTTP.
 * Tools are derived per-identity from the caller's role — AI clients only see
 * what their key permits. Every tool call flows through the same policy engine
 * and connectors as HTTP (MCP-3).
 */

import { parseFilter, FilterParseError } from "../core/filter-parser.js";
import {
  type FilterNode,
  type OrderByItem,
} from "../core/query.js";
import { Row } from "../core/result.js";
import { findTable, type SchemaSnapshot, type TableModel } from "../core/schema.js";
import { trimSnapshot } from "../core/policy/trimmer.js";
import { FULL_ACCESS, type PolicyDecision } from "../core/policy/decision.js";
import { FieldDeniedError, rewrite, masksToApply } from "../core/policy/rewriter.js";
import type { PolicyEngine } from "../core/policy/engine.js";
import type { RowFilterParser } from "../core/policy/contracts.js";
import { Verb, type RequestIdentity, type RoleRuleSet } from "../core/policy/model.js";
import { PayloadBinder, BindError } from "../core/payload-binder.js";
import { ConnectorError } from "../core/write.js";
import type { QueryExecutor, WriteExecutor, ServiceRuntime } from "../connectors/contracts.js";
import { QueryValidationError } from "../connectors/contracts.js";
import { DEFAULT_SERVICE_OPTIONS } from "../core/services.js";

export interface McpOptions {
  readonly enabled: boolean;
  readonly allowWrites: boolean;
  readonly maxRowsPerCall: number;
}

export const DEFAULT_MCP_OPTIONS: McpOptions = {
  enabled: true,
  allowWrites: false,
  maxRowsPerCall: 200,
};

/** Function that resolves visible service names (e.g. for the services dropdown). */
export type VisibleServicesProvider = (signal?: AbortSignal) => Promise<readonly string[]>;

export interface McpHandlerConfig {
  resolveRuntime: (name: string, signal?: AbortSignal) => Promise<ServiceRuntime | undefined>;
  getQueryExecutor: (runtime: ServiceRuntime) => QueryExecutor;
  getWriteExecutor: (runtime: ServiceRuntime) => WriteExecutor | undefined;
  policyEngine: PolicyEngine;
  resolveRoleRules: (identity: RequestIdentity, runtime: ServiceRuntime) => Promise<readonly RoleRuleSet[]>;
  rowFilterParser: RowFilterParser;
  /** Provider resolved at request time; type-aliased to keep the interface below the ISP budget. */
  visibleServices: VisibleServicesProvider;
  options?: McpOptions;
}

export class McpToolError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "McpToolError";
  }
}

export type JsonRpcRequest = Record<string, unknown>;
export type JsonRpcResponse = Record<string, unknown>;

export class McpServer {
  private readonly options: McpOptions;

  constructor(private readonly config: McpHandlerConfig) {
    this.options = config.options ?? DEFAULT_MCP_OPTIONS;
  }

  async handle(
    request: JsonRpcRequest,
    identity: RequestIdentity,
    signal?: AbortSignal,
  ): Promise<JsonRpcResponse | null> {
    const id = request["id"] as unknown;
    const method = request["method"] as string | undefined;

    try {
      switch (method) {
        case "initialize":
          return result(id, initializeResult());
        case "notifications/initialized":
          return null;
        case "ping":
          return result(id, {});
        case "tools/list":
          return result(id, await this.toolsList(identity, signal));
        case "tools/call":
          return result(id, await this.toolsCall(request["params"] as Record<string, unknown> | undefined, identity, signal));
        default:
          return errorResponse(id, -32601, `Method '${method}' not found.`);
      }
    } catch (err) {
      if (err instanceof McpToolError) return result(id, toolError(err.message));
      if (err instanceof FieldDeniedError) return result(id, toolError(`Forbidden.FieldDenied: ${err.message}`));
      if (err instanceof FilterParseError) return result(id, toolError(`${err.errorCode}: ${err.message}`));
      if (err instanceof BindError) return result(id, toolError(`${err.errorCode}: ${err.message}`));
      if (err instanceof QueryValidationError) return result(id, toolError(`${err.code}: ${err.message}`));
      if (err instanceof ConnectorError) return result(id, toolError(`${err.code}: ${err.message}`));
      return errorResponse(id, -32603, "Internal error.");
    }
  }

  private async toolsList(identity: RequestIdentity, signal?: AbortSignal): Promise<Record<string, unknown>> {
    const tools: Record<string, unknown>[] = [
      tool("list_services", "List data services visible to you.", emptySchema()),
      tool("explain_filter_syntax", "Return the filter grammar cheat-sheet for query tools.", emptySchema()),
    ];

    for (const serviceName of await this.config.visibleServices(signal)) {
      const runtime = await this.config.resolveRuntime(serviceName, signal);
      if (runtime === undefined || (runtime.status !== "active" && runtime.status !== "refreshing")) continue;

      const roleRules = await this.config.resolveRoleRules(identity, runtime);
      const visible = trimSnapshot(
        runtime.schema, identity, roleRules, runtime.name,
        this.config.policyEngine, this.config.rowFilterParser,
      );
      if (visible.tables.length === 0) continue;

      const tableNames = visible.tables.map((t) => t.exposedName);
      const p = serviceName;

      tools.push(tool(`${p}_list_tables`, `List tables in the '${p}' service.`, emptySchema()));
      tools.push(tool(`${p}_describe_table`, `Describe a table's columns in '${p}'.`, tableArgSchema(tableNames)));
      tools.push(tool(`${p}_query`, `Query a table in '${p}' with an optional filter.`, querySchema(tableNames)));
      tools.push(tool(`${p}_count`, `Count rows in a table in '${p}'.`, countSchema(tableNames)));

      const hasWritable = visible.tables.some((t) => t.writable);
      const writeExec = this.config.getWriteExecutor(runtime);
      if (this.options.allowWrites && hasWritable && writeExec !== undefined) {
        tools.push(tool(`${p}_insert`, `Insert a record into a table in '${p}'.`, writeSchema(tableNames)));
        tools.push(tool(`${p}_update`, `Update a record by key in '${p}'.`, writeSchema(tableNames)));
        tools.push(tool(`${p}_delete`, `Delete a record by key in '${p}' (requires confirm:true).`, deleteSchema(tableNames)));
      }
    }

    return { tools };
  }

  private async toolsCall(
    params: Record<string, unknown> | undefined,
    identity: RequestIdentity,
    signal?: AbortSignal,
  ): Promise<Record<string, unknown>> {
    const name = params?.["name"] as string | undefined;
    if (!name) throw new McpToolError("Tool name is required.");

    const args = (params?.["arguments"] as Record<string, unknown> | undefined) ?? {};

    if (name === "list_services") {
      const names = await this.config.visibleServices(signal);
      return toolResult({ services: names });
    }

    if (name === "explain_filter_syntax") {
      return toolResult({ syntax: FILTER_SYNTAX_HELP });
    }

    const serviceName = await this.resolveServicePrefix(name, signal);
    if (serviceName === undefined) throw new McpToolError(`Unknown tool '${name}'.`);
    const verb = name.slice(serviceName.length + 1);

    const runtime = await this.config.resolveRuntime(serviceName, signal);
    if (runtime === undefined) throw new McpToolError(`Service '${serviceName}' is unavailable.`);

    const roleRules = await this.config.resolveRoleRules(identity, runtime);

    switch (verb) {
      case "list_tables":
        return this.listTables(runtime, identity, roleRules);
      case "describe_table":
        return this.describeTable(runtime, identity, roleRules, args);
      case "query":
        return await this.query(runtime, identity, roleRules, args, signal);
      case "count":
        return await this.count(runtime, identity, roleRules, args, signal);
      case "insert":
        return await this.insert(runtime, identity, roleRules, args, signal);
      case "update":
        return await this.update(runtime, identity, roleRules, args, signal);
      case "delete":
        return await this.delete(runtime, identity, roleRules, args, signal);
      default:
        throw new McpToolError(`Unknown tool '${name}'.`);
    }
  }

  private async resolveServicePrefix(
    toolName: string,
    signal?: AbortSignal,
  ): Promise<string | undefined> {
    for (const service of await this.config.visibleServices(signal)) {
      if (toolName.startsWith(service + "_")) return service;
    }
    return undefined;
  }

  private listTables(
    runtime: ServiceRuntime,
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
  ): Record<string, unknown> {
    const visible = trimSnapshot(
      runtime.schema, identity, roleRules, runtime.name,
      this.config.policyEngine, this.config.rowFilterParser,
    );
    const tables = visible.tables.map((t) => ({
      name: t.exposedName,
      isView: t.isView,
      description: t.comment ?? null,
    }));
    return toolResult({ tables });
  }

  private describeTable(
    runtime: ServiceRuntime,
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
    args: Record<string, unknown>,
  ): Record<string, unknown> {
    const tableName = args["table"] as string | undefined;
    if (!tableName) throw new McpToolError("'table' is required.");
    const table = findTable(runtime.schema, tableName);
    if (table === undefined) throw new McpToolError(`Unknown table '${tableName}'.`);

    const decision = this.authorize(runtime, identity, roleRules, table, Verb.Get);
    if (!decision.allowed) throw new McpToolError(`Table '${tableName}' is not accessible.`);

    const columns = table.columns
      .filter((c) => !decision.deniedFields.has(c.exposedName) && !decision.writeOnlyFields.has(c.exposedName))
      .map((c) => ({
        name: c.exposedName,
        type: c.edmType,
        nullable: c.nullable,
        primaryKey: c.isPrimaryKey,
        masked: decision.maskedFields.has(c.exposedName),
        allowedValues: c.allowedValues ?? null,
        description: c.comment ?? null,
      }));

    return toolResult({ table: tableName, columns });
  }

  private async query(
    runtime: ServiceRuntime,
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
    args: Record<string, unknown>,
    signal?: AbortSignal,
  ): Promise<Record<string, unknown>> {
    const tableName = args["table"] as string | undefined;
    if (!tableName) throw new McpToolError("'table' is required.");
    const table = findTable(runtime.schema, tableName);
    if (table === undefined) throw new McpToolError(`Unknown table '${tableName}'.`);

    const decision = this.authorize(runtime, identity, roleRules, table, Verb.Get);
    if (!decision.allowed) throw new McpToolError(`Table '${tableName}' is not accessible.`);

    const filterStr = args["filter"] as string | undefined;
    let filter: FilterNode | undefined;
    if (filterStr && filterStr.length > 0) filter = parseFilter(filterStr);

    const limit = Math.min((args["limit"] as number | undefined) ?? 25, this.options.maxRowsPerCall);
    const fields = Array.isArray(args["fields"]) ? (args["fields"] as string[]) : undefined;

    const rawQuery = {
      serviceName: runtime.name,
      table: table.exposedName,
      ...(filter !== undefined ? { filter } : {}),
      ...(fields !== undefined ? { select: fields } : {}),
      orderBy: parseOrder(args["order"] as string | undefined),
      expand: [],
      count: false,
      top: limit,
    };

    const rewritten = rewrite(rawQuery, decision, table);
    const masks = masksToApply(rawQuery, decision);
    const executor = this.config.getQueryExecutor(runtime);
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;

    const queryResult = await executor.query({
      connection: runtime.connection,
      schema: runtime.schema,
      query: rewritten,
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: limit + 1 },
    }, signal);

    const rows = (queryResult.rows as Row[]).map((row) => {
      const obj: Record<string, unknown> = {};
      for (const [k, v] of row.values) {
        obj[k] = masks.has(k) ? masks.get(k)! : toJson(v);
      }
      return obj;
    });

    return toolResult({ rows, rowCount: rows.length, truncated: queryResult.hasMore });
  }

  private async count(
    runtime: ServiceRuntime,
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
    args: Record<string, unknown>,
    signal?: AbortSignal,
  ): Promise<Record<string, unknown>> {
    const tableName = args["table"] as string | undefined;
    if (!tableName) throw new McpToolError("'table' is required.");
    const table = findTable(runtime.schema, tableName);
    if (table === undefined) throw new McpToolError(`Unknown table '${tableName}'.`);

    const decision = this.authorize(runtime, identity, roleRules, table, Verb.Get);
    if (!decision.allowed) throw new McpToolError(`Table '${tableName}' is not accessible.`);

    const filterStr = args["filter"] as string | undefined;
    let filter: FilterNode | undefined;
    if (filterStr && filterStr.length > 0) filter = parseFilter(filterStr);

    const rawQuery = { serviceName: runtime.name, table: table.exposedName, ...(filter !== undefined ? { filter } : {}), orderBy: [], expand: [], count: false };
    const rewritten = rewrite(rawQuery, decision, table);
    const executor = this.config.getQueryExecutor(runtime);
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;

    const countVal = await executor.count({
      connection: runtime.connection,
      schema: runtime.schema,
      query: rewritten,
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
    }, signal);

    return toolResult({ count: countVal });
  }

  private async insert(
    runtime: ServiceRuntime,
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
    args: Record<string, unknown>,
    signal?: AbortSignal,
  ): Promise<Record<string, unknown>> {
    const { table, decision } = this.authorizeWrite(runtime, identity, roleRules, args, Verb.Post);
    const record = bindRecord(args["record"], table, runtime.schema, decision);
    const executor = this.config.getWriteExecutor(runtime)!;
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;

    const result = await executor.write({
      connection: runtime.connection,
      schema: runtime.schema,
      write: {
        serviceName: runtime.name,
        table: table.exposedName,
        kind: "insert",
        records: [record],
        ...(decision.rowFilter !== undefined ? { insertVisibilityFilter: decision.rowFilter } : {}),
      },
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
    }, signal);

    return toolResult({ inserted: result.affectedCount, record: rowToJson(result.records, decision) });
  }

  private async update(
    runtime: ServiceRuntime,
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
    args: Record<string, unknown>,
    signal?: AbortSignal,
  ): Promise<Record<string, unknown>> {
    const { table, decision } = this.authorizeWrite(runtime, identity, roleRules, args, Verb.Patch);
    const record = bindRecord(args["record"], table, runtime.schema, decision);
    const key = bindKey(args["key"], table);
    const executor = this.config.getWriteExecutor(runtime)!;
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;

    const result = await executor.write({
      connection: runtime.connection,
      schema: runtime.schema,
      write: {
        serviceName: runtime.name,
        table: table.exposedName,
        kind: "update",
        records: [record],
        key: { values: key },
        ...(decision.rowFilter !== undefined ? { precondition: decision.rowFilter } : {}),
      },
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
    }, signal);

    if (result.affectedCount === 0) throw new McpToolError("Record not found or not visible.");
    return toolResult({ updated: result.affectedCount, record: rowToJson(result.records, decision) });
  }

  private async delete(
    runtime: ServiceRuntime,
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
    args: Record<string, unknown>,
    signal?: AbortSignal,
  ): Promise<Record<string, unknown>> {
    if (args["confirm"] !== true) {
      throw new McpToolError("Delete requires \"confirm\": true (MCP-6 safety gate).");
    }

    const { table, decision } = this.authorizeWrite(runtime, identity, roleRules, args, Verb.Delete);
    const key = bindKey(args["key"], table);
    const executor = this.config.getWriteExecutor(runtime)!;
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;

    const result = await executor.write({
      connection: runtime.connection,
      schema: runtime.schema,
      write: {
        serviceName: runtime.name,
        table: table.exposedName,
        kind: "delete",
        records: [],
        key: { values: key },
        ...(decision.rowFilter !== undefined ? { precondition: decision.rowFilter } : {}),
      },
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
    }, signal);

    if (result.affectedCount === 0) throw new McpToolError("Record not found or not visible.");
    return toolResult({ deleted: result.affectedCount });
  }

  private authorizeWrite(
    runtime: ServiceRuntime,
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
    args: Record<string, unknown>,
    verb: number,
  ): { table: TableModel; decision: PolicyDecision } {
    if (!this.options.allowWrites) throw new McpToolError("Write tools are disabled on this instance.");
    if (this.config.getWriteExecutor(runtime) === undefined || (runtime.options ?? DEFAULT_SERVICE_OPTIONS).readOnly) {
      throw new McpToolError("This service is read-only.");
    }

    const tableName = args["table"] as string | undefined;
    if (!tableName) throw new McpToolError("'table' is required.");
    const table = findTable(runtime.schema, tableName);
    if (table === undefined) throw new McpToolError(`Unknown table '${tableName}'.`);
    if (!table.writable) throw new McpToolError(`'${tableName}' is read-only.`);

    const decision = this.authorize(runtime, identity, roleRules, table, verb);
    if (!decision.allowed) throw new McpToolError(`You are not allowed to perform this operation on '${tableName}'.`);

    return { table, decision };
  }

  private authorize(
    runtime: ServiceRuntime,
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
    table: TableModel,
    verb: number,
  ): PolicyDecision {
    if (identity.bypass) return FULL_ACCESS;
    return this.config.policyEngine.authorize(
      identity,
      roleRules,
      runtime.name,
      table.exposedName,
      verb,
      table.columns.map((c) => c.exposedName),
      this.config.rowFilterParser,
    );
  }
}

// ---- JSON-RPC helpers ----

function result(id: unknown, res: unknown): JsonRpcResponse {
  return { jsonrpc: "2.0", id, result: res };
}

function errorResponse(id: unknown, code: number, message: string): JsonRpcResponse {
  return { jsonrpc: "2.0", id, error: { code, message } };
}

function toolResult(structured: Record<string, unknown>): Record<string, unknown> {
  return {
    content: [{ type: "text", text: JSON.stringify(structured) }],
    structuredContent: structured,
    isError: false,
  };
}

function toolError(message: string): Record<string, unknown> {
  return {
    content: [{ type: "text", text: message }],
    isError: true,
  };
}

function initializeResult(): Record<string, unknown> {
  return {
    protocolVersion: "2024-11-05",
    capabilities: { tools: { listChanged: true } },
    serverInfo: { name: "ez-odata-api", version: "1.0" },
  };
}

function tool(name: string, description: string, schema: unknown): Record<string, unknown> {
  return { name, description, inputSchema: schema };
}

function emptySchema(): unknown {
  return { type: "object", properties: {} };
}

function tableArgSchema(tables: string[]): unknown {
  return { type: "object", required: ["table"], properties: { table: tableEnum(tables) } };
}

function querySchema(tables: string[]): unknown {
  return {
    type: "object",
    required: ["table"],
    properties: {
      table: tableEnum(tables),
      filter: { type: "string", description: "SQL-ish filter, e.g. (status='open') and (total>250)" },
      fields: { type: "array", items: { type: "string" } },
      order: { type: "string", description: "e.g. 'created_at desc'" },
      limit: { type: "integer", maximum: 200, default: 25 },
    },
  };
}

function countSchema(tables: string[]): unknown {
  return { type: "object", required: ["table"], properties: { table: tableEnum(tables), filter: { type: "string" } } };
}

function writeSchema(tables: string[]): unknown {
  return {
    type: "object",
    required: ["table", "record"],
    properties: { table: tableEnum(tables), record: { type: "object" }, key: { type: "object" } },
  };
}

function deleteSchema(tables: string[]): unknown {
  return {
    type: "object",
    required: ["table", "key", "confirm"],
    properties: {
      table: tableEnum(tables),
      key: { type: "object" },
      confirm: { type: "boolean", description: "Must be true to delete." },
    },
  };
}

function tableEnum(tables: string[]): unknown {
  return { type: "string", enum: tables };
}

function parseOrder(order: string | undefined): readonly OrderByItem[] {
  if (!order || order.trim().length === 0) return [];
  return order.split(",").map((part) => {
    const bits = part.trim().split(/\s+/);
    return { field: bits[0]!, descending: bits[1]?.toLowerCase() === "desc" };
  });
}

function bindRecord(
  recordArg: unknown,
  table: TableModel,
  schema: SchemaSnapshot,
  decision: PolicyDecision,
): ReturnType<typeof PayloadBinder.bind> {
  if (recordArg === null || recordArg === undefined) throw new McpToolError("'record' is required.");
  const record = PayloadBinder.bind(recordArg, table, schema, { allowDeepInsert: false });
  for (const field of Object.keys(record.values)) {
    if (decision.deniedFields.has(field) || decision.maskedFields.has(field)) {
      throw new FieldDeniedError(field);
    }
  }
  return record;
}

function bindKey(keyArg: unknown, table: TableModel): Record<string, unknown> {
  if (typeof keyArg !== "object" || keyArg === null) throw new McpToolError("'key' object is required.");
  const obj = keyArg as Record<string, unknown>;
  const values: Record<string, unknown> = {};
  for (const keyColumn of table.primaryKey) {
    if (obj[keyColumn] === undefined || obj[keyColumn] === null) {
      throw new McpToolError(`Key column '${keyColumn}' is required.`);
    }
    const raw = obj[keyColumn];
    values[keyColumn] = typeof raw === "number" ? raw : String(raw);
  }
  return values;
}

function rowToJson(rows: readonly Row[], decision: PolicyDecision): Record<string, unknown> | null {
  if (rows.length === 0) return null;
  const obj: Record<string, unknown> = {};
  for (const [k, v] of (rows[0] as Row).values) {
    if (decision.deniedFields.has(k) || decision.writeOnlyFields.has(k)) continue;
    obj[k] = decision.maskedFields.has(k) ? decision.maskedFields.get(k)! : toJson(v);
  }
  return obj;
}

function toJson(value: unknown): unknown {
  if (value === null || value === undefined) return null;
  if (value instanceof Date) return value.toISOString();
  if (typeof value === "object" && "toISOString" in value) return (value as Date).toISOString();
  if (value instanceof Uint8Array || Buffer.isBuffer(value)) return Buffer.from(value).toString("base64");
  return value;
}

const FILTER_SYNTAX_HELP =
  "Filter grammar: field op value, combined with 'and'/'or'/'not' and parentheses. " +
  "Operators: = != > >= < <=, 'in (a,b)', 'is null', 'is not null', 'contains', 'starts with', 'ends with'. " +
  "Strings use single quotes (escape ' as ''). Example: (country='US') and (total>100).";
