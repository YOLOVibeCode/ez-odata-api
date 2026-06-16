/**
 * REST/JSON dialect engine (port of RestRequestHandler.cs, spec 06):
 * same Query IR, policy engine, and connectors as OData but plain JSON output.
 * Routes relative to service root: /_table, /_table/{name}, /_table/{name}/_schema,
 * /_table/{name}/{id}.
 */

import { ErrorCodes } from "../core/errors.js";
import { parseFilter, FilterParseError } from "../core/filter-parser.js";
import {
  comparison,
  constant,
  fieldRef,
  inList,
  logical,
  type FilterNode,
  type OrderByItem,
} from "../core/query.js";
import { Row } from "../core/result.js";
import { findTable, type TableModel } from "../core/schema.js";
import { trimSnapshot } from "../core/policy/trimmer.js";
import { FULL_ACCESS, type PolicyDecision } from "../core/policy/decision.js";
import { FieldDeniedError, rewrite, masksToApply } from "../core/policy/rewriter.js";
import type { PolicyEngine } from "../core/policy/engine.js";
import type { RowFilterParser } from "../core/policy/contracts.js";
import { Verb, type RequestIdentity, type RoleRuleSet } from "../core/policy/model.js";
import { PayloadBinder, BindError } from "../core/payload-binder.js";
import { ConnectorError } from "../core/write.js";
import type {
  QueryExecutor,
  WriteExecutor,
  ServiceRuntime,
} from "../connectors/contracts.js";
import { NotSupportedQueryError, QueryValidationError } from "../connectors/contracts.js";
import { DEFAULT_SERVICE_OPTIONS } from "../core/services.js";

export interface RestRequest {
  readonly method: string;
  /** Path segments after the service root, e.g. "_table/customers" or "_table/customers/42". */
  readonly path: string;
  readonly queryString: string;
  readonly identity: RequestIdentity;
  readonly headers: Readonly<Record<string, string>>;
  readonly body?: unknown;
}

export interface RestResponse {
  readonly status: number;
  readonly headers: Record<string, string>;
  readonly body: string;
  readonly contentType: string;
}

export interface RestHandlerConfig {
  resolveRuntime: (name: string, signal?: AbortSignal) => Promise<ServiceRuntime | undefined>;
  getQueryExecutor: (runtime: ServiceRuntime) => QueryExecutor;
  getWriteExecutor: (runtime: ServiceRuntime) => WriteExecutor | undefined;
  policyEngine: PolicyEngine;
  resolveRoleRules: (identity: RequestIdentity, runtime: ServiceRuntime) => Promise<readonly RoleRuleSet[]>;
  rowFilterParser: RowFilterParser;
}

export class RestHandler {
  constructor(private readonly config: RestHandlerConfig) {}

  async handleForService(
    serviceName: string,
    req: RestRequest,
    signal?: AbortSignal,
  ): Promise<RestResponse> {
    try {
      const runtime = await this.config.resolveRuntime(serviceName, signal);
      if (runtime === undefined) return restError(404, "NotFound", `Unknown service '${serviceName}'.`);
      if (runtime.status === "disabled") return restError(503, "ServiceUnavailable", "Service is disabled.");
      if (runtime.status !== "active" && runtime.status !== "refreshing" && runtime.status !== "introspecting") {
        return restError(503, "ServiceUnavailable", "Service schema is not ready yet.");
      }

      const roleRules = await this.config.resolveRoleRules(req.identity, runtime);
      const segments = req.path.trim().replace(/^\/|\/$/g, "").split("/").filter((s) => s.length > 0);

      if (segments.length === 1 && segments[0] === "_table") {
        return req.method === "GET" ? this.listTables(runtime, req.identity, roleRules) : methodNotAllowed();
      }

      if (segments.length === 2 && segments[0] === "_table") {
        const tableName = segments[1]!;
        switch (req.method) {
          case "GET": return await this.queryTable(runtime, req, roleRules, tableName, signal);
          case "POST": return await this.insert(runtime, req, roleRules, tableName, signal);
          case "PATCH": return await this.bulkUpdate(runtime, req, roleRules, tableName, signal);
          case "DELETE": return await this.bulkDelete(runtime, req, roleRules, tableName, signal);
          default: return methodNotAllowed();
        }
      }

      if (segments.length === 3 && segments[0] === "_table" && segments[2] === "_schema") {
        return req.method === "GET"
          ? this.describeTable(runtime, req.identity, roleRules, segments[1]!)
          : methodNotAllowed();
      }

      if (segments.length === 3 && segments[0] === "_table") {
        const tableName = segments[1]!;
        const id = segments[2]!;
        switch (req.method) {
          case "GET": return await this.getById(runtime, req, roleRules, tableName, id, signal);
          case "PATCH": return await this.updateById(runtime, req, roleRules, tableName, id, false, signal);
          case "PUT": return await this.updateById(runtime, req, roleRules, tableName, id, true, signal);
          case "DELETE": return await this.deleteById(runtime, req, roleRules, tableName, id, signal);
          default: return methodNotAllowed();
        }
      }

      return restError(404, "NotFound", "Unrecognized REST resource path.");
    } catch (err) {
      return mapRestError(err);
    }
  }

  private listTables(runtime: ServiceRuntime, identity: RequestIdentity, roleRules: readonly RoleRuleSet[]): RestResponse {
    const visible = trimSnapshot(
      runtime.schema, identity, roleRules, runtime.name,
      this.config.policyEngine, this.config.rowFilterParser,
    );
    const tables = visible.tables.map((t) => ({
      name: t.exposedName,
      label: t.exposedName,
      isView: t.isView,
      writable: t.writable && !(runtime.options ?? DEFAULT_SERVICE_OPTIONS).readOnly,
      description: t.comment ?? null,
    }));
    return restOk({ resource: tables });
  }

  private describeTable(
    runtime: ServiceRuntime,
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
    tableName: string,
  ): RestResponse {
    const table = findTable(runtime.schema, tableName);
    if (table === undefined) return restError(404, "NotFound", `Unknown table '${tableName}'.`);

    const decision = this.authorize(identity, roleRules, runtime.name, table, Verb.Get);
    if (!decision.allowed) {
      return decision.hidden
        ? restError(404, "NotFound", `Unknown table '${tableName}'.`)
        : restError(403, "Forbidden", "Access denied.");
    }

    const fields = table.columns
      .filter((c) => !decision.deniedFields.has(c.exposedName) && !decision.writeOnlyFields.has(c.exposedName))
      .map((c) => ({
        name: c.exposedName,
        type: c.edmType,
        nullable: c.nullable,
        pk: c.isPrimaryKey,
        autoIncrement: c.isAutoGenerated,
        maxLength: c.maxLength ?? null,
        allowedValues: c.allowedValues ?? null,
        masked: decision.maskedFields.has(c.exposedName),
        description: c.comment ?? null,
      }));

    return restOk({ name: table.exposedName, isView: table.isView, writable: table.writable, fields });
  }

  private async queryTable(
    runtime: ServiceRuntime,
    req: RestRequest,
    roleRules: readonly RoleRuleSet[],
    tableName: string,
    signal?: AbortSignal,
  ): Promise<RestResponse> {
    const table = findTable(runtime.schema, tableName);
    if (table === undefined) return restError(404, "NotFound", `Unknown table '${tableName}'.`);

    const decision = this.authorize(req.identity, roleRules, runtime.name, table, Verb.Get);
    if (!decision.allowed) {
      return decision.hidden
        ? restError(404, "NotFound", `Unknown table '${tableName}'.`)
        : restError(403, decision.denialCode ?? "Forbidden", decision.denialMessage ?? "Access denied.");
    }

    const q = parseQueryString(req.queryString);
    let filter: FilterNode | undefined;
    if (q.filter !== undefined) {
      filter = parseFilter(q.filter);
    }

    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;
    const effectiveLimit = Math.min(q.limit ?? options.defaultPageSize, options.maxPageSize);

    const rawQuery = {
      serviceName: runtime.name,
      table: table.exposedName,
      ...(filter !== undefined ? { filter } : {}),
      orderBy: parseOrder(q.order),
      ...(q.fields !== undefined ? { select: q.fields } : {}),
      expand: [],
      top: effectiveLimit,
      ...(q.offset !== undefined ? { skip: q.offset } : {}),
      count: false,
    };

    const rewritten = rewrite(rawQuery, decision, table);
    const masks = masksToApply(rawQuery, decision);
    const executor = this.config.getQueryExecutor(runtime);

    const result = await executor.query({
      connection: runtime.connection,
      schema: runtime.schema,
      query: rewritten,
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: effectiveLimit + 1 },
    }, signal);

    const rows = result.rows as Row[];
    applyMasks(rows, masks);

    let count: number | undefined;
    if (q.includeCount) {
      count = await executor.count({
        connection: runtime.connection,
        schema: runtime.schema,
        query: rewritten,
        options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
      }, signal);
    }

    const meta: Record<string, unknown> = { schemaVersion: runtime.schemaVersion };
    if (count !== undefined) meta["count"] = count;
    if (result.hasMore) {
      meta["next"] = `_table/${table.exposedName}?offset=${(q.offset ?? 0) + rows.length}&limit=${effectiveLimit}`;
    }

    return restOk({ resource: rows.map(rowToDict), meta });
  }

  private async getById(
    runtime: ServiceRuntime,
    req: RestRequest,
    roleRules: readonly RoleRuleSet[],
    tableName: string,
    id: string,
    signal?: AbortSignal,
  ): Promise<RestResponse> {
    const table = findTable(runtime.schema, tableName);
    if (table === undefined) return restError(404, "NotFound", `Unknown table '${tableName}'.`);

    const decision = this.authorize(req.identity, roleRules, runtime.name, table, Verb.Get);
    if (!decision.allowed) {
      return decision.hidden
        ? restError(404, "NotFound", `Unknown table '${tableName}'.`)
        : restError(403, "Forbidden", "Access denied.");
    }

    const keyFilter = buildKeyFilter(table, id);
    const rawQuery = { serviceName: runtime.name, table: table.exposedName, filter: keyFilter, orderBy: [], expand: [], count: false, top: 1 };
    const rewritten = rewrite(rawQuery, decision, table);
    const masks = masksToApply(rawQuery, decision);
    const executor = this.config.getQueryExecutor(runtime);
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;

    const result = await executor.query({
      connection: runtime.connection,
      schema: runtime.schema,
      query: rewritten,
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 1 },
    }, signal);

    if (result.rows.length === 0) return restError(404, "NotFound", "Record not found.");
    applyMasks(result.rows as Row[], masks);
    return restOk(rowToDict(result.rows[0] as Row));
  }

  private async insert(
    runtime: ServiceRuntime,
    req: RestRequest,
    roleRules: readonly RoleRuleSet[],
    tableName: string,
    signal?: AbortSignal,
  ): Promise<RestResponse> {
    const { table, decision, error } = this.authorizeWrite(runtime, roleRules, req.identity, tableName, Verb.Post);
    if (error !== undefined) return error;

    if (req.body === null || req.body === undefined) {
      return restError(400, ErrorCodes.ValidationInvalidValue, "A JSON body is required.");
    }

    const { elements, isBulk } = extractRecords(req.body);
    const records = [];
    for (const element of elements) {
      const record = PayloadBinder.bind(element, table!, runtime.schema, { allowDeepInsert: false });
      validateWritable(record.values, decision!);
      records.push(record);
    }

    const executor = this.config.getWriteExecutor(runtime)!;
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;
    const result = await executor.write({
      connection: runtime.connection,
      schema: runtime.schema,
      write: {
        serviceName: runtime.name,
        table: table!.exposedName,
        kind: "insert",
        records,
        ...(decision!.rowFilter !== undefined ? { insertVisibilityFilter: decision!.rowFilter } : {}),
      },
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
    }, signal);

    stripWrite(result.records, decision!);
    return isBulk
      ? restCreated({ resource: result.records.map(rowToDict) })
      : restCreated(rowToDict(result.records[0] as Row));
  }

  private async updateById(
    runtime: ServiceRuntime,
    req: RestRequest,
    roleRules: readonly RoleRuleSet[],
    tableName: string,
    id: string,
    replace: boolean,
    signal?: AbortSignal,
  ): Promise<RestResponse> {
    const verb = replace ? Verb.Put : Verb.Patch;
    const { table, decision, error } = this.authorizeWrite(runtime, roleRules, req.identity, tableName, verb);
    if (error !== undefined) return error;

    if (req.body === null || req.body === undefined) {
      return restError(400, ErrorCodes.ValidationInvalidValue, "A JSON body is required.");
    }

    const record = PayloadBinder.bind(req.body, table!, runtime.schema, { allowDeepInsert: false, isReplace: replace });
    validateWritable(record.values, decision!);

    const key = buildKeyPredicate(table!, id);
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;
    const executor = this.config.getWriteExecutor(runtime)!;

    const result = await executor.write({
      connection: runtime.connection,
      schema: runtime.schema,
      write: {
        serviceName: runtime.name,
        table: table!.exposedName,
        kind: replace ? "replace" : "update",
        records: [record],
        key: { values: key.values },
        ...(decision!.rowFilter !== undefined ? { precondition: decision!.rowFilter } : {}),
      },
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
    }, signal);

    if (result.affectedCount === 0) return restError(404, "NotFound", "Record not found.");
    stripWrite(result.records, decision!);
    return result.records.length > 0
      ? restOk(rowToDict(result.records[0] as Row))
      : { status: 204, headers: {}, body: "", contentType: "application/json" };
  }

  private async deleteById(
    runtime: ServiceRuntime,
    req: RestRequest,
    roleRules: readonly RoleRuleSet[],
    tableName: string,
    id: string,
    signal?: AbortSignal,
  ): Promise<RestResponse> {
    const { table, decision, error } = this.authorizeWrite(runtime, roleRules, req.identity, tableName, Verb.Delete);
    if (error !== undefined) return error;

    const key = buildKeyPredicate(table!, id);
    const executor = this.config.getWriteExecutor(runtime)!;
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;

    const result = await executor.write({
      connection: runtime.connection,
      schema: runtime.schema,
      write: {
        serviceName: runtime.name,
        table: table!.exposedName,
        kind: "delete",
        records: [],
        key: { values: key.values },
        ...(decision!.rowFilter !== undefined ? { precondition: decision!.rowFilter } : {}),
      },
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
    }, signal);

    return result.affectedCount === 0
      ? restError(404, "NotFound", "Record not found.")
      : { status: 204, headers: {}, body: "", contentType: "application/json" };
  }

  private async bulkUpdate(
    runtime: ServiceRuntime,
    req: RestRequest,
    roleRules: readonly RoleRuleSet[],
    tableName: string,
    signal?: AbortSignal,
  ): Promise<RestResponse> {
    const { table, decision, error } = this.authorizeWrite(runtime, roleRules, req.identity, tableName, Verb.Patch);
    if (error !== undefined) return error;
    if (table!.primaryKey.length === 0) {
      return restError(400, ErrorCodes.ValidationInvalidValue, "Bulk update requires a primary key.");
    }

    if (req.body === null || req.body === undefined) {
      return restError(400, ErrorCodes.ValidationInvalidValue, "A JSON body is required.");
    }

    const { elements } = extractRecords(req.body);
    const executor = this.config.getWriteExecutor(runtime)!;
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;
    const results: unknown[] = [];

    for (const element of elements) {
      const record = PayloadBinder.bind(element, table!, runtime.schema, { allowDeepInsert: false });
      validateWritable(record.values, decision!);

      const keyValues: Record<string, unknown> = {};
      for (const k of table!.primaryKey) keyValues[k] = record.values[k] ?? null;
      const trimmedValues: Record<string, unknown> = {};
      for (const [k, v] of Object.entries(record.values)) {
        if (!table!.primaryKey.includes(k)) trimmedValues[k] = v;
      }
      const trimmedRecord = { ...record, values: trimmedValues };

      const r = await executor.write({
        connection: runtime.connection,
        schema: runtime.schema,
        write: {
          serviceName: runtime.name,
          table: table!.exposedName,
          kind: "update",
          records: [trimmedRecord],
          key: { values: keyValues },
          ...(decision!.rowFilter !== undefined ? { precondition: decision!.rowFilter } : {}),
        },
        options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
      }, signal);

      stripWrite(r.records, decision!);
      results.push({
        status: r.affectedCount > 0 ? 200 : 404,
        record: r.records.length > 0 ? rowToDict(r.records[0] as Row) : null,
      });
    }

    return restOk({ resource: results });
  }

  private async bulkDelete(
    runtime: ServiceRuntime,
    req: RestRequest,
    roleRules: readonly RoleRuleSet[],
    tableName: string,
    signal?: AbortSignal,
  ): Promise<RestResponse> {
    const { table, decision, error } = this.authorizeWrite(runtime, roleRules, req.identity, tableName, Verb.Delete);
    if (error !== undefined) return error;

    const q = parseQueryString(req.queryString);
    let filter: FilterNode | undefined;

    if (q.ids !== undefined && q.ids.length > 0 && table!.primaryKey.length === 1) {
      const pk = table!.primaryKey[0]!;
      const col = table!.columns.find((c) => c.exposedName === pk);
      filter = inList(
        fieldRef(pk),
        q.ids.map((v) => constant(coerceKey(col?.edmType ?? "Edm.String", v))),
      );
    } else if (q.filter !== undefined) {
      filter = parseFilter(q.filter);
    } else {
      return restError(400, ErrorCodes.ValidationInvalidValue, "Bulk delete requires ids= or filter=.");
    }

    const effectiveFilter =
      decision!.rowFilter !== undefined
        ? logical("and", [filter!, decision!.rowFilter])
        : filter!;

    const executor = this.config.getWriteExecutor(runtime)!;
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;

    const result = await executor.write({
      connection: runtime.connection,
      schema: runtime.schema,
      write: {
        serviceName: runtime.name,
        table: table!.exposedName,
        kind: "delete",
        records: [],
        precondition: effectiveFilter,
      },
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
    }, signal);

    return restOk({ meta: { affected: result.affectedCount } });
  }

  private authorizeWrite(
    runtime: ServiceRuntime,
    roleRules: readonly RoleRuleSet[],
    identity: RequestIdentity,
    tableName: string,
    verb: number,
  ): { table?: TableModel; decision?: PolicyDecision; error?: RestResponse } {
    const table = findTable(runtime.schema, tableName);
    if (table === undefined) return { error: restError(404, "NotFound", `Unknown table '${tableName}'.`) };

    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;
    if (!table.writable || options.readOnly) {
      return { error: restError(403, ErrorCodes.ForbiddenVerb, `'${tableName}' is read-only.`) };
    }
    if (this.config.getWriteExecutor(runtime) === undefined) {
      return { error: restError(403, ErrorCodes.ForbiddenVerb, "This service is read-only.") };
    }

    const decision = this.authorize(identity, roleRules, runtime.name, table, verb);
    if (!decision.allowed) {
      return {
        error: decision.hidden
          ? restError(404, "NotFound", `Unknown table '${tableName}'.`)
          : restError(403, decision.denialCode ?? "Forbidden", decision.denialMessage ?? "Access denied."),
      };
    }

    return { table, decision };
  }

  private authorize(
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
    serviceName: string,
    table: TableModel,
    verb: number,
  ): PolicyDecision {
    if (identity.bypass) return FULL_ACCESS;
    return this.config.policyEngine.authorize(
      identity,
      roleRules,
      serviceName,
      table.exposedName,
      verb,
      table.columns.map((c) => c.exposedName),
      this.config.rowFilterParser,
    );
  }
}

// ---- Pure helpers ----

interface QueryParams {
  readonly filter: string | undefined;
  readonly fields: string[] | undefined;
  readonly order: string | undefined;
  readonly limit: number | undefined;
  readonly offset: number | undefined;
  readonly includeCount: boolean;
  readonly ids: string[] | undefined;
}

function parseQueryString(qs: string): QueryParams {
  const pairs: Record<string, string> = {};
  for (const part of qs.split("&").filter((p) => p.length > 0)) {
    const eq = part.indexOf("=");
    const key = eq < 0 ? part : decodeURIComponent(part.slice(0, eq));
    const value = eq < 0 ? "" : decodeURIComponent(part.slice(eq + 1).replace(/\+/g, " "));
    pairs[key] = value;
  }

  const get = (k: string) => pairs[k];
  const getInt = (k: string) => {
    const v = get(k);
    const n = v !== undefined ? parseInt(v, 10) : NaN;
    return isNaN(n) ? undefined : n;
  };

  return {
    filter: get("filter"),
    fields:
      get("fields") !== undefined && get("fields") !== "*"
        ? get("fields")!.split(",").map((s) => s.trim()).filter((s) => s.length > 0)
        : undefined,
    order: get("order"),
    limit: getInt("limit"),
    offset: getInt("offset"),
    includeCount: get("include_count") === "true",
    ids: get("ids")?.split(",").filter((s) => s.length > 0),
  };
}

function parseOrder(order: string | undefined): readonly OrderByItem[] {
  if (!order || order.trim().length === 0) return [];
  return order.split(",").map((part) => {
    const bits = part.trim().split(/\s+/);
    return { field: bits[0]!, descending: bits[1]?.toLowerCase() === "desc" };
  });
}

function buildKeyFilter(table: TableModel, id: string): FilterNode {
  const pred = buildKeyPredicate(table, id);
  let filter: FilterNode | undefined;
  for (const [k, v] of Object.entries(pred.values)) {
    const cmp = comparison(fieldRef(k), "eq", constant(v));
    filter = filter === undefined ? cmp : logical("and", [filter, cmp]);
  }
  if (filter === undefined) {
    throw new QueryValidationError(ErrorCodes.ValidationInvalidValue, "Table has no key.");
  }
  return filter;
}

function buildKeyPredicate(table: TableModel, id: string): { values: Record<string, unknown> } {
  if (table.primaryKey.length === 0) {
    throw new QueryValidationError(ErrorCodes.ValidationInvalidValue, `Table '${table.exposedName}' has no key.`);
  }
  const parts = id.split(",");
  if (parts.length !== table.primaryKey.length) {
    throw new QueryValidationError(
      ErrorCodes.ValidationInvalidValue,
      `Expected ${table.primaryKey.length} key value(s).`,
    );
  }
  const values: Record<string, unknown> = {};
  for (let i = 0; i < parts.length; i++) {
    const col = table.columns.find((c) => c.exposedName === table.primaryKey[i]);
    values[table.primaryKey[i]!] = coerceKey(col?.edmType ?? "Edm.String", parts[i]!);
  }
  return { values };
}

function coerceKey(edmType: string, raw: string): unknown {
  switch (edmType) {
    case "Edm.Int16":
    case "Edm.Int32":
    case "Edm.Int64":
      return parseInt(raw, 10);
    default:
      return raw;
  }
}

function extractRecords(json: unknown): { elements: unknown[]; isBulk: boolean } {
  if (Array.isArray(json)) return { elements: json, isBulk: true };
  if (
    typeof json === "object" &&
    json !== null &&
    "resource" in json &&
    Array.isArray((json as Record<string, unknown>)["resource"])
  ) {
    return { elements: (json as Record<string, unknown>)["resource"] as unknown[], isBulk: true };
  }
  return { elements: [json], isBulk: false };
}

function validateWritable(values: Record<string, unknown>, decision: PolicyDecision): void {
  for (const field of Object.keys(values)) {
    if (decision.deniedFields.has(field) || decision.maskedFields.has(field)) {
      throw new FieldDeniedError(field);
    }
  }
}

function stripWrite(rows: readonly Row[], decision: PolicyDecision): void {
  if (decision.bypass) return;
  for (const row of rows) {
    for (const field of [...decision.deniedFields, ...decision.writeOnlyFields]) row.remove(field);
    for (const [k, v] of decision.maskedFields) {
      if (row.has(k)) row.set(k, v);
    }
  }
}

function applyMasks(rows: readonly Row[], masks: ReadonlyMap<string, string>): void {
  if (masks.size === 0) return;
  for (const row of rows) {
    for (const [k, v] of masks) row.set(k, v);
  }
}

function rowToDict(row: Row): Record<string, unknown> {
  const obj: Record<string, unknown> = {};
  for (const [k, v] of row.values) {
    obj[k] = normalizeForJson(v);
  }
  return obj;
}

function normalizeForJson(value: unknown): unknown {
  if (value === null || value === undefined) return null;
  if (value instanceof Date) return value.toISOString();
  if (value instanceof Uint8Array || Buffer.isBuffer(value)) {
    return Buffer.from(value).toString("base64");
  }
  return value;
}

function methodNotAllowed(): RestResponse {
  return restError(405, "MethodNotAllowed", "Method not allowed on this resource.");
}

function restOk(payload: unknown): RestResponse {
  return { status: 200, headers: {}, body: JSON.stringify(payload), contentType: "application/json" };
}

function restCreated(payload: unknown): RestResponse {
  return { status: 201, headers: {}, body: JSON.stringify(payload), contentType: "application/json" };
}

function restError(status: number, code: string, message: string): RestResponse {
  return {
    status,
    headers: {},
    body: JSON.stringify({ type: "about:blank", title: message, status, errorCode: code }),
    contentType: "application/problem+json",
  };
}

function mapRestError(err: unknown): RestResponse {
  if (err instanceof FieldDeniedError) {
    return restError(403, ErrorCodes.ForbiddenFieldDenied, err.message);
  }
  if (err instanceof BindError) {
    return restError(400, err.errorCode, err.message);
  }
  if (err instanceof FilterParseError) {
    return restError(400, err.errorCode, err.message);
  }
  if (err instanceof ConnectorError) {
    return restError(connectorStatus(err.code), err.code, err.message);
  }
  if (err instanceof QueryValidationError) {
    return restError(400, err.code, err.message);
  }
  if (err instanceof NotSupportedQueryError) {
    return restError(501, "NotImplemented", err.message);
  }
  if (err instanceof Error) {
    return restError(500, ErrorCodes.InternalUnmapped, err.message);
  }
  return restError(500, ErrorCodes.InternalUnmapped, "Internal server error.");
}

function connectorStatus(code: string): number {
  switch (code) {
    case ErrorCodes.ConflictUniqueViolation:
    case ErrorCodes.ConflictForeignKeyViolation:
      return 409;
    case ErrorCodes.ValidationNotNullViolation:
    case ErrorCodes.ValidationValueTooLong:
    case ErrorCodes.ValidationInvalidValue:
      return 400;
    case ErrorCodes.ForbiddenRowFilter:
      return 403;
    case ErrorCodes.UpstreamPermissionDenied:
      return 502;
    case ErrorCodes.UpstreamTimeout:
      return 504;
    case ErrorCodes.UpstreamUnavailable:
      return 503;
    default:
      return 500;
  }
}
