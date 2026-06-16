import { describe, expect, it } from "vitest";
import { parseFilter, parseQueryOptions, parsePath, parseExpand, parseApply } from "../../../src/odata/parser.js";
import type { ComparisonNode, FunctionNode, InNode, LambdaNode, LogicalNode, NotNode } from "../../../src/core/query.js";

describe("OData parser — $filter", () => {
  it("simple equality", () => {
    const node = parseFilter("status eq 'open'") as ComparisonNode;
    expect(node.kind).toBe("comparison");
    expect(node.field.path).toEqual(["status"]);
    expect(node.op).toBe("eq");
    expect(node.value.value).toBe("open");
  });

  it("ne comparison", () => {
    const node = parseFilter("status ne 'open'") as ComparisonNode;
    expect(node.kind).toBe("comparison");
    expect(node.op).toBe("ne");
  });

  it("gt with decimal", () => {
    const node = parseFilter("total gt 250.0") as ComparisonNode;
    expect(node.kind).toBe("comparison");
    expect(node.op).toBe("gt");
    expect(node.value.value).toBe(250.0);
  });

  it("null literal", () => {
    const node = parseFilter("ssn eq null") as ComparisonNode;
    expect(node.value.value).toBeNull();
  });

  it("logical AND", () => {
    const node = parseFilter("status eq 'open' and total lt 100") as LogicalNode;
    expect(node.kind).toBe("logical");
    expect(node.op).toBe("and");
    expect(node.operands).toHaveLength(2);
  });

  it("logical OR", () => {
    const node = parseFilter("status eq 'open' or status eq 'shipped'") as LogicalNode;
    expect(node.op).toBe("or");
  });

  it("NOT operator", () => {
    const node = parseFilter("not (status eq 'cancelled')") as NotNode;
    expect(node.kind).toBe("not");
  });

  it("parenthesized grouping", () => {
    const node = parseFilter("(status eq 'open' or status eq 'shipped') and total lt 100") as LogicalNode;
    expect(node.kind).toBe("logical");
    expect(node.op).toBe("and");
    const left = node.operands[0] as LogicalNode;
    expect(left.op).toBe("or");
  });

  it("contains function", () => {
    const node = parseFilter("contains(name,'corp')") as FunctionNode;
    expect(node.kind).toBe("function");
    expect(node.fn).toBe("contains");
    expect(node.args).toHaveLength(2);
    expect(node.op).toBeUndefined();
  });

  it("startswith function", () => {
    const node = parseFilter("startswith(name,'Way')") as FunctionNode;
    expect(node.fn).toBe("startsWith");
  });

  it("endswith function", () => {
    const node = parseFilter("endswith(email,'.example')") as FunctionNode;
    expect(node.fn).toBe("endsWith");
  });

  it("length function with comparison", () => {
    const node = parseFilter("length(country) eq 2") as FunctionNode;
    expect(node.kind).toBe("function");
    expect(node.fn).toBe("length");
    expect(node.op).toBe("eq");
    expect(node.comparand?.value).toBe(2);
  });

  it("year function with comparison", () => {
    const node = parseFilter("year(created_at) eq 2026") as FunctionNode;
    expect(node.fn).toBe("year");
    expect(node.op).toBe("eq");
    expect(node.comparand?.value).toBe(2026);
  });

  it("IN list", () => {
    const node = parseFilter("country in ('DE','JP')") as InNode;
    expect(node.kind).toBe("in");
    expect(node.field.path).toEqual(["country"]);
    expect(node.values).toHaveLength(2);
    expect(node.values[0]?.value).toBe("DE");
    expect(node.values[1]?.value).toBe("JP");
  });

  it("navigation path comparison", () => {
    const node = parseFilter("customer/country eq 'US'") as ComparisonNode;
    expect(node.kind).toBe("comparison");
    expect(node.field.path).toEqual(["customer", "country"]);
    expect(node.op).toBe("eq");
    expect(node.value.value).toBe("US");
  });

  it("any lambda with predicate", () => {
    const node = parseFilter("orders/any(o: o/total gt 500)") as LambdaNode;
    expect(node.kind).toBe("lambda");
    expect(node.navigation).toBe("orders");
    expect(node.lambdaKind).toBe("any");
    expect(node.predicate).toBeDefined();
    const pred = node.predicate as ComparisonNode;
    expect(pred.field.path).toEqual(["total"]);
    expect(pred.op).toBe("gt");
    expect(pred.value.value).toBe(500);
  });

  it("bare any() without predicate", () => {
    const node = parseFilter("orders/any()") as LambdaNode;
    expect(node.kind).toBe("lambda");
    expect(node.navigation).toBe("orders");
    expect(node.predicate).toBeUndefined();
  });

  it("string with single-quote escape", () => {
    const node = parseFilter("name eq 'x'' OR 1=1 --'") as ComparisonNode;
    expect(node.value.value).toBe("x' OR 1=1 --");
  });

  it("throws on malformed filter", () => {
    expect(() => parseFilter("name eq")).toThrow();
    expect(() => parseFilter("")).toThrow();
  });

  it("boolean literals", () => {
    const node = parseFilter("discontinued eq true") as ComparisonNode;
    expect(node.value.value).toBe(true);
  });
});

describe("OData parser — query options", () => {
  it("$top and $skip", () => {
    const q = parseQueryOptions("$top=3&$skip=6");
    expect(q.top).toBe(3);
    expect(q.skip).toBe(6);
  });

  it("$count=true", () => {
    const q = parseQueryOptions("$count=true");
    expect(q.count).toBe(true);
  });

  it("$select parses comma-separated fields", () => {
    const q = parseQueryOptions("$select=id,name");
    expect(q.select).toEqual(["id", "name"]);
  });

  it("$orderby descending", () => {
    const q = parseQueryOptions("$orderby=id desc");
    expect(q.orderBy[0]?.field).toBe("id");
    expect(q.orderBy[0]?.descending).toBe(true);
  });

  it("$skiptoken preserved as raw string", () => {
    const q = parseQueryOptions("$skiptoken=abc123");
    expect(q.skipToken).toBe("abc123");
  });

  it("$filter parsed into IR", () => {
    const q = parseQueryOptions("$filter=status eq 'open'");
    expect(q.filter?.kind).toBe("comparison");
  });
});

describe("OData parser — $expand", () => {
  it("simple expand", () => {
    const expands = parseExpand("customer", 1, 3);
    expect(expands).toHaveLength(1);
    expect(expands[0]?.navigation).toBe("customer");
  });

  it("expand with filter option", () => {
    const expands = parseExpand("orders($filter=status eq 'open')", 1, 3);
    expect(expands[0]?.filter?.kind).toBe("comparison");
  });

  it("expand with top and orderby", () => {
    const expands = parseExpand("orders($top=1;$orderby=total desc)", 1, 3);
    expect(expands[0]?.top).toBe(1);
    expect(expands[0]?.orderBy[0]?.descending).toBe(true);
  });

  it("nested expand", () => {
    const expands = parseExpand("orders($expand=order_items)", 1, 3);
    expect(expands[0]?.expand).toHaveLength(1);
    expect(expands[0]?.expand[0]?.navigation).toBe("order_items");
  });

  it("multiple top-level expands", () => {
    const expands = parseExpand("customer,orders", 1, 3);
    expect(expands).toHaveLength(2);
  });

  it("throws when depth exceeds maxDepth", () => {
    expect(() =>
      parseExpand("orders($expand=order_items($expand=product($expand=customer)))", 1, 3),
    ).toThrow();
  });
});

describe("OData parser — $apply", () => {
  it("groupby with aggregate", () => {
    const { clause } = parseApply("groupby((status), aggregate(total with sum as revenue))");
    expect(clause.groupBy).toEqual(["status"]);
    expect(clause.aggregations).toHaveLength(1);
    expect(clause.aggregations[0]?.op).toBe("sum");
    expect(clause.aggregations[0]?.alias).toBe("revenue");
  });

  it("bare aggregate with $count", () => {
    const { clause } = parseApply("aggregate(total with sum as revenue, $count as n)");
    expect(clause.groupBy).toHaveLength(0);
    expect(clause.aggregations).toHaveLength(2);
    const countAgg = clause.aggregations.find((a) => a.op === "count");
    expect(countAgg?.alias).toBe("n");
  });

  it("filter then groupby", () => {
    const { clause, preFilter } = parseApply(
      "filter(status eq 'open')/groupby((customer_id), aggregate(total with sum as revenue))",
    );
    expect(preFilter).toBeDefined();
    expect(clause.groupBy).toEqual(["customer_id"]);
  });

  it("countdistinct", () => {
    const { clause } = parseApply("aggregate(status with countdistinct as kinds)");
    expect(clause.aggregations[0]?.op).toBe("countDistinct");
  });
});

describe("OData parser — path", () => {
  it("simple collection", () => {
    const p = parsePath("customers");
    expect(p.entitySet).toBe("customers");
    expect(p.key).toBeUndefined();
    expect(p.isCount).toBe(false);
  });

  it("by key (positional)", () => {
    const p = parsePath("customers(1)");
    expect(p.entitySet).toBe("customers");
    expect(p.key?.["__positional__"]).toBe(1);
  });

  it("by key (named)", () => {
    const p = parsePath("order_items(order_id=1,line_no=2)");
    expect(p.key?.["order_id"]).toBe(1);
    expect(p.key?.["line_no"]).toBe(2);
  });

  it("$count segment", () => {
    const p = parsePath("customers/$count");
    expect(p.entitySet).toBe("customers");
    expect(p.isCount).toBe(true);
  });
});
