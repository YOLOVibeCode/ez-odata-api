/**
 * REST filter parser unit tests (port of RestFilterParserTests.cs, spec 06 §5).
 */

import { describe, expect, it } from "vitest";
import { parseFilter, FilterParseError } from "../../../src/core/filter-parser.js";
import type { ComparisonNode, LogicalNode, NotNode, InNode, FunctionNode } from "../../../src/core/query.js";

describe("parseFilter", () => {
  describe("simple comparisons", () => {
    it("parses equality", () => {
      const node = parseFilter("name = 'Alice'") as ComparisonNode;
      expect(node.kind).toBe("comparison");
      expect(node.op).toBe("eq");
      expect(node.field.path).toEqual(["name"]);
      expect(node.value.value).toBe("Alice");
    });

    it("parses != operator", () => {
      const node = parseFilter("status != 'closed'") as ComparisonNode;
      expect(node.op).toBe("ne");
    });

    it("parses <> operator as ne", () => {
      const node = parseFilter("status <> 'closed'") as ComparisonNode;
      expect(node.op).toBe("ne");
    });

    it("parses > operator", () => {
      const node = parseFilter("total > 100") as ComparisonNode;
      expect(node.op).toBe("gt");
      expect(node.value.value).toBe(100);
    });

    it("parses >= operator", () => {
      const node = parseFilter("total >= 0") as ComparisonNode;
      expect(node.op).toBe("ge");
    });

    it("parses < operator", () => {
      const node = parseFilter("total < 50") as ComparisonNode;
      expect(node.op).toBe("lt");
    });

    it("parses <= operator", () => {
      const node = parseFilter("total <= 99.5") as ComparisonNode;
      expect(node.op).toBe("le");
      expect(node.value.value).toBe(99.5);
    });

    it("parses negative number", () => {
      const node = parseFilter("temp > -5") as ComparisonNode;
      expect(node.value.value).toBe(-5);
    });

    it("parses boolean true", () => {
      const node = parseFilter("active = true") as ComparisonNode;
      expect(node.value.value).toBe(true);
    });

    it("parses boolean false", () => {
      const node = parseFilter("active = false") as ComparisonNode;
      expect(node.value.value).toBe(false);
    });

    it("parses null literal", () => {
      const node = parseFilter("email = null") as ComparisonNode;
      expect(node.value.value).toBeNull();
    });

    it("handles escaped single quote in string", () => {
      const node = parseFilter("name = 'O''Brien'") as ComparisonNode;
      expect(node.value.value).toBe("O'Brien");
    });
  });

  describe("is null / is not null", () => {
    it("parses 'is null'", () => {
      const node = parseFilter("email is null") as ComparisonNode;
      expect(node.kind).toBe("comparison");
      expect(node.op).toBe("eq");
      expect(node.value.value).toBeNull();
    });

    it("parses 'is not null'", () => {
      const node = parseFilter("email is not null") as ComparisonNode;
      expect(node.kind).toBe("comparison");
      expect(node.op).toBe("ne");
      expect(node.value.value).toBeNull();
    });
  });

  describe("in operator", () => {
    it("parses in with multiple values", () => {
      const node = parseFilter("status in ('open','pending','closed')") as InNode;
      expect(node.kind).toBe("in");
      expect(node.field.path).toEqual(["status"]);
      expect(node.values).toHaveLength(3);
      expect(node.values[0]!.value).toBe("open");
      expect(node.values[2]!.value).toBe("closed");
    });

    it("parses in with numbers", () => {
      const node = parseFilter("id in (1,2,3)") as InNode;
      expect(node.values.map((v) => v.value)).toEqual([1, 2, 3]);
    });
  });

  describe("function operators", () => {
    it("parses 'contains' keyword", () => {
      const node = parseFilter("name contains 'Alice'") as FunctionNode;
      expect(node.kind).toBe("function");
      expect(node.fn).toBe("contains");
    });

    it("parses 'starts with'", () => {
      const node = parseFilter("name starts with 'Ali'") as FunctionNode;
      expect(node.kind).toBe("function");
      expect(node.fn).toBe("startsWith");
    });

    it("parses 'ends with'", () => {
      const node = parseFilter("name ends with 'ice'") as FunctionNode;
      expect(node.kind).toBe("function");
      expect(node.fn).toBe("endsWith");
    });

    it("parses 'like' stripping wildcards", () => {
      const node = parseFilter("name like '%Ali%'") as FunctionNode;
      expect(node.kind).toBe("function");
      expect(node.fn).toBe("contains");
      const constArg = node.args.find((a) => a.kind === "constant");
      expect((constArg as { kind: "constant"; value: { value: unknown } })?.value?.value).toBe("Ali");
    });
  });

  describe("logical operators", () => {
    it("parses 'and'", () => {
      const node = parseFilter("status = 'open' and total > 100") as LogicalNode;
      expect(node.kind).toBe("logical");
      expect(node.op).toBe("and");
      expect(node.operands).toHaveLength(2);
    });

    it("parses 'or'", () => {
      const node = parseFilter("status = 'open' or status = 'pending'") as LogicalNode;
      expect(node.kind).toBe("logical");
      expect(node.op).toBe("or");
    });

    it("parses 'not'", () => {
      const node = parseFilter("not status = 'closed'") as NotNode;
      expect(node.kind).toBe("not");
    });

    it("chains multiple ands", () => {
      const node = parseFilter("a = 1 and b = 2 and c = 3") as LogicalNode;
      expect(node.kind).toBe("logical");
      expect(node.op).toBe("and");
    });
  });

  describe("parentheses", () => {
    it("groups with parens", () => {
      const node = parseFilter("(status = 'open') and (total > 100)") as LogicalNode;
      expect(node.kind).toBe("logical");
      expect(node.op).toBe("and");
    });

    it("nested parens", () => {
      const node = parseFilter("(a = 1 or b = 2) and c = 3") as LogicalNode;
      expect(node.kind).toBe("logical");
      expect(node.op).toBe("and");
      expect((node.operands[0] as LogicalNode).op).toBe("or");
    });
  });

  describe("navigation paths", () => {
    it("parses depth-1 navigation", () => {
      const node = parseFilter("customer.country = 'US'") as ComparisonNode;
      expect(node.field.path).toEqual(["customer", "country"]);
    });

    it("rejects depth > 2 navigation", () => {
      expect(() => parseFilter("a.b.c = 1")).toThrow(FilterParseError);
    });
  });

  describe("error cases", () => {
    it("throws FilterParseError for unknown operator", () => {
      expect(() => parseFilter("name ? 'x'")).toThrow(FilterParseError);
    });

    it("throws FilterParseError for unexpected character", () => {
      expect(() => parseFilter("name = @x")).toThrow(FilterParseError);
    });

    it("throws FilterParseError for incomplete expression", () => {
      expect(() => parseFilter("name = ")).toThrow(FilterParseError);
    });
  });
});
