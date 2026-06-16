/**
 * Express integration entry point (port of EzOdataBuilder, spec 15 §3).
 * Composition root — may import everything.
 *
 * Usage:
 *   const { router, stop } = await ezodata({ services: [...], roles: [...], auth: { mode: "none" } });
 *   app.use("/api", router);
 */

import express, { type Router, type Request, type Response } from "express";
import { ODataHandler, type ODataHandlerConfig } from "../odata/handler.js";
import { ODataWriteHandler, type ODataWriteHandlerConfig } from "../odata/write-handler.js";
import { RestHandler, type RestHandlerConfig } from "../rest/handler.js";
import { McpServer, type McpHandlerConfig, DEFAULT_MCP_OPTIONS, type McpOptions } from "../mcp/server.js";
import { generateOpenApi, type ApiDialect } from "../openapi/generator.js";
import { trimSnapshot } from "../core/policy/trimmer.js";
import { PolicyEngine } from "../core/policy/engine.js";
import { SkipTokenCodec } from "../odata/skiptoken.js";
import { type RoleRuleSet, type RequestIdentity, type AccessRule } from "../core/policy/model.js";
import type { ServiceRuntime, QueryExecutor, WriteExecutor, ConnectionSpec } from "../connectors/contracts.js";
import { DEFAULT_SERVICE_OPTIONS, type ServiceOptions } from "../core/services.js";
import { KnexQueryExecutor } from "../connectors/executors/knex-query-executor.js";
import { KnexWriteExecutor } from "../connectors/executors/knex-write-executor.js";
import type { KnexClient } from "../connectors/knex-helpers.js";
import { createAuthMiddleware, type AuthConfig, type EzRequest, hashRoleName } from "./auth.js";
import { ANONYMOUS_IDENTITY } from "../core/policy/model.js";

// Pre-import all dialects/introspectors (composition root may depend on everything)
import { PostgreSqlDialect } from "../connectors/dialects/postgres.js";
import { MySqlDialect } from "../connectors/dialects/mysql.js";
import { SqlServerDialect } from "../connectors/dialects/sqlserver.js";
import { SqliteDialect } from "../connectors/dialects/sqlite.js";
import { PostgreSqlIntrospector } from "../connectors/introspectors/postgres-introspector.js";
import { MySqlIntrospector } from "../connectors/introspectors/mysql-introspector.js";
import { SqlServerIntrospector } from "../connectors/introspectors/sqlserver-introspector.js";
import { SqliteIntrospector } from "../connectors/introspectors/sqlite-introspector.js";
import type { SqlDialect, SchemaIntrospector } from "../connectors/contracts.js";

// ---- Public config types ----

export interface ServiceConfig {
  readonly name: string;
  readonly connector: "postgresql" | "mysql" | "sqlserver" | "sqlite";
  readonly connection: Partial<Omit<ConnectionSpec, "tls" | "extra">> & {
    filePath?: string;
    tls?: ConnectionSpec["tls"];
    extra?: Record<string, string>;
  };
  readonly options?: Partial<ServiceOptions>;
}

export interface RoleConfig {
  readonly name: string;
  readonly bypassDataRules?: boolean;
  readonly rules?: readonly Omit<AccessRule, "id">[];
}

export interface EzOdataConfig {
  readonly services: readonly ServiceConfig[];
  readonly roles?: readonly RoleConfig[];
  readonly auth?: AuthConfig;
  readonly skipTokenSecret?: string | Buffer;
  readonly mcp?: Partial<McpOptions>;
}

export interface EzOdataInstance {
  readonly router: Router;
  stop(): Promise<void>;
}

// ---- Factory ----

export async function ezodata(config: EzOdataConfig): Promise<EzOdataInstance> {
  const { services: serviceConfigs, roles: roleConfigs = [], auth, skipTokenSecret, mcp: mcpOpts } = config;

  const runtimes = new Map<string, ServiceRuntime>();
  const queryExecutors = new Map<string, QueryExecutor>();
  const writeExecutors = new Map<string, WriteExecutor>();

  for (const svc of serviceConfigs) {
    const connection = buildConnection(svc);
    const options: ServiceOptions = { ...DEFAULT_SERVICE_OPTIONS, ...svc.options };
    const { introspector, dialect, knexClient } = resolveDialect(svc.connector);

    const schema = await introspector.introspect(connection, {
      includeSchemas: options.includeSchemas,
      excludeTables: options.excludeTables,
      includeViews: options.includeViews,
      exposedNameStyle: options.exposedNameStyle,
    });

    const runtime: ServiceRuntime = {
      name: svc.name,
      connectorType: svc.connector,
      connection,
      schema,
      options,
      schemaVersion: `${schema.version}-${schema.collectedAt}`,
      status: "active",
    };

    runtimes.set(svc.name, runtime);
    queryExecutors.set(svc.name, new KnexQueryExecutor(dialect, knexClient));
    if (!options.readOnly) {
      writeExecutors.set(svc.name, new KnexWriteExecutor(dialect, knexClient));
    }
  }

  const roleRuleSets: RoleRuleSet[] = roleConfigs.map((rc) => ({
    roleId: hashRoleName(rc.name),
    roleName: rc.name,
    bypassDataRules: rc.bypassDataRules ?? false,
    rules: (rc.rules ?? []).map((r, ridx) => ({ id: ridx + 1, ...r })),
  }));

  const policyEngine = new PolicyEngine();
  const resolveRuntime = async (name: string): Promise<ServiceRuntime | undefined> => runtimes.get(name);
  const getQueryExecutor = (rt: ServiceRuntime): QueryExecutor => queryExecutors.get(rt.name)!;
  const getWriteExecutor = (rt: ServiceRuntime): WriteExecutor | undefined => writeExecutors.get(rt.name);

  const resolveRoleRules = async (
    identity: RequestIdentity,
    _runtime: ServiceRuntime,
  ): Promise<readonly RoleRuleSet[]> =>
    identity.bypass ? roleRuleSets : roleRuleSets.filter((rs) => identity.roleIds.includes(rs.roleId));

  const rowFilterParser = (): never => {
    throw new Error("Row filter parsing requires OData parser — not yet wired.");
  };

  const skipTokenCodec = new SkipTokenCodec(
    skipTokenSecret !== undefined
      ? Buffer.isBuffer(skipTokenSecret)
        ? skipTokenSecret
        : Buffer.from(skipTokenSecret)
      : Buffer.alloc(32, "default-dev-key-00"),
  );

  const authMiddleware = createAuthMiddleware(auth ?? { mode: "none" });
  const router = express.Router();
  router.use(express.json());
  router.use(authMiddleware as unknown as (req: Request, res: Response, next: () => void) => void);

  // ---- OData ----
  const odataConfig: ODataHandlerConfig = {
    resolveRuntime,
    getExecutor: getQueryExecutor,
    skipTokenCodec,
    policyEngine,
    resolveRoleRules,
    rowFilterParser,
  };
  const odataHandler = new ODataHandler(odataConfig);

  const odataWriteConfig: ODataWriteHandlerConfig = {
    resolveRuntime,
    getWriteExecutor,
    getQueryExecutor,
    policyEngine,
    resolveRoleRules,
    rowFilterParser,
  };
  const odataWriteHandler = new ODataWriteHandler(odataWriteConfig);

  const getIdentity = (req: Request): RequestIdentity =>
    (req as EzRequest).ezIdentity ?? ANONYMOUS_IDENTITY;

  const getQs = (req: Request): string => {
    const raw = req.url;
    const idx = raw.indexOf("?");
    return idx < 0 ? "" : raw.slice(idx + 1);
  };

  const getParam = (req: Request, name: string): string => {
    const v = req.params[name];
    return Array.isArray(v) ? v.join("/") : (v ?? "");
  };

  router.get("/odata/:service/\\$metadata", async (req: Request, res: Response) => {
    const svc = getParam(req, "service");
    const resp = await odataHandler.handleForService(svc, {
      method: req.method, path: "$metadata", queryString: getQs(req),
      serviceRoot: buildRoot(req, "odata", svc), identity: getIdentity(req),
      headers: req.headers as Record<string, string>,
    });
    sendODataResp(res, resp);
  });

  router.get("/odata/:service", async (req: Request, res: Response) => {
    const svc = getParam(req, "service");
    const resp = await odataHandler.handleForService(svc, {
      method: req.method, path: "", queryString: "",
      serviceRoot: buildRoot(req, "odata", svc), identity: getIdentity(req),
      headers: req.headers as Record<string, string>,
    });
    sendODataResp(res, resp);
  });

  router.all("/odata/:service/*path", async (req: Request, res: Response) => {
    const svc = getParam(req, "service");
    const subPath = getParam(req, "path");
    const method = req.method.toUpperCase();
    const isWrite = ["POST", "PUT", "PATCH", "DELETE"].includes(method);
    const qs = getQs(req);
    const root = buildRoot(req, "odata", svc);

    if (isWrite) {
      const resp = await odataWriteHandler.handleForService(svc, {
        method: req.method, path: subPath, queryString: qs, serviceRoot: root,
        identity: getIdentity(req), headers: req.headers as Record<string, string>,
        body: req.body as unknown,
      });
      sendODataResp(res, resp);
    } else {
      const resp = await odataHandler.handleForService(svc, {
        method: req.method, path: subPath, queryString: qs, serviceRoot: root,
        identity: getIdentity(req), headers: req.headers as Record<string, string>,
      });
      sendODataResp(res, resp);
    }
  });

  // ---- REST ----
  const restConfig: RestHandlerConfig = {
    resolveRuntime, getQueryExecutor, getWriteExecutor, policyEngine, resolveRoleRules, rowFilterParser,
  };
  const restHandler = new RestHandler(restConfig);

  router.all("/rest/:service/*path", async (req: Request, res: Response) => {
    const svc = getParam(req, "service");
    const subPath = getParam(req, "path");
    const resp = await restHandler.handleForService(svc, {
      method: req.method, path: subPath, queryString: getQs(req),
      identity: getIdentity(req), headers: req.headers as Record<string, string>,
      body: req.body as unknown,
    });
    res.status(resp.status).contentType(resp.contentType).send(resp.body);
  });

  // ---- MCP ----
  const mcpConfig: McpHandlerConfig = {
    resolveRuntime, getQueryExecutor, getWriteExecutor, policyEngine, resolveRoleRules, rowFilterParser,
    visibleServices: async () => [...runtimes.keys()],
    options: { ...DEFAULT_MCP_OPTIONS, ...mcpOpts },
  };
  const mcpServer = new McpServer(mcpConfig);

  router.post("/mcp", async (req: Request, res: Response) => {
    const resp = await mcpServer.handle(req.body as Record<string, unknown>, getIdentity(req));
    if (resp === null) { res.status(204).send(); } else { res.status(200).json(resp); }
  });

  // ---- OpenAPI ----
  router.get("/openapi/:service", async (req: Request, res: Response) => {
    const svc = getParam(req, "service");
    const runtime = runtimes.get(svc);
    if (runtime === undefined) { res.status(404).json({ error: `Unknown service '${svc}'.` }); return; }

    const identity = getIdentity(req);
    const roleRuleSetsForIdentity = identity.bypass
      ? roleRuleSets
      : roleRuleSets.filter((rs) => identity.roleIds.includes(rs.roleId));

    const visible = trimSnapshot(runtime.schema, identity, roleRuleSetsForIdentity, runtime.name, policyEngine, rowFilterParser);

    const dialectParam = String(req.query["dialect"] ?? "odata");
    const dialect: ApiDialect = dialectParam === "rest" ? "rest" : "odata";
    const serviceRoot = buildRoot(req, dialect === "odata" ? "odata" : "rest", svc);
    const doc = generateOpenApi(visible, dialect, svc, svc, runtime.schemaVersion, serviceRoot);
    res.status(200).contentType("application/json").send(doc);
  });

  return { router, stop: async () => { /* no-op: Knex creates pools per-query */ } };
}

// ---- Internal helpers ----

function buildConnection(svc: ServiceConfig): ConnectionSpec {
  const c = svc.connection;
  return {
    tls: c.tls ?? { mode: "disable", allowInvalid: false },
    extra: c.extra ?? {},
    ...(c.host !== undefined ? { host: c.host } : {}),
    ...(c.port !== undefined ? { port: c.port } : {}),
    ...(c.database !== undefined ? { database: c.database } : {}),
    ...(c.username !== undefined ? { username: c.username } : {}),
    ...(c.password !== undefined ? { password: c.password } : {}),
    ...(c.filePath !== undefined ? { filePath: c.filePath } : {}),
  };
}

interface DialectBundle {
  readonly introspector: SchemaIntrospector;
  readonly dialect: SqlDialect;
  readonly knexClient: KnexClient;
}

function resolveDialect(connector: string): DialectBundle {
  switch (connector) {
    case "postgresql":
      return { introspector: new PostgreSqlIntrospector(), dialect: new PostgreSqlDialect(), knexClient: "pg" };
    case "mysql":
      return { introspector: new MySqlIntrospector(), dialect: new MySqlDialect(), knexClient: "mysql2" };
    case "sqlserver":
      return { introspector: new SqlServerIntrospector(), dialect: new SqlServerDialect(), knexClient: "mssql" };
    case "sqlite":
      return { introspector: new SqliteIntrospector(), dialect: new SqliteDialect(), knexClient: "better-sqlite3" };
    default:
      throw new Error(`Unknown connector '${connector}'.`);
  }
}

function buildRoot(req: Request, protocol: string, serviceName: string): string {
  return `${req.protocol}://${req.get("host") ?? "localhost"}${req.baseUrl}/${protocol}/${serviceName}`;
}

function sendODataResp(
  res: Response,
  resp: { status: number; headers: Record<string, string>; body: string | Buffer; contentType: string },
): void {
  for (const [k, v] of Object.entries(resp.headers)) res.setHeader(k, v);
  res.status(resp.status).contentType(resp.contentType).send(resp.body);
}
