/**
 * OData v4 write pipeline (port of ODataRequestHandler.Writes.cs, spec 05 §5–7):
 * POST (insert/bulk), PUT/PATCH (replace/update), DELETE, $batch changesets, ETag.
 */

import { ErrorCodes } from "../core/errors.js";
import {
  comparison,
  constant,
  fieldRef,
  logical,
  type FilterNode,
} from "../core/query.js";
import { type Row } from "../core/result.js";
import { findTable, type TableModel, type ColumnModel } from "../core/schema.js";
import { FULL_ACCESS, type PolicyDecision } from "../core/policy/decision.js";
import { FieldDeniedError } from "../core/policy/rewriter.js";
import type { PolicyEngine } from "../core/policy/engine.js";
import type { RowFilterParser } from "../core/policy/contracts.js";
import { Verb, type RequestIdentity, type RoleRuleSet } from "../core/policy/model.js";
import type { WriteExecutor, ServiceRuntime, QueryExecutor } from "../connectors/contracts.js";
import { ConnectorError } from "../core/write.js";
import { DEFAULT_SERVICE_OPTIONS } from "../core/services.js";
import { PayloadBinder, BindError } from "../core/payload-binder.js";
import { buildErrorPayload } from "./payload.js";
import type { ODataResponse } from "./handler.js";

export interface ODataWriteHandlerConfig {
  resolveRuntime: (name: string, signal?: AbortSignal) => Promise<ServiceRuntime | undefined>;
  getWriteExecutor: (runtime: ServiceRuntime) => WriteExecutor | undefined;
  getQueryExecutor: (runtime: ServiceRuntime) => QueryExecutor;
  policyEngine: PolicyEngine;
  resolveRoleRules: (identity: RequestIdentity, runtime: ServiceRuntime) => Promise<readonly RoleRuleSet[]>;
  rowFilterParser: RowFilterParser;
}

export interface ODataWriteRequest {
  readonly method: string;
  readonly path: string;
  readonly queryString: string;
  readonly serviceRoot: string;
  readonly identity: RequestIdentity;
  readonly headers: Readonly<Record<string, string>>;
  readonly body?: unknown;
}

export class ODataWriteHandler {
  constructor(private readonly config: ODataWriteHandlerConfig) {}

  async handleForService(
    serviceName: string,
    req: ODataWriteRequest,
    signal?: AbortSignal,
  ): Promise<ODataResponse> {
    try {
      const runtime = await this.config.resolveRuntime(serviceName, signal);
      if (runtime === undefined) {
        return jsonError(404, "NotFound", `Unknown service '${serviceName}'.`);
      }
      if (runtime.status === "disabled") {
        return jsonError(503, "ServiceUnavailable", "Service is disabled.");
      }
      if (runtime.status !== "active" && runtime.status !== "refreshing" && runtime.status !== "introspecting") {
        return jsonError(503, "ServiceUnavailable", "Service schema is not ready yet.");
      }

      const writeExecutor = this.config.getWriteExecutor(runtime);
      if (writeExecutor === undefined || (runtime.options ?? DEFAULT_SERVICE_OPTIONS).readOnly) {
        return jsonError(403, ErrorCodes.ForbiddenVerb, "This service is read-only.");
      }

      const roleRules = await this.config.resolveRoleRules(req.identity, runtime);
      const path = req.path.replace(/^\//, "");

      if (path === "$batch") {
        return await this.handleBatch(runtime, writeExecutor, req, roleRules, signal);
      }

      // Parse entity set and optional key from path e.g. "customers" or "customers(1)"
      const parsed = parsePath(path);
      if (parsed === null) {
        return jsonError(501, "NotImplemented", "Writes are supported on entity sets only.");
      }

      const { entitySet: tableName, key: rawKey } = parsed;
      const table = findTable(runtime.schema, tableName);
      if (table === undefined) {
        return jsonError(404, "NotFound", `Resource '${tableName}' not found.`);
      }
      if (!table.writable) {
        return jsonError(403, ErrorCodes.ForbiddenVerb, `'${table.exposedName}' is read-only (view).`);
      }

      // Resolve positional key (e.g. customers(1)) → named key using primary key column
      const key = resolvePositionalKey(rawKey, table.primaryKey);

      const method = req.method.toUpperCase();
      const verb = methodToVerb(method);
      if (verb === Verb.None) {
        return jsonError(405, "MethodNotAllowed", `Method ${method} is not supported.`);
      }

      const decision = this.authorize(req.identity, roleRules, runtime.name, table, verb);
      if (!decision.allowed) {
        return decision.hidden
          ? jsonError(404, "NotFound", `Resource '${tableName}' not found.`)
          : jsonError(403, decision.denialCode ?? "Forbidden", decision.denialMessage ?? "Access denied.");
      }

      if (verb === Verb.Post && key !== undefined) {
        return jsonError(405, "MethodNotAllowed", "POST is not valid on a single entity.");
      }
      if ((verb === Verb.Patch || verb === Verb.Put || verb === Verb.Delete) && key === undefined) {
        return jsonError(405, "MethodNotAllowed", `${method} requires an entity key.`);
      }

      if (verb === Verb.Post) {
        return await this.handleInsert(runtime, writeExecutor, req, table, decision, signal);
      }
      if (verb === Verb.Patch || verb === Verb.Put) {
        return await this.handleUpdate(runtime, writeExecutor, req, table, key!, decision, verb === Verb.Put, signal);
      }
      return await this.handleDelete(runtime, writeExecutor, req, table, key!, decision, signal);
    } catch (err) {
      return mapError(err);
    }
  }

  private async handleInsert(
    runtime: ServiceRuntime,
    executor: WriteExecutor,
    req: ODataWriteRequest,
    table: TableModel,
    decision: PolicyDecision,
    signal?: AbortSignal,
  ): Promise<ODataResponse> {
    const json = req.body;
    if (json === null || json === undefined) {
      return jsonError(400, ErrorCodes.ValidationInvalidValue, "A JSON body is required.");
    }

    const isBulk = Array.isArray(json);
    const elements: unknown[] = isBulk ? (json as unknown[]) : [json];
    if (elements.length === 0) {
      return jsonError(400, ErrorCodes.ValidationInvalidValue, "Empty insert payload.");
    }

    const records = [];
    for (const element of elements) {
      const record = PayloadBinder.bind(element, table, runtime.schema, { allowDeepInsert: true });
      validateWritableFields(record.values, decision);
      records.push(record);
    }

    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;
    const result = await executor.write({
      connection: runtime.connection,
      schema: runtime.schema,
      write: {
        serviceName: runtime.name,
        table: table.exposedName,
        kind: "insert",
        records,
        ...(decision.rowFilter !== undefined ? { insertVisibilityFilter: decision.rowFilter } : {}),
      },
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
    }, signal);

    const preferMinimal =
      (req.headers["prefer"] ?? "").toLowerCase().includes("return=minimal");
    if (preferMinimal) {
      return {
        status: 204,
        headers: { "OData-Version": "4.0", "Preference-Applied": "return=minimal" },
        body: "",
        contentType: "application/json",
      };
    }

    applyReadPolicy(result.records, decision);

    if (isBulk) {
      return json201(buildCollection(req.serviceRoot, table.exposedName, result.records));
    }
    const firstRow = result.records[0];
    const body = firstRow !== undefined ? buildSingle(req.serviceRoot, table.exposedName, firstRow) : {};
    const location = buildLocation(req, table, firstRow);
    return {
      status: 201,
      headers: {
        "OData-Version": "4.0",
        ...(location !== undefined ? { Location: location } : {}),
      },
      body: JSON.stringify(body),
      contentType: "application/json;odata.metadata=minimal",
    };
  }

  private async handleUpdate(
    runtime: ServiceRuntime,
    executor: WriteExecutor,
    req: ODataWriteRequest,
    table: TableModel,
    key: Record<string, unknown>,
    decision: PolicyDecision,
    isReplace: boolean,
    signal?: AbortSignal,
  ): Promise<ODataResponse> {
    const json = req.body;
    if (json === null || json === undefined) {
      return jsonError(400, ErrorCodes.ValidationInvalidValue, "A JSON body is required.");
    }

    let record = PayloadBinder.bind(json, table, runtime.schema, { allowDeepInsert: false, isReplace });
    validateWritableFields(record.values, decision);

    // Key in body must match URL or be absent
    const trimmedValues = { ...record.values };
    for (const [k, v] of Object.entries(key)) {
      if (Object.prototype.hasOwnProperty.call(trimmedValues, k)) {
        if (String(trimmedValues[k]) !== String(v)) {
          return jsonError(
            400,
            ErrorCodes.ValidationInvalidValue,
            `Key property '${k}' in the body conflicts with the URL.`,
          );
        }
        delete trimmedValues[k];
      }
    }
    record = { ...record, values: trimmedValues };

    const { precondition, error: etagError } = await buildPrecondition(runtime, table, decision, req);
    if (etagError !== null) return etagError;

    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;
    const result = await executor.write({
      connection: runtime.connection,
      schema: runtime.schema,
      write: {
        serviceName: runtime.name,
        table: table.exposedName,
        kind: isReplace ? "replace" : "update",
        records: [record],
        key: { values: key },
        ...(precondition !== undefined ? { precondition } : {}),
      },
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
    }, signal);

    if (result.affectedCount === 0) {
      return await this.notFoundOrPreconditionFailed(runtime, executor, table, key, decision, req, signal);
    }

    applyReadPolicy(result.records, decision);
    const row = result.records[0];
    const body = row !== undefined ? buildSingle(req.serviceRoot, table.exposedName, row) : {};
    return { status: 200, headers: { "OData-Version": "4.0" }, body: JSON.stringify(body), contentType: "application/json;odata.metadata=minimal" };
  }

  private async handleDelete(
    runtime: ServiceRuntime,
    executor: WriteExecutor,
    req: ODataWriteRequest,
    table: TableModel,
    key: Record<string, unknown>,
    decision: PolicyDecision,
    signal?: AbortSignal,
  ): Promise<ODataResponse> {
    const { precondition, error: etagError } = await buildPrecondition(runtime, table, decision, req);
    if (etagError !== null) return etagError;

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
        ...(precondition !== undefined ? { precondition } : {}),
      },
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
    }, signal);

    if (result.affectedCount === 0) {
      return await this.notFoundOrPreconditionFailed(runtime, executor, table, key, decision, req, signal);
    }

    return { status: 204, headers: { "OData-Version": "4.0" }, body: "", contentType: "application/json" };
  }

  private async handleBatch(
    runtime: ServiceRuntime,
    _executor: WriteExecutor,
    req: ODataWriteRequest,
    _roleRules: readonly RoleRuleSet[],
    _signal?: AbortSignal,
  ): Promise<ODataResponse> {
    const json = req.body as Record<string, unknown> | null;
    if (
      json === null ||
      typeof json !== "object" ||
      !Array.isArray((json as Record<string, unknown>)["requests"])
    ) {
      return jsonError(400, ErrorCodes.ValidationInvalidValue, "JSON batch requires a 'requests' array.");
    }

    const items = (json as Record<string, unknown>)["requests"] as unknown[];
    if (items.length > 100) {
      return jsonError(400, ErrorCodes.ValidationInvalidValue, "Batches are limited to 100 requests (spec 05 §6).");
    }

    const responses: unknown[] = [];
    for (const item of items) {
      const obj = item as Record<string, unknown>;
      const id = String(obj["id"] ?? "?");
      const method = String(obj["method"] ?? "GET");
      const url = String(obj["url"] ?? "");

      // Re-dispatch sub-requests through a nested write handler call (simplified)
      const queryIndex = url.indexOf("?");
      const subPath = queryIndex < 0 ? url.replace(/^\//, "") : url.slice(0, queryIndex).replace(/^\//, "");

      const subReq: ODataWriteRequest = {
        method,
        path: subPath,
        queryString: queryIndex < 0 ? "" : url.slice(queryIndex + 1),
        serviceRoot: req.serviceRoot,
        identity: req.identity,
        headers: req.headers,
        body: obj["body"],
      };

      const subResp = await this.handleForService(runtime.name, subReq, _signal);
      let parsedBody: unknown = null;
      try {
        parsedBody = JSON.parse(subResp.body as string);
      } catch {
        parsedBody = null;
      }
      responses.push({
        id,
        status: subResp.status,
        headers: { "content-type": subResp.contentType },
        body: parsedBody,
      });
    }

    return {
      status: 200,
      headers: { "OData-Version": "4.0" },
      body: JSON.stringify({ responses }),
      contentType: "application/json",
    };
  }

  private async notFoundOrPreconditionFailed(
    runtime: ServiceRuntime,
    _executor: WriteExecutor,
    table: TableModel,
    key: Record<string, unknown>,
    decision: PolicyDecision,
    req: ODataWriteRequest,
    signal?: AbortSignal,
  ): Promise<ODataResponse> {
    const hasIfMatch = req.headers["if-match"] !== undefined && req.headers["if-match"] !== "";
    if (!hasIfMatch) {
      return jsonError(404, "NotFound", "Entity not found.");
    }

    // Probe to distinguish 404 vs 412
    let keyFilter: FilterNode | undefined;
    for (const [k, v] of Object.entries(key)) {
      const cmp = comparison(fieldRef(k), "eq", constant(v));
      keyFilter = keyFilter === undefined ? cmp : logical("and", [keyFilter, cmp]);
    }
    if (decision.rowFilter !== undefined && keyFilter !== undefined) {
      keyFilter = logical("and", [keyFilter, decision.rowFilter]);
    }

    const qExec = this.config.getQueryExecutor(runtime);
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;
    const probe = await qExec.query({
      connection: runtime.connection,
      schema: runtime.schema,
      query: {
        serviceName: runtime.name,
        table: table.exposedName,
        ...(keyFilter !== undefined ? { filter: keyFilter } : {}),
        select: table.primaryKey,
        orderBy: [],
        expand: [],
        count: false,
        top: 1,
      },
      options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 1 },
    }, signal);

    return probe.rows.length > 0
      ? jsonError(412, "PreconditionFailed", "The entity was modified by another request.")
      : jsonError(404, "NotFound", "Entity not found.");
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

function methodToVerb(method: string): number {
  switch (method) {
    case "POST": return Verb.Post;
    case "PUT": return Verb.Put;
    case "PATCH": return Verb.Patch;
    case "DELETE": return Verb.Delete;
    default: return Verb.None;
  }
}

/**
 * Resolves a positional key `{ __positional__: value }` to a named key using
 * the table's primary key columns (mirrors read-side parsePath logic).
 */
function resolvePositionalKey(
  key: Record<string, unknown> | undefined,
  primaryKey: readonly string[],
): Record<string, unknown> | undefined {
  if (key === undefined) return undefined;
  if (!("__positional__" in key)) return key;
  const pkCol = primaryKey[0];
  if (pkCol === undefined) return key;
  const { __positional__: value, ...rest } = key;
  return Object.keys(rest).length === 0 ? { [pkCol]: value } : { [pkCol]: value, ...rest };
}

function parsePath(path: string): { entitySet: string; key?: Record<string, unknown> } | null {
  const parenIdx = path.indexOf("(");
  if (parenIdx < 0) {
    return path.length > 0 ? { entitySet: path } : null;
  }

  const entitySet = path.slice(0, parenIdx);
  const keyStr = path.slice(parenIdx + 1, path.endsWith(")") ? path.length - 1 : path.length);

  // Parse composite keys: name=val,name2=val2 or positional: just a value
  const key: Record<string, unknown> = {};
  if (keyStr.includes("=")) {
    for (const part of keyStr.split(",")) {
      const eqIdx = part.indexOf("=");
      const name = part.slice(0, eqIdx).trim();
      const rawVal = part.slice(eqIdx + 1).trim();
      key[name] = parseKeyValue(rawVal);
    }
  } else {
    key["__positional__"] = parseKeyValue(keyStr);
  }
  return { entitySet, key };
}

function parseKeyValue(raw: string): unknown {
  if (raw.startsWith("'") && raw.endsWith("'")) {
    return raw.slice(1, -1).replace(/''/g, "'");
  }
  const n = Number(raw);
  if (!isNaN(n)) return n;
  return raw;
}

function validateWritableFields(values: Record<string, unknown>, decision: PolicyDecision): void {
  for (const field of Object.keys(values)) {
    if (decision.deniedFields.has(field) || decision.maskedFields.has(field)) {
      throw new FieldDeniedError(field);
    }
  }
}

function applyReadPolicy(rows: readonly Row[], decision: PolicyDecision): void {
  if (decision.bypass) return;
  for (const row of rows) {
    for (const field of [...decision.deniedFields, ...decision.writeOnlyFields]) {
      row.remove(field);
    }
    for (const [k, v] of decision.maskedFields) {
      if (row.has(k)) row.set(k, v);
    }
  }
}

async function buildPrecondition(
  runtime: ServiceRuntime,
  table: TableModel,
  decision: PolicyDecision,
  req: ODataWriteRequest,
): Promise<{ precondition: FilterNode | undefined; error: ODataResponse | null }> {
  let precondition: FilterNode | undefined = decision.rowFilter;
  const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;

  const concurrencyColumn = options.concurrencyColumns
    .map((name) => table.columns.find((c) => c.exposedName === name))
    .find((c) => c !== undefined) as ColumnModel | undefined;

  if (concurrencyColumn !== undefined) {
    const ifMatch = req.headers["if-match"] ?? "";
    if (ifMatch === "") {
      return {
        precondition: undefined,
        error: jsonError(428, "PreconditionRequired", `If-Match is required for writes to '${table.exposedName}'.`),
      };
    }

    if (ifMatch !== "*") {
      let raw = ifMatch.trim();
      if (raw.startsWith("W/")) raw = raw.slice(2);
      raw = raw.replace(/^"|"$/g, "");

      const expected = decodeETagValue(raw, concurrencyColumn);
      const etagCheck = comparison(
        fieldRef(concurrencyColumn.exposedName),
        "eq",
        constant(expected),
      );
      precondition =
        precondition === undefined
          ? etagCheck
          : logical("and", [precondition, etagCheck]);
    }
  }

  return { precondition, error: null };
}

function decodeETagValue(raw: string, column: ColumnModel): unknown {
  switch (column.edmType) {
    case "Edm.Int16":
    case "Edm.Int32":
    case "Edm.Int64":
      return parseInt(raw, 10);
    default:
      return raw;
  }
}

function buildCollection(
  serviceRoot: string,
  entitySet: string,
  rows: readonly Row[],
): unknown {
  return {
    "@odata.context": `${trimSlash(serviceRoot)}/$metadata#${entitySet}`,
    value: rows.map((r) => rowToObj(r)),
  };
}

function buildSingle(serviceRoot: string, entitySet: string, row: Row): unknown {
  return {
    "@odata.context": `${trimSlash(serviceRoot)}/$metadata#${entitySet}/$entity`,
    ...rowToObj(row),
  };
}

function rowToObj(row: Row): Record<string, unknown> {
  const obj: Record<string, unknown> = {};
  for (const [k, v] of row.values) {
    obj[k] = normalizeValue(v);
  }
  return obj;
}

function normalizeValue(v: unknown): unknown {
  if (v === null || v === undefined) return null;
  if (v instanceof Date) return v.toISOString();
  return v;
}

function buildLocation(req: ODataWriteRequest, table: TableModel, row: Row | undefined): string | undefined {
  if (row === undefined || table.primaryKey.length === 0) return undefined;
  const keyPart =
    table.primaryKey.length === 1
      ? formatKeyValue(row.get(table.primaryKey[0]!))
      : table.primaryKey.map((k) => `${k}=${formatKeyValue(row.get(k))}`).join(",");
  return `${trimSlash(req.serviceRoot)}/${table.exposedName}(${keyPart})`;
}

function formatKeyValue(value: unknown): string {
  if (value === null || value === undefined) return "null";
  if (typeof value === "string") return `'${value.replace(/'/g, "''")}'`;
  return String(value);
}

function trimSlash(s: string): string {
  return s.endsWith("/") ? s.slice(0, -1) : s;
}

function json201(body: unknown): ODataResponse {
  return {
    status: 201,
    headers: { "OData-Version": "4.0" },
    body: JSON.stringify(body),
    contentType: "application/json;odata.metadata=minimal",
  };
}

function jsonError(status: number, code: string, message: string): ODataResponse {
  return {
    status,
    headers: { "OData-Version": "4.0" },
    body: JSON.stringify(buildErrorPayload(code, message)),
    contentType: "application/json",
  };
}

function mapError(err: unknown): ODataResponse {
  if (err instanceof FieldDeniedError) {
    return jsonError(403, ErrorCodes.ForbiddenFieldDenied, err.message);
  }
  if (err instanceof BindError) {
    return jsonError(400, err.errorCode, err.message);
  }
  if (err instanceof ConnectorError) {
    const status = connectorStatus(err.code);
    return jsonError(status, err.code, err.message);
  }
  if (err instanceof Error) {
    return jsonError(500, ErrorCodes.InternalUnmapped, err.message);
  }
  return jsonError(500, ErrorCodes.InternalUnmapped, "Internal server error.");
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
