import { describe, expect, it } from "vitest";
import {
  buildCollectionPayload,
  buildSinglePayload,
  buildErrorPayload,
  buildServiceDocument,
} from "../../../src/odata/payload.js";
import { Row } from "../../../src/core/result.js";

function makeRow(fields: Record<string, unknown>): Row {
  const row = new Row();
  for (const [k, v] of Object.entries(fields)) row.set(k, v);
  return row;
}

describe("buildCollectionPayload", () => {
  it("wraps rows in value array with context", () => {
    const rows = [makeRow({ id: 1, name: "Acme" })];
    const payload = buildCollectionPayload("http://localhost/odata/sales", "customers", rows);
    expect(payload["@odata.context"]).toContain("$metadata#customers");
    expect(payload.value).toHaveLength(1);
    expect(payload.value[0]?.["id"]).toBe(1);
  });

  it("includes @odata.count when provided", () => {
    const payload = buildCollectionPayload("http://localhost/odata/sales", "customers", [], { count: 42 });
    expect(payload["@odata.count"]).toBe(42);
  });

  it("includes @odata.nextLink when provided", () => {
    const payload = buildCollectionPayload("http://localhost/odata/sales", "customers", [], {
      nextLink: "http://localhost/odata/sales/customers?$skiptoken=abc",
    });
    expect(payload["@odata.nextLink"]).toBe("http://localhost/odata/sales/customers?$skiptoken=abc");
  });

  it("omits @odata.count and @odata.nextLink when not provided", () => {
    const payload = buildCollectionPayload("http://localhost/odata/sales", "customers", []);
    expect("@odata.count" in payload).toBe(false);
    expect("@odata.nextLink" in payload).toBe(false);
  });
});

describe("buildSinglePayload", () => {
  it("flattens row into root object with context", () => {
    const row = makeRow({ id: 2, name: "Globex" });
    const payload = buildSinglePayload("http://localhost/odata/sales", "customers", row);
    expect(payload["@odata.context"]).toContain("$metadata");
    expect(payload["id"]).toBe(2);
    expect(payload["name"]).toBe("Globex");
  });

  it("handles nested Row for expanded to-one", () => {
    const parent = makeRow({ id: 1, status: "open" });
    const childRow = makeRow({ name: "Acme Corp" });
    parent.set("customer", childRow);
    const payload = buildSinglePayload("http://localhost/odata/sales", "orders", parent);
    expect((payload["customer"] as Record<string, unknown>)["name"]).toBe("Acme Corp");
  });

  it("handles array of Rows for expanded to-many", () => {
    const parent = makeRow({ id: 1, name: "Acme" });
    const orders = [makeRow({ id: 1, total: 100 }), makeRow({ id: 2, total: 200 })];
    parent.set("orders", orders);
    const payload = buildSinglePayload("http://localhost/odata/sales", "customers", parent);
    const payloadOrders = payload["orders"] as Record<string, unknown>[];
    expect(payloadOrders).toHaveLength(2);
    expect(payloadOrders[0]?.["total"]).toBe(100);
  });
});

describe("buildErrorPayload", () => {
  it("wraps code and message in OData error format", () => {
    const err = buildErrorPayload("Validation.BadFilter", "Invalid filter") as { error: { code: string; message: string } };
    expect(err.error.code).toBe("Validation.BadFilter");
    expect(err.error.message).toBe("Invalid filter");
  });
});

describe("buildServiceDocument", () => {
  it("lists entity sets with name and url", () => {
    const doc = buildServiceDocument("http://localhost/odata/sales", ["customers", "orders"]);
    const value = doc["value"] as Array<{ name: string; url: string }>;
    expect(value).toHaveLength(2);
    expect(value[0]?.name).toBe("customers");
    expect(value[0]?.url).toBe("customers");
  });
});
