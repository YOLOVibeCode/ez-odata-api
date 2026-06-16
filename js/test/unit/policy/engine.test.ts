import { describe, expect, it } from "vitest";
import { ErrorCodes, RowFilterError } from "../../../src/core/errors.js";
import { comparison, constant, fieldRef, type FilterNode, type LogicalNode } from "../../../src/core/query.js";
import type { RowFilterParser } from "../../../src/core/policy/contracts.js";
import { PolicyEngine } from "../../../src/core/policy/engine.js";
import {
  accessRule,
  ANONYMOUS_IDENTITY,
  getClaim,
  Verb,
  type AccessRule,
  type RequestIdentity,
  type RoleRuleSet,
} from "../../../src/core/policy/model.js";

/** Normative tests for spec 08 §4-5 evaluation semantics (port of PolicyEngineTests.cs). */
describe("PolicyEngine (spec 08 §4-5)", () => {
  const customerColumns = ["id", "name", "email", "country", "ssn"];
  const engine = new PolicyEngine();

  /** Hand-rolled parser fake (spec 13 §0.3): claims resolved like the real one. */
  const parserFor =
    (identity: RequestIdentity): RowFilterParser =>
    (_table, rowFilter) => {
      if (rowFilter.includes("@identity.")) {
        const claim = rowFilter.split("@identity.")[1]!.trim();
        const value = getClaim(identity, claim);
        if (value === undefined) {
          throw new RowFilterError(`Claim '${claim}' not present.`);
        }
        return comparison(fieldRef("owner_id"), "eq", constant(value));
      }
      return comparison(fieldRef("country"), "eq", constant(rowFilter));
    };

  const role = (name: string, ...rules: AccessRule[]): RoleRuleSet => ({
    roleId: name.length,
    roleName: name,
    bypassDataRules: false,
    rules,
  });

  const authorize = (verb: Verb, ...roles: RoleRuleSet[]) =>
    engine.authorize(ANONYMOUS_IDENTITY, roles, "sales", "customers", verb, customerColumns, parserFor(ANONYMOUS_IDENTITY));

  // ---- §5.1 deny by default ----

  it("hides the resource as 404 when no rule matches", () => {
    const decision = authorize(Verb.Get, role("r", accessRule({ resourcePattern: "orders", verbs: Verb.All })));
    expect(decision.allowed).toBe(false);
    expect(decision.hidden).toBe(true);
  });

  it("hides everything when there are no roles at all", () => {
    const decision = authorize(Verb.Get);
    expect(decision.allowed).toBe(false);
    expect(decision.hidden).toBe(true);
  });

  // ---- §4 verb check ----

  it("returns 403 (not 404) for a matching rule without the verb", () => {
    const decision = authorize(Verb.Delete, role("ro", accessRule({ resourcePattern: "*", verbs: Verb.Get })));
    expect(decision.allowed).toBe(false);
    expect(decision.hidden).toBe(false);
    expect(decision.denialCode).toBe(ErrorCodes.ForbiddenVerb);
  });

  // ---- §5.2 priority & effect ----

  it("lets a higher-priority deny carve an exception out of a broad allow", () => {
    const r = role(
      "r",
      accessRule({ resourcePattern: "*", verbs: Verb.All, priority: 0 }),
      accessRule({ resourcePattern: "customers", effect: "deny", priority: 100 }),
    );
    const decision = authorize(Verb.Get, r);
    expect(decision.allowed).toBe(false);
    expect(decision.hidden).toBe(false); // matched, so 403 not 404
  });

  it("lets deny win on an equal-priority tie", () => {
    const r = role(
      "r",
      accessRule({ resourcePattern: "customers", verbs: Verb.All, priority: 5 }),
      accessRule({ resourcePattern: "customer?", effect: "deny", priority: 5 }),
    );
    expect(authorize(Verb.Get, r).allowed).toBe(false);
  });

  it("lets a higher-priority allow override a deny", () => {
    const r = role(
      "r",
      accessRule({ resourcePattern: "*", effect: "deny", priority: 0 }),
      accessRule({ resourcePattern: "customers", verbs: Verb.Get, priority: 10 }),
    );
    expect(authorize(Verb.Get, r).allowed).toBe(true);
  });

  // ---- §5.3 wildcards ----

  it.each(["*", "cust*", "CUSTOMERS"])("globs resource patterns case-insensitively: %s", (pattern) => {
    expect(authorize(Verb.Get, role("r", accessRule({ resourcePattern: pattern, verbs: Verb.Get }))).allowed).toBe(true);
  });

  // ---- field policies ----

  it("expands field-policy globs and unions them across rules", () => {
    const r = role(
      "r",
      accessRule({ resourcePattern: "*", verbs: Verb.Get, fieldRules: [{ pattern: "ssn", action: "deny" }] }),
      accessRule({
        resourcePattern: "customers",
        verbs: Verb.Get,
        priority: 1,
        fieldRules: [{ pattern: "e*", action: "mask", maskValue: "***@***" }],
      }),
    );
    const decision = authorize(Verb.Get, r);
    expect(decision.allowed).toBe(true);
    expect(decision.deniedFields.has("ssn")).toBe(true);
    expect(decision.maskedFields.get("email")).toBe("***@***");
    expect(decision.deniedFields.has("name")).toBe(false);
  });

  // ---- §5.4 row filters ----

  it("ANDs row filters within a role", () => {
    const r = role(
      "r",
      accessRule({ resourcePattern: "*", verbs: Verb.Get, rowFilter: "US" }),
      accessRule({ resourcePattern: "customers", verbs: Verb.Get, rowFilter: "DE" }),
    );
    const decision = authorize(Verb.Get, r);
    expect(decision.rowFilter?.kind).toBe("logical");
    expect((decision.rowFilter as LogicalNode).op).toBe("and");
  });

  it("fails closed when an identity claim is missing", () => {
    const r = role("r", accessRule({ resourcePattern: "*", verbs: Verb.Get, rowFilter: "owner eq @identity.userId" }));
    expect(authorize(Verb.Get, r).allowed).toBe(false);
  });

  it("resolves a present identity claim into the filter", () => {
    const identity: RequestIdentity = { ...ANONYMOUS_IDENTITY, claims: { userId: "42" } };
    const r = role("r", accessRule({ resourcePattern: "*", verbs: Verb.Get, rowFilter: "owner eq @identity.userId" }));
    const decision = engine.authorize(identity, [r], "sales", "customers", Verb.Get, customerColumns, parserFor(identity));
    expect(decision.allowed).toBe(true);
    const cmp = decision.rowFilter as Extract<FilterNode, { kind: "comparison" }>;
    expect(cmp.kind).toBe("comparison");
    expect(cmp.value.value).toBe("42");
  });

  // ---- §5.6 multiple roles ----

  it("grants access when any role allows it", () => {
    const denying = role("a", accessRule({ resourcePattern: "customers", effect: "deny" }));
    const allowing = role("b", accessRule({ resourcePattern: "customers", verbs: Verb.Get }));
    expect(authorize(Verb.Get, denying, allowing).allowed).toBe(true);
  });

  it("keeps a field restriction only if every allowing role imposes it", () => {
    const restrictive = role(
      "a",
      accessRule({ resourcePattern: "*", verbs: Verb.Get, fieldRules: [{ pattern: "ssn", action: "deny" }] }),
    );
    const permissive = role("b", accessRule({ resourcePattern: "*", verbs: Verb.Get }));

    expect(authorize(Verb.Get, restrictive, permissive).deniedFields.size).toBe(0);

    const bothRestrict = authorize(
      Verb.Get,
      restrictive,
      role("c", accessRule({ resourcePattern: "*", verbs: Verb.Get, fieldRules: [{ pattern: "ssn", action: "deny" }] })),
    );
    expect(bothRestrict.deniedFields.has("ssn")).toBe(true);
  });

  it("ORs row filters across roles and lets an unfiltered role win", () => {
    const filteredA = role("a", accessRule({ resourcePattern: "*", verbs: Verb.Get, rowFilter: "US" }));
    const filteredB = role("b", accessRule({ resourcePattern: "*", verbs: Verb.Get, rowFilter: "DE" }));
    const unfiltered = role("c", accessRule({ resourcePattern: "*", verbs: Verb.Get }));

    const or = authorize(Verb.Get, filteredA, filteredB);
    expect(or.rowFilter?.kind).toBe("logical");
    expect((or.rowFilter as LogicalNode).op).toBe("or");

    expect(authorize(Verb.Get, filteredA, unfiltered).rowFilter).toBeUndefined();
  });

  // ---- §5.7 bypass ----

  it("short-circuits a bypass-data-rules role with the bypass flag", () => {
    const bypass: RoleRuleSet = { roleId: 1, roleName: "admin", bypassDataRules: true, rules: [] };
    const decision = authorize(Verb.Delete, bypass);
    expect(decision.allowed).toBe(true);
    expect(decision.bypass).toBe(true);
  });
});
