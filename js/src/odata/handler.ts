/**
 * OData v4 request handler (port of ODataRequestHandler.cs, spec 05).
 * Protocol-agnostic: receives ODataRequest, returns ODataResponse.
 */

import { ErrorCodes } from "../core/errors.js";
import {
  comparison,
  constant,
  fieldRef,
  logical,
  type FilterNode,
  type QueryRequest,
} from "../core/query.js";
import { Row } from "../core/result.js";
import { findTable, type SchemaSnapshot, type TableModel } from "../core/schema.js";
import { FULL_ACCESS, type PolicyDecision } from "../core/policy/decision.js";
import { FieldDeniedError, masksToApply, rewrite } from "../core/policy/rewriter.js";
import type { PolicyEngine } from "../core/policy/engine.js";
import type { RowFilterParser } from "../core/policy/contracts.js";
import type { RequestIdentity, RoleRuleSet } from "../core/policy/model.js";
import type {
  ExecutionOptions,
  QueryExecutor,
  ServiceRuntime,
} from "../connectors/contracts.js";
import { NotSupportedQueryError, QueryValidationError } from "../connectors/contracts.js";
import { Verb } from "../core/policy/model.js";
import { DEFAULT_SERVICE_OPTIONS } from "../core/services.js";
import { trimSnapshot } from "../core/policy/trimmer.js";
import { buildCsdlXmlFull } from "./csdl.js";
import {
  buildApplyPayload,
  buildCollectionPayload,
  buildCountPayload,
  buildErrorPayload,
  buildServiceDocument,
  buildSinglePayload,
} from "./payload.js";
import { parseQueryOptions, parsePath, ODataParseError, type ParsedQuery } from "./parser.js";
import { SkipTokenCodec } from "./skiptoken.js";
import { ExpansionExecutor, FieldDeniedByExpandError } from "./expand.js";

export interface ODataRequest {
  readonly method: string;
  /** Path relative to service root (no leading /), e.g. "" | "$metadata" | "customers" | "customers(1)". */
  readonly path: string;
  readonly queryString: string;
  /** Full URL prefix for context and nextLink generation, e.g. "http://host/api/odata/sales". */
  readonly serviceRoot: string;
  readonly identity: RequestIdentity;
  readonly headers: Readonly<Record<string, string>>;
  readonly body?: string;
}

export interface ODataResponse {
  readonly status: number;
  readonly headers: Record<string, string>;
  readonly body: string | Buffer;
  readonly contentType: string;
}

export interface ODataHandlerConfig {
  resolveRuntime: (name: string, signal?: AbortSignal) => Promise<ServiceRuntime | undefined>;
  getExecutor: (runtime: ServiceRuntime) => QueryExecutor;
  skipTokenCodec: SkipTokenCodec;
  policyEngine: PolicyEngine;
  resolveRoleRules: (
    identity: RequestIdentity,
    runtime: ServiceRuntime,
  ) => Promise<readonly RoleRuleSet[]>;
  rowFilterParser: RowFilterParser;
}

const ODATA_HEADERS: Record<string, string> = { "OData-Version": "4.0" };

export class ODataHandler {
  constructor(private readonly config: ODataHandlerConfig) {}

  async handleForService(
    serviceName: string,
    req: ODataRequest,
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
      if (
        runtime.status !== "active" &&
        runtime.status !== "refreshing" &&
        runtime.status !== "introspecting"
      ) {
        return jsonError(503, "ServiceUnavailable", "Service schema is not ready yet.");
      }

      const roleRules = await this.config.resolveRoleRules(req.identity, runtime);
      const isRead = req.method.toUpperCase() === "GET" || req.method.toUpperCase() === "HEAD";
      if (!isRead) {
        return jsonError(405, "MethodNotAllowed", "Only GET is supported in this milestone.");
      }

      const path = req.path;
      if (path === "" || path === "/") {
        return this.handleServiceDocument(req, runtime, roleRules);
      }
      if (path === "$metadata") {
        return this.handleMetadata(req, runtime, roleRules);
      }

      return await this.handleQuery(req, runtime, roleRules, signal);
    } catch (err) {
      return mapError(err);
    }
  }

  private handleServiceDocument(
    req: ODataRequest,
    runtime: ServiceRuntime,
    roleRules: readonly RoleRuleSet[],
  ): ODataResponse {
    const visible = trimSnapshot(
      runtime.schema,
      req.identity,
      roleRules,
      runtime.name,
      this.config.policyEngine,
      this.config.rowFilterParser,
    );
    const doc = buildServiceDocument(
      req.serviceRoot,
      visible.tables.map((t) => t.exposedName),
    );
    return json(200, doc);
  }

  private handleMetadata(
    req: ODataRequest,
    runtime: ServiceRuntime,
    roleRules: readonly RoleRuleSet[],
  ): ODataResponse {
    const visible = trimSnapshot(
      runtime.schema,
      req.identity,
      roleRules,
      runtime.name,
      this.config.policyEngine,
      this.config.rowFilterParser,
    );
    const xml = buildCsdlXmlFull(runtime.name, visible);
    return {
      status: 200,
      headers: { ...ODATA_HEADERS },
      body: xml,
      contentType: "application/xml;charset=utf-8",
    };
  }

  private async handleQuery(
    req: ODataRequest,
    runtime: ServiceRuntime,
    roleRules: readonly RoleRuleSet[],
    signal?: AbortSignal,
  ): Promise<ODataResponse> {
    const parsed = parsePath(req.path);
    const { entitySet: tableName, key, isCount } = parsed;

    const table = findTable(runtime.schema, tableName);
    if (table === undefined) {
      return jsonError(404, "NotFound", `Resource '${tableName}' not found.`);
    }

    let queryOpts: ParsedQuery;
    try {
      queryOpts = parseQueryOptions(req.queryString);
    } catch (err) {
      if (err instanceof ODataParseError) {
        return jsonError(400, ErrorCodes.ValidationBadFilter, err.message);
      }
      throw err;
    }

    const decision = this.authorize(req.identity, roleRules, runtime.name, table);
    if (!decision.allowed) {
      return decision.hidden
        ? jsonError(404, "NotFound", `Resource '${tableName}' not found.`)
        : jsonError(
            403,
            decision.denialCode ?? "Forbidden",
            decision.denialMessage ?? "Access denied.",
          );
    }

    if (queryOpts.apply !== undefined) {
      return this.handleApply(req, runtime, table, decision, queryOpts, signal);
    }

    const executor = this.config.getExecutor(runtime);
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;

    let skip = queryOpts.skip;
    if (queryOpts.skipToken !== undefined) {
      const tokenSkip = this.config.skipTokenCodec.tryDecode(queryOpts.skipToken);
      if (tokenSkip === null) {
        return jsonError(400, ErrorCodes.ValidationInvalidValue, "Invalid $skiptoken.");
      }
      skip = tokenSkip;
    }

    const effectiveTop =
      key !== undefined
        ? 1
        : Math.min(queryOpts.top ?? options.defaultPageSize, options.maxPageSize);

    let rawQuery: QueryRequest = {
      serviceName: runtime.name,
      table: tableName,
      orderBy: queryOpts.orderBy,
      expand: [],
      top: effectiveTop,
      count: queryOpts.count,
      ...(queryOpts.filter !== undefined ? { filter: queryOpts.filter } : {}),
      ...(queryOpts.select !== undefined ? { select: queryOpts.select as readonly string[] } : {}),
      ...(skip !== undefined ? { skip } : {}),
    } as QueryRequest;

    if (key !== undefined) {
      const keyFilter = buildKeyFilter(key, table);
      rawQuery = {
        ...rawQuery,
        filter: keyFilter,
        orderBy: [],
        top: 1,
        count: false,
      } as QueryRequest;
    }

    let rewritten: QueryRequest;
    try {
      rewritten = rewrite(rawQuery, decision, table);
    } catch (err) {
      if (err instanceof FieldDeniedError) {
        return jsonError(403, ErrorCodes.ForbiddenFieldDenied, err.message);
      }
      throw err;
    }

    const masks = masksToApply(rawQuery, decision);

    // Augment select with FK columns needed for $expand stitching
    const expandKeys = requiredExpandKeys(table, queryOpts.expand, runtime.schema);
    const augSelect =
      rewritten.select !== undefined && expandKeys.length > 0
        ? [...new Set([...rewritten.select, ...expandKeys])]
        : rewritten.select;

    const executionQuery: QueryRequest =
      augSelect !== rewritten.select
        ? ({ ...rewritten, select: augSelect } as QueryRequest)
        : rewritten;

    const execOptions: ExecutionOptions = {
      commandTimeoutSeconds: options.commandTimeoutSeconds,
      rowLimit: isCount ? 0 : effectiveTop + 1,
    };

    if (isCount) {
      const countVal = await executor.count(
        {
          connection: runtime.connection,
          schema: runtime.schema,
          query: executionQuery,
          options: { ...execOptions, rowLimit: 0 },
        },
        signal,
      );
      return {
        status: 200,
        headers: { ...ODATA_HEADERS },
        body: buildCountPayload(countVal),
        contentType: "text/plain",
      };
    }

    const result = await executor.query(
      {
        connection: runtime.connection,
        schema: runtime.schema,
        query: executionQuery,
        options: execOptions,
      },
      signal,
    );

    const rows = result.rows as Row[];

    let inlineCount: number | undefined;
    if (queryOpts.count) {
      inlineCount = await executor.count(
        {
          connection: runtime.connection,
          schema: runtime.schema,
          query: rewritten,
          options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 0 },
        },
        signal,
      );
    }

    if (masks.size > 0) {
      for (const row of rows) {
        for (const [k, v] of masks) row.set(k, v);
      }
    }

    if (queryOpts.expand.length > 0) {
      const expander = new ExpansionExecutor(
        executor,
        runtime.connection,
        runtime.schema,
        { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 10_000 },
        runtime.name,
        (childTable) => {
          const d = this.authorize(req.identity, roleRules, runtime.name, childTable);
          const epd: import("./expand.js").ExpandPolicyDecision = {
            allowed: d.allowed,
            hidden: d.hidden,
            deniedFields: d.deniedFields,
            maskedFields: d.maskedFields,
            ...(d.rowFilter !== undefined ? { rowFilter: d.rowFilter } : {}),
          };
          return epd;
        },
      );
      try {
        await expander.expand(table, rows, queryOpts.expand, signal);
      } catch (err) {
        if (err instanceof FieldDeniedByExpandError) {
          return jsonError(403, ErrorCodes.ForbiddenExpandDenied, err.message);
        }
        throw err;
      }
    }

    // Strip helper columns added for stitching
    if (rawQuery.select !== undefined && expandKeys.length > 0) {
      const origSelectSet = new Set(rawQuery.select);
      const toRemove = expandKeys.filter((k) => !origSelectSet.has(k));
      if (toRemove.length > 0) {
        for (const row of rows) {
          for (const col of toRemove) row.remove(col);
        }
      }
    }

    const pageRows = result.hasMore ? rows.slice(0, effectiveTop) : rows;

    if (key !== undefined) {
      if (pageRows.length === 0) {
        return jsonError(404, "NotFound", "Entity not found.");
      }
      return json(200, buildSinglePayload(req.serviceRoot, tableName, pageRows[0]!));
    }

    let nextLink: string | undefined;
    if (result.hasMore) {
      const nextSkip = (skip ?? 0) + pageRows.length;
      const token = this.config.skipTokenCodec.encode(nextSkip);
      nextLink = buildNextLink(req, token);
    }

    return json(
      200,
      buildCollectionPayload(req.serviceRoot, tableName, pageRows, {
        ...(inlineCount !== undefined ? { count: inlineCount } : {}),
        ...(nextLink !== undefined ? { nextLink } : {}),
      }),
    );
  }

  private async handleApply(
    req: ODataRequest,
    runtime: ServiceRuntime,
    table: TableModel,
    decision: PolicyDecision,
    queryOpts: ParsedQuery,
    signal?: AbortSignal,
  ): Promise<ODataResponse> {
    const apply = queryOpts.apply!;

    for (const field of [
      ...apply.groupBy,
      ...apply.aggregations.filter((a) => a.field !== undefined).map((a) => a.field!),
    ]) {
      if (decision.deniedFields.has(field) || decision.maskedFields.has(field)) {
        return jsonError(
          403,
          ErrorCodes.ForbiddenFieldDenied,
          `Access to field '${field}' is denied.`,
        );
      }
    }

    let filter: FilterNode | undefined = queryOpts.filter;
    if (queryOpts.applyFilter !== undefined) {
      filter = filter ? logical("and", [filter, queryOpts.applyFilter]) : queryOpts.applyFilter;
    }
    if (decision.rowFilter !== undefined) {
      filter = filter ? logical("and", [filter, decision.rowFilter]) : decision.rowFilter;
    }

    const query: QueryRequest = {
      serviceName: runtime.name,
      table: table.exposedName,
      apply,
      orderBy: [],
      expand: [],
      count: false,
      ...(filter !== undefined ? { filter } : {}),
    } as QueryRequest;

    const executor = this.config.getExecutor(runtime);
    const options = runtime.options ?? DEFAULT_SERVICE_OPTIONS;
    const result = await executor.query(
      {
        connection: runtime.connection,
        schema: runtime.schema,
        query,
        options: { commandTimeoutSeconds: options.commandTimeoutSeconds, rowLimit: 100_000 },
      },
      signal,
    );

    return json(
      200,
      buildApplyPayload(
        req.serviceRoot,
        table.exposedName,
        result.rows,
        apply.groupBy,
        apply.aggregations.map((a) => a.alias),
      ),
    );
  }

  private authorize(
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
    serviceName: string,
    table: TableModel,
  ): PolicyDecision {
    if (identity.bypass) return FULL_ACCESS;
    return this.config.policyEngine.authorize(
      identity,
      roleRules,
      serviceName,
      table.exposedName,
      Verb.Get,
      table.columns.map((c) => c.exposedName),
      this.config.rowFilterParser,
    );
  }
}

// ---- Pure helpers ----

function buildKeyFilter(key: Record<string, unknown>, table: TableModel): FilterNode {
  const pairs = Object.entries(key);

  if (pairs.length === 1 && pairs[0]![0] === "__positional__") {
    const pkCol = table.primaryKey[0];
    if (pkCol === undefined) {
      // Keyless table — use positional value against first column
      const col = table.columns[0]!.exposedName;
      return comparison(fieldRef(col), "eq", constant(pairs[0]![1]));
    }
    return comparison(fieldRef(pkCol), "eq", constant(pairs[0]![1]));
  }

  let filter: FilterNode | undefined;
  for (const [name, val] of pairs) {
    const node = comparison(fieldRef(name), "eq", constant(val));
    filter = filter === undefined ? node : logical("and", [filter, node]);
  }
  return filter!;
}

function requiredExpandKeys(
  table: TableModel,
  expands: readonly { navigation: string }[],
  schema: SchemaSnapshot,
): string[] {
  const keys: string[] = [];
  for (const expand of expands) {
    const toOne = table.foreignKeys.find((f) => f.navToOne === expand.navigation);
    if (toOne !== undefined) {
      keys.push(...toOne.columns);
      continue;
    }
    const toMany = schema.tables
      .flatMap((t) => t.foreignKeys)
      .find((f) => f.refTable === table.exposedName && f.navToMany === expand.navigation);
    if (toMany !== undefined) {
      keys.push(...toMany.refColumns);
    }
  }
  return [...new Set(keys)];
}

function buildNextLink(req: ODataRequest, token: string): string {
  const parts = req.queryString
    .split("&")
    .filter(
      (p) => !p.startsWith("$skiptoken=") && !p.startsWith("%24skiptoken=") && p.length > 0,
    );
  parts.push(`$skiptoken=${encodeURIComponent(token)}`);
  const base = `${trimSlash(req.serviceRoot)}/${req.path}`;
  return `${base}?${parts.join("&")}`;
}

function trimSlash(s: string): string {
  return s.endsWith("/") ? s.slice(0, -1) : s;
}

function json(status: number, body: unknown): ODataResponse {
  return {
    status,
    headers: { ...ODATA_HEADERS },
    body: JSON.stringify(body),
    contentType: "application/json;odata.metadata=minimal",
  };
}

function jsonError(status: number, code: string, message: string): ODataResponse {
  return {
    status,
    headers: { ...ODATA_HEADERS },
    body: JSON.stringify(buildErrorPayload(code, message)),
    contentType: "application/json",
  };
}

function mapError(err: unknown): ODataResponse {
  if (err instanceof FieldDeniedError) {
    return jsonError(403, ErrorCodes.ForbiddenFieldDenied, err.message);
  }
  if (err instanceof ODataParseError) {
    return jsonError(400, ErrorCodes.ValidationBadFilter, err.message);
  }
  if (err instanceof QueryValidationError) {
    return jsonError(400, err.code, err.message);
  }
  if (err instanceof NotSupportedQueryError) {
    return jsonError(501, "NotImplemented", err.message);
  }
  if (err instanceof Error) {
    return jsonError(500, ErrorCodes.InternalUnmapped, err.message);
  }
  return jsonError(500, ErrorCodes.InternalUnmapped, "Internal server error.");
}
