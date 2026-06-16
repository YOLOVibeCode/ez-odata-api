/**
 * Conformance suite for the SQLite dialect.
 * Uses better-sqlite3 in-memory (via temp file) — no container needed.
 * Runs a representative subset of the YAML conformance cases plus
 * SQLite-specific dialect assertions.
 */

import Database from "better-sqlite3";
import { readFileSync, readdirSync, mkdirSync, rmSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import yaml from "js-yaml";
import express, { type Application } from "express";
import supertest from "supertest";

import { SqliteIntrospector } from "../../src/connectors/introspectors/sqlite-introspector.js";
import { KnexQueryExecutor } from "../../src/connectors/executors/knex-query-executor.js";
import { KnexWriteExecutor } from "../../src/connectors/executors/knex-write-executor.js";
import { SqliteDialect } from "../../src/connectors/dialects/sqlite.js";
import type { ConnectionSpec } from "../../src/connectors/contracts.js";
import { ODataHandler } from "../../src/odata/handler.js";
import { ODataWriteHandler } from "../../src/odata/write-handler.js";
import { SkipTokenCodec } from "../../src/odata/skiptoken.js";
import { PolicyEngine } from "../../src/core/policy/engine.js";
import { DEV_BYPASS_IDENTITY } from "../../src/core/policy/model.js";
import type { SchemaSnapshot } from "../../src/core/schema.js";
import { DEFAULT_SERVICE_OPTIONS } from "../../src/core/services.js";
import type { ServiceRuntime, QueryExecutor } from "../../src/connectors/contracts.js";

// ---- Conformance case types ----

interface ConformanceCase {
  name: string;
  request: string;
  expect: {
    status?: number;
    headers?: Record<string, string>;
    jsonpath?: Record<string, unknown>;
    bodyContains?: string[];
    bodyNotContains?: string[];
  };
}

// ---- JSONPath evaluator ----

interface JsonPathResult {
  found: boolean;
  value?: unknown;
  length?: number;
}

function evaluateJsonPath(root: unknown, path: string): JsonPathResult {
  let p = path.startsWith("$") ? path.slice(1) : path;
  let current: unknown = root;
  let found = true;

  while (p.length > 0) {
    if (p === ".length()") {
      if (Array.isArray(current)) {
        return { found: true, value: current.length, length: current.length };
      }
      return { found: false };
    }
    const bracketMatch = p.match(/^\['([^']+)'\](.*)/s);
    if (bracketMatch) {
      const key = bracketMatch[1]!;
      const rest = bracketMatch[2]!;
      if (current === null || typeof current !== "object") { found = false; break; }
      if (!(key in (current as Record<string, unknown>))) return { found: false };
      current = (current as Record<string, unknown>)[key];
      p = rest;
      continue;
    }
    const indexMatch = p.match(/^\[(\d+)\](.*)/s);
    if (indexMatch) {
      const idx = parseInt(indexMatch[1]!, 10);
      const rest = indexMatch[2]!;
      if (!Array.isArray(current) || idx >= current.length) return { found: false };
      current = current[idx];
      p = rest;
      continue;
    }
    const propMatch = p.match(/^\.([a-zA-Z_@][a-zA-Z0-9_@]*)(.*)/s);
    if (propMatch) {
      const key = propMatch[1]!;
      const rest = propMatch[2]!;
      if (current === null || typeof current !== "object") { found = false; break; }
      if (!(key in (current as Record<string, unknown>))) return { found: false };
      current = (current as Record<string, unknown>)[key];
      p = rest;
      continue;
    }
    if (p.startsWith(".length()")) {
      if (Array.isArray(current)) {
        return { found: true, value: current.length, length: current.length };
      }
      return { found: false };
    }
    break;
  }
  return found ? { found: true, value: current } : { found: false };
}

// ---- Test setup ----

const SERVICE_NAME = "sales";
const SKIP_TOKEN_KEY = Buffer.from("conformance-test-signing-key-32b!");

// SQLite cases that are inherently dialect-incompatible
const SQLITE_SKIP = new Set([
  // SQLite maps all integer types to Edm.Int64 (no Edm.Int32)
  "metadata-has-keys-and-types",
]);

describe(
  "HTTP Conformance Suite (SQLite)",
  { timeout: 60_000 },
  () => {
    let tempDir: string;
    let dbFile: string;
    let spec: ConnectionSpec;
    let schema: SchemaSnapshot;
    let app: Application;

    beforeAll(
      async () => {
        // Create a temp SQLite file
        tempDir = join(tmpdir(), `sqlite-conformance-${Date.now()}`);
        mkdirSync(tempDir, { recursive: true });
        dbFile = join(tempDir, "northwind.db");

        const fixturePath = fileURLToPath(
          new URL("./fixtures/northwind-sqlite.sql", import.meta.url),
        );
        const sql = readFileSync(fixturePath, "utf8");

        // Apply fixture using better-sqlite3 directly (supports exec of multi-statement SQL)
        const rawDb = new Database(dbFile);
        rawDb.pragma("foreign_keys = ON");
        rawDb.exec(sql);
        rawDb.close();

        spec = {
          filePath: dbFile,
          tls: { mode: "disable", allowInvalid: false },
          extra: {},
        };

        schema = await new SqliteIntrospector().introspect(spec, {
          includeSchemas: [],
          excludeTables: [],
          includeViews: true,
          exposedNameStyle: "original",
        });

        const dialect = new SqliteDialect();
        const executor: QueryExecutor = new KnexQueryExecutor(dialect, "better-sqlite3");
        const writeExecutor = new KnexWriteExecutor(dialect, "better-sqlite3");

        const runtime: ServiceRuntime = {
          name: SERVICE_NAME,
          connectorType: "sqlite",
          connection: spec,
          schema,
          options: DEFAULT_SERVICE_OPTIONS,
          schemaVersion: "1",
          status: "active",
        };

        const policyEngine = new PolicyEngine();
        const resolveRuntime = async (name: string) =>
          name === SERVICE_NAME ? runtime : undefined;

        const handler = new ODataHandler({
          resolveRuntime,
          getExecutor: () => executor,
          skipTokenCodec: new SkipTokenCodec(SKIP_TOKEN_KEY),
          policyEngine,
          resolveRoleRules: async () => [],
          rowFilterParser: () => {
            throw new Error("row filter parser not configured");
          },
        });

        const writeHandler = new ODataWriteHandler({
          resolveRuntime,
          getWriteExecutor: () => writeExecutor,
          getQueryExecutor: () => executor,
          policyEngine,
          resolveRoleRules: async () => [],
          rowFilterParser: () => {
            throw new Error("row filter parser not configured");
          },
        });

        app = express();
        app.use(express.json());

        app.use("/api/odata/:serviceName", async (req, res) => {
          const serviceName = req.params["serviceName"]!;
          const relativeUrl = req.url;
          const qsIdx = relativeUrl.indexOf("?");
          const rawPath = qsIdx >= 0 ? relativeUrl.slice(0, qsIdx) : relativeUrl;
          const qs = qsIdx >= 0 ? relativeUrl.slice(qsIdx + 1) : "";
          const path = rawPath.startsWith("/") ? rawPath.slice(1) : rawPath;
          const serviceRoot = `${req.protocol}://${req.get("host")}/api/odata/${serviceName}`;
          const method = req.method.toUpperCase();
          const isRead = method === "GET" || method === "HEAD";

          const baseReq = {
            method: req.method,
            path,
            queryString: qs,
            serviceRoot,
            identity: DEV_BYPASS_IDENTITY,
            headers: req.headers as Record<string, string>,
          };

          let odataRes;
          try {
            if (isRead) {
              odataRes = await handler.handleForService(serviceName, baseReq);
            } else {
              odataRes = await writeHandler.handleForService(serviceName, {
                ...baseReq,
                body: req.body as unknown,
              });
            }
          } catch (err) {
            res.status(500).json({ error: { code: "InternalError", message: String(err) } });
            return;
          }

          res.status(odataRes.status);
          for (const [k, v] of Object.entries(odataRes.headers)) {
            res.set(k, v);
          }
          res.type(odataRes.contentType).send(odataRes.body);
        });
      },
      30_000,
    );

    afterAll(() => {
      try { rmSync(tempDir, { recursive: true, force: true }); } catch { /* ignore */ }
    });

    // ---- Load and run YAML conformance cases ----

    const casesDir = fileURLToPath(
      new URL(
        "../../../tests/EzOdata.ConformanceTests/cases",
        import.meta.url,
      ),
    );

    const yamlFiles = readdirSync(casesDir)
      .filter((f) => f.endsWith(".yaml"))
      .sort();

    const allCases: Array<{ file: string; testCase: ConformanceCase }> = [];
    for (const file of yamlFiles) {
      const contents = readFileSync(`${casesDir}/${file}`, "utf8");
      const cases = yaml.load(contents) as ConformanceCase[];
      for (const tc of cases) {
        allCases.push({ file: file.replace(".yaml", ""), testCase: tc });
      }
    }

    for (const { file, testCase } of allCases) {
      const skip = SQLITE_SKIP.has(testCase.name);
      it.skipIf(skip)(`[${file}] ${testCase.name}`, async () => {
        const firstSpace = testCase.request.indexOf(" ");
        const method = (firstSpace >= 0 ? testCase.request.slice(0, firstSpace) : testCase.request).toUpperCase();
        const rawUrl = firstSpace >= 0 ? testCase.request.slice(firstSpace + 1) : "/";
        let url = rawUrl.replace(/\n/g, "").replace(/ /g, "%20").replace(/'/g, "%27");

        if (url.startsWith("/") && !url.startsWith("/api/")) {
          url = `/api/odata/${SERVICE_NAME}${url === "/" ? "" : url}`;
        }

        const agent = supertest(app);
        let req: ReturnType<typeof agent.get>;
        if (method === "GET") {
          req = agent.get(url);
        } else {
          req = agent.get(url);
        }

        const res = await req;
        const rawBodyStr = res.text && res.text.length > 0
          ? res.text
          : typeof res.body === "string"
            ? res.body
            : JSON.stringify(res.body ?? "");

        const expectedStatus = testCase.expect.status ?? 200;
        expect(res.status, `[${testCase.name}] expected status ${expectedStatus}: ${rawBodyStr.slice(0, 200)}`).toBe(expectedStatus);

        if (testCase.expect.headers) {
          for (const [headerName, headerValue] of Object.entries(testCase.expect.headers)) {
            const actual = res.headers[headerName.toLowerCase()] as string | undefined;
            expect(actual, `[${testCase.name}] header ${headerName} = '${actual}'`).toContain(headerValue);
          }
        }

        for (const fragment of testCase.expect.bodyContains ?? []) {
          expect(rawBodyStr, `[${testCase.name}] body should contain '${fragment}'`).toContain(fragment);
        }
        for (const fragment of testCase.expect.bodyNotContains ?? []) {
          expect(rawBodyStr, `[${testCase.name}] body should NOT contain '${fragment}'`).not.toContain(fragment);
        }

        if (testCase.expect.jsonpath) {
          let parsed: unknown;
          try {
            parsed = typeof res.body === "object" ? res.body : JSON.parse(res.text ?? "{}");
          } catch {
            parsed = {};
          }
          for (const [jsonPath, expectationVal] of Object.entries(testCase.expect.jsonpath)) {
            assertJsonPath(parsed, jsonPath, expectationVal, testCase.name);
          }
        }
      });
    }

    // ---- SQLite-specific dialect tests ----

    it("sqlite: double-quote identifier quoting in metadata", async () => {
      // Verify metadata is produced correctly (no syntax errors from quoting)
      const res = await supertest(app).get(`/api/odata/${SERVICE_NAME}/$metadata`);
      expect(res.status).toBe(200);
      expect(res.text).toContain("EntityType");
    });

    it("sqlite: LIMIT -1 (skip without explicit limit) paginates correctly", async () => {
      // $skip without $top should return remaining items using LIMIT -1
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/customers?$orderby=id&$skip=7`);
      expect(res.status).toBe(200);
      const body = res.body as { value: unknown[] };
      expect(body.value.length).toBe(1);
    });

    it("sqlite: PRAGMA-based introspection detects primary keys", async () => {
      const customersTable = schema.tables.find((t) => t.exposedName === "customers");
      expect(customersTable).toBeDefined();
      expect(customersTable!.primaryKey).toEqual(["id"]);
      expect(customersTable!.columns.find((c) => c.exposedName === "id")?.isAutoGenerated).toBe(true);
    });

    it("sqlite: composite PK introspected correctly", async () => {
      const orderItems = schema.tables.find((t) => t.exposedName === "order_items");
      expect(orderItems).toBeDefined();
      expect(orderItems!.primaryKey).toHaveLength(2);
      expect(orderItems!.primaryKey).toContain("order_id");
      expect(orderItems!.primaryKey).toContain("line_no");
    });

    it("sqlite: FK navigation introspected (orders → customer)", async () => {
      const ordersTable = schema.tables.find((t) => t.exposedName === "orders");
      expect(ordersTable).toBeDefined();
      const fk = ordersTable!.foreignKeys.find((f) => f.refTable === "customers");
      expect(fk).toBeDefined();
    });

    it("sqlite: view included when includeViews=true", async () => {
      const view = schema.tables.find((t) => t.exposedName === "v_customer_order_totals");
      expect(view).toBeDefined();
      expect(view!.isView).toBe(true);
    });

    it("sqlite: $filter with contains (ASCII case-insensitive LIKE)", async () => {
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/customers?$filter=contains(name,'corp')&$count=true`);
      expect(res.status).toBe(200);
      // SQLite LIKE is case-insensitive for ASCII: 'Acme Corp' and 'Tyrell Corp'
      expect((res.body as { "@odata.count": number })["@odata.count"]).toBe(2);
    });

    it("sqlite: $orderby + $skip + $top pagination", async () => {
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/customers?$orderby=id&$skip=2&$top=3`);
      expect(res.status).toBe(200);
      const body = res.body as { value: Array<{ id: number }> };
      expect(body.value.length).toBe(3);
      expect(body.value[0]!.id).toBe(3);
    });

    it("sqlite: $expand to-one navigates FK correctly", async () => {
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/orders(1)?$expand=customer&$select=id,status`);
      expect(res.status).toBe(200);
      expect((res.body as { customer: { name: string } }).customer.name).toBe("Acme Corp");
    });

    it("sqlite: self-referencing FK (employees.manager_id)", async () => {
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/employees?$orderby=id`);
      expect(res.status).toBe(200);
      const body = res.body as { value: Array<{ id: number; manager_id: number | null }> };
      // Diana Prince is root (manager_id null)
      expect(body.value[0]!.manager_id).toBeNull();
      expect(body.value[1]!.manager_id).toBe(1);
    });

    it("sqlite: instr-based indexOf in $filter", async () => {
      // indexof('email','@') ge 0 → all customers (all have @)
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/customers?$filter=indexof(email,'@') ge 0&$count=true`);
      expect(res.status).toBe(200);
      expect((res.body as { "@odata.count": number })["@odata.count"]).toBe(8);
    });

    it("sqlite: $apply groupby aggregate works", async () => {
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/orders?$apply=groupby((status),aggregate(total with sum as revenue))`);
      expect(res.status).toBe(200);
      const body = res.body as { value: unknown[] };
      expect(body.value.length).toBeGreaterThanOrEqual(3); // open, shipped, cancelled
    });

    it("sqlite: POST creates a new customer and returns it", async () => {
      const res = await supertest(app)
        .post(`/api/odata/${SERVICE_NAME}/customers`)
        .set("Content-Type", "application/json")
        .send(JSON.stringify({ name: "Test Co", email: "test@test.example", country: "US" }));
      expect(res.status).toBe(201);
      expect((res.body as { name: string }).name).toBe("Test Co");
    });

    it("sqlite: PUT updates an existing record", async () => {
      // First insert
      const ins = await supertest(app)
        .post(`/api/odata/${SERVICE_NAME}/products`)
        .set("Content-Type", "application/json")
        .send(JSON.stringify({ sku: "TEST-PUT", name: "Temp Product", price: 1.00, discontinued: false }));
      expect(ins.status).toBe(201);
      const id = (ins.body as { id: number }).id;

      const upd = await supertest(app)
        .put(`/api/odata/${SERVICE_NAME}/products(${id})`)
        .set("Content-Type", "application/json")
        .send(JSON.stringify({ sku: "TEST-PUT", name: "Updated Product", price: 2.00, discontinued: false }));
      expect(upd.status).toBe(200);
    });

    it("sqlite: DELETE removes a record", async () => {
      const ins = await supertest(app)
        .post(`/api/odata/${SERVICE_NAME}/employees`)
        .set("Content-Type", "application/json")
        .send(JSON.stringify({ name: "Temp Employee" }));
      expect(ins.status).toBe(201);
      const id = (ins.body as { id: number }).id;

      const del = await supertest(app)
        .delete(`/api/odata/${SERVICE_NAME}/employees(${id})`);
      expect(del.status).toBe(204);

      const get = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/employees(${id})`);
      expect(get.status).toBe(404);
    });
  },
);

function assertJsonPath(
  root: unknown,
  path: string,
  expectationRaw: unknown,
  caseName: string,
): void {
  const expectation = String(expectationRaw);
  const { found, value, length } = evaluateJsonPath(root, path);

  if (expectation === "absent") {
    expect(!found, `[${caseName}] ${path} should be absent but was found`).toBe(true);
    return;
  }

  expect(found, `[${caseName}] ${path} not found in ${JSON.stringify(root).slice(0, 300)}`).toBe(true);
  if (expectation === "exists") return;

  const actualNumber = length ?? (typeof value === "number" ? value : undefined);

  for (const op of [">=", "<=", ">", "<"] as const) {
    if (expectation.startsWith(op)) {
      const bound = parseFloat(expectation.slice(op.length).trim());
      expect(actualNumber, `[${caseName}] ${path} is not numeric`).toBeDefined();
      const ok =
        op === ">=" ? (actualNumber ?? 0) >= bound :
        op === "<=" ? (actualNumber ?? 0) <= bound :
        op === ">" ? (actualNumber ?? 0) > bound :
        (actualNumber ?? 0) < bound;
      expect(ok, `[${caseName}] ${path}: expected ${expectation}, got ${actualNumber}`).toBe(true);
      return;
    }
  }

  const numericExpect = parseFloat(expectation);
  if (!isNaN(numericExpect) && expectation.trim() !== "") {
    const actualNum = typeof value === "number" ? value : typeof value === "string" ? parseFloat(value) : Number(value);
    expect(actualNum, `[${caseName}] ${path}: expected ${numericExpect}, got ${String(value)}`).toBe(numericExpect);
    return;
  }

  if (expectation === "true" || expectation === "false") {
    expect(value === (expectation === "true"), `[${caseName}] ${path}: expected ${expectation}, got ${String(value)}`).toBe(true);
    return;
  }

  expect(String(value ?? ""), `[${caseName}] ${path}: expected '${expectation}', got '${String(value)}'`).toBe(expectation);
}
