import { describe, expect, it } from "vitest";
import { isMatch, matchesAny } from "../../src/core/glob.js";

describe("Glob (spec 03 §2.4)", () => {
  it("matches everything with bare star", () => {
    expect(isMatch("anything", "*")).toBe(true);
    expect(isMatch("", "*")).toBe(true);
  });

  it("is case-insensitive", () => {
    expect(isMatch("CUSTOMERS", "customers")).toBe(true);
    expect(isMatch("customers", "CUST*")).toBe(true);
  });

  it("treats * as any-run and ? as single char", () => {
    expect(isMatch("customers", "cust*")).toBe(true);
    expect(isMatch("customers", "customer?")).toBe(true);
    expect(isMatch("customerss", "customer?")).toBe(false);
    expect(isMatch("orders", "cust*")).toBe(false);
  });

  it("anchors the whole value", () => {
    expect(isMatch("xcustomersx", "customers")).toBe(false);
    expect(isMatch("email_address", "e*")).toBe(true);
  });

  it("escapes regex metacharacters in the literal part", () => {
    expect(isMatch("a.b", "a.b")).toBe(true);
    expect(isMatch("axb", "a.b")).toBe(false);
    expect(isMatch("a+b", "a+b")).toBe(true);
  });

  it("matchesAny checks every pattern", () => {
    expect(matchesAny("ssn", ["email", "ssn"])).toBe(true);
    expect(matchesAny("name", ["email", "ssn"])).toBe(false);
    expect(matchesAny("anything", ["*"])).toBe(true);
  });
});
