/**
 * Express integration smoke test — mounts the router with a SQLite in-memory
 * service and verifies the key endpoints respond correctly.
 */

import { describe, expect, it, beforeAll } from "vitest";
import express from "express";
import request from "supertest";
import { ezodata } from "../../../src/express/index.js";

let app: ReturnType<typeof express>;

beforeAll(async () => {
  process.env["NODE_ENV"] = "test";

  const instance = await ezodata({
    services: [
      {
        name: "test",
        connector: "sqlite",
        connection: { filePath: ":memory:" },
      },
    ],
    roles: [],
    auth: { mode: "none" },
  });

  app = express();
  app.use(express.json());
  app.use("/api", instance.router);
}, 30000);

describe("Express integration smoke tests", () => {
  it("GET /api/odata/test/$metadata returns 200 XML", async () => {
    const resp = await request(app).get("/api/odata/test/$metadata");
    expect(resp.status).toBe(200);
    expect(resp.headers["content-type"]).toMatch(/xml/);
    expect(resp.text).toContain("edmx");
  });

  it("GET /api/odata/test returns 200 service document", async () => {
    const resp = await request(app).get("/api/odata/test");
    expect(resp.status).toBe(200);
    const body = JSON.parse(resp.text);
    expect(body["@odata.context"]).toContain("$metadata");
  });

  it("GET /api/rest/test/_table returns 200 table list", async () => {
    const resp = await request(app).get("/api/rest/test/_table");
    expect(resp.status).toBe(200);
    const body = JSON.parse(resp.text);
    expect(Array.isArray(body.resource)).toBe(true);
  });

  it("POST /api/mcp with initialize returns protocol version", async () => {
    const resp = await request(app)
      .post("/api/mcp")
      .send({ jsonrpc: "2.0", id: 1, method: "initialize" });
    expect(resp.status).toBe(200);
    const body = resp.body as Record<string, unknown>;
    expect((body["result"] as Record<string, unknown>)["protocolVersion"]).toBeDefined();
  });

  it("GET /api/openapi/test returns 200 JSON OpenAPI document", async () => {
    const resp = await request(app).get("/api/openapi/test");
    expect(resp.status).toBe(200);
    const body = JSON.parse(resp.text);
    expect(body.openapi).toBe("3.1.0");
  });

  it("GET /api/odata/unknown returns 404", async () => {
    const resp = await request(app).get("/api/odata/unknown/customers");
    expect(resp.status).toBe(404);
  });
});
