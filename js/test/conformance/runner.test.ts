/**
 * Full HTTP conformance suite (spec 13 §3):
 * Testcontainers PostgreSQL + northwind fixture + OData handler + supertest HTTP assertions.
 * Loads all YAML conformance cases from tests/EzOdata.ConformanceTests/cases/*.yaml.
 */

import { PostgreSqlContainer, type StartedPostgreSqlContainer } from "@testcontainers/postgresql";
import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import knex, { type Knex } from "knex";
import yaml from "js-yaml";
import express, { type Application } from "express";
import supertest from "supertest";

import { PostgreSqlIntrospector } from "../../src/connectors/introspectors/postgres-introspector.js";
import { KnexQueryExecutor } from "../../src/connectors/executors/knex-query-executor.js";
import { PostgreSqlDialect } from "../../src/connectors/dialects/postgres.js";
import type { ConnectionSpec } from "../../src/connectors/contracts.js";
import { ODataHandler } from "../../src/odata/handler.js";
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

// ---- JSONPath evaluator (minimal, covers all conformance assertion patterns) ----

interface JsonPathResult {
  found: boolean;
  value?: unknown;
  length?: number;
}

function evaluateJsonPath(root: unknown, path: string): JsonPathResult {
  // Normalize path: remove leading $
  let p = path.startsWith("$") ? path.slice(1) : path;

  let current: unknown = root;
  let found = true;

  while (p.length > 0) {
    // .length() → array length
    if (p === ".length()") {
      if (Array.isArray(current)) {
        return { found: true, value: current.length, length: current.length };
      }
      return { found: false };
    }

    // ['key'] bracket notation
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

    // [N] array index
    const indexMatch = p.match(/^\[(\d+)\](.*)/s);
    if (indexMatch) {
      const idx = parseInt(indexMatch[1]!, 10);
      const rest = indexMatch[2]!;
      if (!Array.isArray(current) || idx >= current.length) return { found: false };
      current = current[idx];
      p = rest;
      continue;
    }

    // .property
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

    // .length() suffix
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

describe(
  "HTTP Conformance Suite (PostgreSQL)",
  { timeout: 300_000 },
  () => {
    let container: StartedPostgreSqlContainer;
    let db: Knex;
    let spec: ConnectionSpec;
    let schema: SchemaSnapshot;
    let app: Application;

    beforeAll(
      async () => {
        container = await new PostgreSqlContainer("postgres:16-alpine").start();

        spec = {
          host: container.getHost(),
          port: container.getMappedPort(5432),
          database: container.getDatabase(),
          username: container.getUsername(),
          password: container.getPassword(),
          tls: { mode: "disable", allowInvalid: false },
          extra: {},
        };

        db = knex({
          client: "pg",
          connection: {
            host: spec.host,
            port: spec.port,
            database: spec.database,
            user: spec.username,
            password: spec.password,
            ssl: false,
          } as Knex.PgConnectionConfig,
          pool: { min: 0, max: 5 },
        });

        // Apply northwind fixture
        const northwindPath = fileURLToPath(
          new URL(
            "../../../tests/EzOdata.ConformanceTests/Fixtures/northwind.sql",
            import.meta.url,
          ),
        );
        await db.raw(readFileSync(northwindPath, "utf8"));

        // Introspect schema with views
        schema = await new PostgreSqlIntrospector().introspect(spec, {
          includeSchemas: [],
          excludeTables: [],
          includeViews: true,
          exposedNameStyle: "original",
        });

        // Build the OData handler
        const dialect = new PostgreSqlDialect();
        const executor: QueryExecutor = new KnexQueryExecutor(dialect, "pg");

        const runtime: ServiceRuntime = {
          name: SERVICE_NAME,
          connectorType: "postgresql",
          connection: spec,
          schema,
          options: DEFAULT_SERVICE_OPTIONS,
          schemaVersion: "1",
          status: "active",
        };

        const handler = new ODataHandler({
          resolveRuntime: async (name) =>
            name === SERVICE_NAME ? runtime : undefined,
          getExecutor: () => executor,
          skipTokenCodec: new SkipTokenCodec(SKIP_TOKEN_KEY),
          policyEngine: new PolicyEngine(),
          resolveRoleRules: async () => [],
          rowFilterParser: () => {
            throw new Error("row filter parser not configured");
          },
        });

        // Build Express app
        app = express();

        // Route: /api/odata/:service/*
        // In Express app.use, req.url is relative to the mount point (e.g., "/orders?$filter=...").
        app.use("/api/odata/:serviceName", async (req, res) => {
          const serviceName = req.params["serviceName"]!;
          // req.url is relative to "/api/odata/:serviceName" mount, e.g. "/orders?$filter=..."
          const relativeUrl = req.url;
          const qsIdx = relativeUrl.indexOf("?");
          const rawPath = qsIdx >= 0 ? relativeUrl.slice(0, qsIdx) : relativeUrl;
          const qs = qsIdx >= 0 ? relativeUrl.slice(qsIdx + 1) : "";
          const path = rawPath.startsWith("/") ? rawPath.slice(1) : rawPath;
          const serviceRoot = `${req.protocol}://${req.get("host")}/api/odata/${serviceName}`;

          const odataReq = {
            method: req.method,
            path,
            queryString: qs,
            serviceRoot,
            identity: DEV_BYPASS_IDENTITY,
            headers: req.headers as Record<string, string>,
          };

          let odataRes;
          try {
            odataRes = await handler.handleForService(serviceName, odataReq);
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
      it(`[${file}] ${testCase.name}`, async () => {
        // C# Split(' ', 2) keeps the rest in [1]; JS split(" ", 2) does NOT.
        // So we split manually at the first space only.
        const firstSpace = testCase.request.indexOf(" ");
        const method = (firstSpace >= 0 ? testCase.request.slice(0, firstSpace) : testCase.request).toUpperCase();
        const rawUrl = firstSpace >= 0 ? testCase.request.slice(firstSpace + 1) : "/";
        // Encode spaces (like C# runner: Replace(" ", "%20")) and single quotes
        let url = rawUrl.replace(/\n/g, "").replace(/ /g, "%20").replace(/'/g, "%27");

        // Service-relative URLs → prefix with /api/odata/sales
        // Absolute paths starting with /api/ pass through untouched
        if (url.startsWith("/") && !url.startsWith("/api/")) {
          url = `/api/odata/${SERVICE_NAME}${url === "/" ? "" : url}`;
        }

        const agent = supertest(app);
        let req: ReturnType<typeof agent.get>;
        if (method === "GET") {
          req = agent.get(url);
        } else {
          req = agent.get(url); // fallback for non-GET
        }

        const res = await req;

        // Get body as string (prefer res.text for XML/plain-text; fall back to JSON)
        const rawBodyStr = res.text && res.text.length > 0
          ? res.text
          : typeof res.body === "string"
            ? res.body
            : JSON.stringify(res.body ?? "");

        const expectedStatus = testCase.expect.status ?? 200;
        expect(res.status, `[${testCase.name}] expected status ${expectedStatus}: ${rawBodyStr.slice(0, 200)}`).toBe(
          expectedStatus,
        );

        // Header assertions
        if (testCase.expect.headers) {
          for (const [headerName, headerValue] of Object.entries(testCase.expect.headers)) {
            const actual = res.headers[headerName.toLowerCase()] as string | undefined;
            expect(
              actual,
              `[${testCase.name}] header ${headerName} = '${actual}'`,
            ).toContain(headerValue);
          }
        }

        // bodyContains
        for (const fragment of testCase.expect.bodyContains ?? []) {
          expect(
            rawBodyStr,
            `[${testCase.name}] body should contain '${fragment}'`,
          ).toContain(fragment);
        }

        // bodyNotContains
        for (const fragment of testCase.expect.bodyNotContains ?? []) {
          expect(
            rawBodyStr,
            `[${testCase.name}] body should NOT contain '${fragment}'`,
          ).not.toContain(fragment);
        }

        // JSONPath assertions
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
  },
);

function assertJsonPath(
  root: unknown,
  path: string,
  expectationRaw: unknown,
  caseName: string,
): void {
  // YAML may give numeric values; coerce to string for comparison logic
  const expectation = String(expectationRaw);
  const { found, value, length } = evaluateJsonPath(root, path);

  if (expectation === "absent") {
    expect(!found, `[${caseName}] ${path} should be absent but was found`).toBe(true);
    return;
  }

  expect(found, `[${caseName}] ${path} not found in ${JSON.stringify(root).slice(0, 300)}`).toBe(
    true,
  );
  if (expectation === "exists") return;

  const actualNumber =
    length ?? (typeof value === "number" ? value : undefined);

  // Comparison expressions: ">= 2", "> 0"
  for (const op of [">=", "<=", ">", "<"] as const) {
    if (expectation.startsWith(op)) {
      const bound = parseFloat(expectation.slice(op.length).trim());
      expect(actualNumber, `[${caseName}] ${path} is not numeric`).toBeDefined();
      const ok =
        op === ">="
          ? (actualNumber ?? 0) >= bound
          : op === "<="
            ? (actualNumber ?? 0) <= bound
            : op === ">"
              ? (actualNumber ?? 0) > bound
              : (actualNumber ?? 0) < bound;
      expect(ok, `[${caseName}] ${path}: expected ${expectation}, got ${actualNumber}`).toBe(true);
      return;
    }
  }

  // Numeric equality
  const numericExpect = parseFloat(expectation);
  if (!isNaN(numericExpect) && expectation.trim() !== "") {
    const actualNum = typeof value === "number" ? value : typeof value === "string" ? parseFloat(value) : Number(value);
    expect(
      actualNum,
      `[${caseName}] ${path}: expected ${numericExpect}, got ${String(value)}`,
    ).toBe(numericExpect);
    return;
  }

  // Boolean
  if (expectation === "true" || expectation === "false") {
    expect(
      value === (expectation === "true"),
      `[${caseName}] ${path}: expected ${expectation}, got ${String(value)}`,
    ).toBe(true);
    return;
  }

  // String equality
  expect(
    String(value ?? ""),
    `[${caseName}] ${path}: expected '${expectation}', got '${String(value)}'`,
  ).toBe(expectation);
}
