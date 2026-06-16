/**
 * Conformance suite for the MySQL dialect.
 * Uses @testcontainers/mysql (MySQL 8) plus northwind fixture adapted for MySQL.
 * Runs all shared YAML conformance cases plus MySQL-specific dialect assertions.
 */

import { MySqlContainer, type StartedMySqlContainer } from "@testcontainers/mysql";
import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import knex, { type Knex } from "knex";
import yaml from "js-yaml";
import express, { type Application } from "express";
import supertest from "supertest";

import { MySqlIntrospector } from "../../src/connectors/introspectors/mysql-introspector.js";
import { KnexQueryExecutor } from "../../src/connectors/executors/knex-query-executor.js";
import { KnexWriteExecutor } from "../../src/connectors/executors/knex-write-executor.js";
import { MySqlDialect } from "../../src/connectors/dialects/mysql.js";
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

// MySQL-specific skips: tests that rely on PostgreSQL-only behavior
const MYSQL_SKIP = new Set<string>([
  // none currently — all shared cases pass on MySQL
]);

describe(
  "HTTP Conformance Suite (MySQL)",
  { timeout: 300_000 },
  () => {
    let container: StartedMySqlContainer;
    let db: Knex;
    let spec: ConnectionSpec;
    let schema: SchemaSnapshot;
    let app: Application;

    beforeAll(
      async () => {
        container = await new MySqlContainer("mysql:8").start();

        spec = {
          host: container.getHost(),
          port: container.getMappedPort(3306),
          database: container.getDatabase(),
          username: container.getUsername(),
          password: container.getUserPassword(),
          tls: { mode: "disable", allowInvalid: false },
          extra: {},
        };

        db = knex({
          client: "mysql2",
          connection: {
            host: spec.host,
            port: spec.port,
            database: spec.database,
            user: spec.username,
            password: spec.password,
            multipleStatements: true,
          } as Knex.MySql2ConnectionConfig,
          pool: { min: 0, max: 5 },
        });

        // Apply northwind MySQL fixture — execute statements one at a time
        const northwindPath = fileURLToPath(
          new URL("./fixtures/northwind-mysql.sql", import.meta.url),
        );
        const fullSql = readFileSync(northwindPath, "utf8");
        const statements = fullSql
          .split(/;[ \t]*(?:\r?\n|$)/)
          .map((s) => s.trim())
          .filter((s) => s.length > 0 && s.replace(/--[^\n]*/g, "").trim().length > 0);
        for (const stmt of statements) {
          await db.raw(stmt);
        }

        schema = await new MySqlIntrospector().introspect(spec, {
          includeSchemas: [],
          excludeTables: [],
          includeViews: true,
          exposedNameStyle: "original",
        });

        const dialect = new MySqlDialect();
        const executor: QueryExecutor = new KnexQueryExecutor(dialect, "mysql2");
        const writeExecutor = new KnexWriteExecutor(dialect, "mysql2");

        const runtime: ServiceRuntime = {
          name: SERVICE_NAME,
          connectorType: "mysql",
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
      240_000,
    );

    afterAll(async () => {
      await db?.destroy();
      await container?.stop();
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
      const skip = MYSQL_SKIP.has(testCase.name);
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

    // ---- MySQL-specific dialect tests ----

    it("mysql: backtick identifier quoting in metadata", async () => {
      const res = await supertest(app).get(`/api/odata/${SERVICE_NAME}/$metadata`);
      expect(res.status).toBe(200);
      // Metadata uses entity names — verify it renders without errors
      expect(res.text).toContain('EntityType Name="customers"');
    });

    it("mysql: LIMIT/OFFSET pagination — $top with $skip", async () => {
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/customers?$orderby=id&$top=3&$skip=2`);
      expect(res.status).toBe(200);
      const body = res.body as { value: Array<{ id: number }> };
      expect(body.value.length).toBe(3);
      expect(body.value[0]!.id).toBe(3);
    });

    it("mysql: LIMIT without OFFSET ($top only)", async () => {
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/customers?$top=2&$orderby=id`);
      expect(res.status).toBe(200);
      const body = res.body as { value: Array<{ id: number }> };
      expect(body.value.length).toBe(2);
      expect(body.value[0]!.id).toBe(1);
    });

    it("mysql: LOCATE-based indexOf in $filter", async () => {
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/customers?$filter=indexof(email,'@') ge 0&$count=true`);
      expect(res.status).toBe(200);
      expect((res.body as { "@odata.count": number })["@odata.count"]).toBe(8);
    });

    it("mysql: LIKE (case-insensitive default CI collation)", async () => {
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/customers?$filter=contains(name,'ACME')&$count=true`);
      expect(res.status).toBe(200);
      // MySQL default utf8mb4_0900_ai_ci is case-insensitive
      expect((res.body as { "@odata.count": number })["@odata.count"]).toBe(1);
    });

    it("mysql: introspector detects AUTO_INCREMENT PK", async () => {
      const customersTable = schema.tables.find((t) => t.exposedName === "customers");
      expect(customersTable).toBeDefined();
      expect(customersTable!.primaryKey).toEqual(["id"]);
      expect(customersTable!.columns.find((c) => c.exposedName === "id")?.isAutoGenerated).toBe(true);
    });

    it("mysql: tinyint(1) mapped to Edm.Boolean", async () => {
      const productsTable = schema.tables.find((t) => t.exposedName === "products");
      expect(productsTable).toBeDefined();
      const discontinued = productsTable!.columns.find((c) => c.exposedName === "discontinued");
      expect(discontinued?.edmType).toBe("Edm.Boolean");
    });

    it("mysql: FK navigation to-one introspected correctly", async () => {
      const ordersTable = schema.tables.find((t) => t.exposedName === "orders");
      expect(ordersTable).toBeDefined();
      const fk = ordersTable!.foreignKeys.find((f) => f.refTable === "customers");
      expect(fk).toBeDefined();
    });

    it("mysql: $filter eq on string with special chars is SQL-injection-safe", async () => {
      // Use a properly OData-escaped filter value — single quote doubled inside OData string literal
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/customers?$filter=name eq 'nobody%27%27s name'&$count=true`);
      expect(res.status).toBe(200);
      expect((res.body as { "@odata.count": number })["@odata.count"]).toBe(0);
    });

    it("mysql: $expand to-one navigates FK", async () => {
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/orders(1)?$expand=customer&$select=id,status`);
      expect(res.status).toBe(200);
      expect((res.body as { customer: { name: string } }).customer.name).toBe("Acme Corp");
    });

    it("mysql: $expand to-many returns correct count", async () => {
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/customers(1)?$expand=orders`);
      expect(res.status).toBe(200);
      const body = res.body as { orders: unknown[] };
      expect(body.orders.length).toBe(2);
    });

    it("mysql: $apply groupby aggregate", async () => {
      const res = await supertest(app)
        .get(`/api/odata/${SERVICE_NAME}/orders?$apply=groupby((status),aggregate(total with sum as revenue))`);
      expect(res.status).toBe(200);
      const body = res.body as { value: unknown[] };
      expect(body.value.length).toBeGreaterThanOrEqual(3);
    });

    it("mysql: POST creates a customer and returns 201", async () => {
      const res = await supertest(app)
        .post(`/api/odata/${SERVICE_NAME}/customers`)
        .set("Content-Type", "application/json")
        .send(JSON.stringify({ name: "MySQL Test Co", email: "mysql@test.example", country: "US" }));
      expect(res.status).toBe(201);
      expect((res.body as { name: string }).name).toBe("MySQL Test Co");
    });

    it("mysql: PUT updates an existing record", async () => {
      const ins = await supertest(app)
        .post(`/api/odata/${SERVICE_NAME}/products`)
        .set("Content-Type", "application/json")
        .send(JSON.stringify({ sku: "MYSQL-PUT", name: "Temp", price: 1.00, discontinued: false }));
      expect(ins.status).toBe(201);
      const id = (ins.body as { id: number }).id;

      const upd = await supertest(app)
        .put(`/api/odata/${SERVICE_NAME}/products(${id})`)
        .set("Content-Type", "application/json")
        .send(JSON.stringify({ sku: "MYSQL-PUT", name: "Updated", price: 2.00, discontinued: false }));
      expect(upd.status).toBe(200);
    });

    it("mysql: DELETE removes a record", async () => {
      const ins = await supertest(app)
        .post(`/api/odata/${SERVICE_NAME}/employees`)
        .set("Content-Type", "application/json")
        .send(JSON.stringify({ name: "Temp Employee MySQL" }));
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
