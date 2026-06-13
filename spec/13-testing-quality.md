# 13 — Testing & Quality Strategy

## 0. TDD Workflow (normative)

Test-Driven Development is the required workflow for all production code in `Core`, `OData`, `Rest`, `Mcp`, `Admin`, and `Connectors.*`. It is not aspirational; it is enforced in review and tooling (§0.5).

### 0.1 The loop

Red → Green → Refactor, at the smallest meaningful behavior increment:

1. **Red** — write a failing test that states the next behavior in domain terms (a spec requirement, a grammar production, a policy rule outcome). Run it; watch it fail for the *expected reason*.
2. **Green** — write the minimum production code to pass. Resist generalizing beyond the test.
3. **Refactor** — improve names/structure of both production and test code with the bar green. This is where ISP violations get fixed: if the test needed a mock with unused members, segregate the interface now (doc 02 §3.1).

Commits should show the rhythm: test-and-implementation arrive together; a PR whose tests were all clearly written after the fact (e.g. mirrors of the implementation's internals) gets bounced in review.

### 0.2 Spec-as-failing-tests

This specification's requirement tables are the backlog of red tests:

- **Conformance-first:** every doc 05/06 behavior lands as a failing YAML conformance case (§3) *before* the feature is implemented. M1's first deliverable is the conformance harness plus a wall of red cases — the burn-down to green *is* the milestone progress metric.
- **Policy-first:** RBAC behaviors (doc 08 §4–5) land as policy-case JSON fixtures (§5) before the policy engine code that satisfies them.
- **Dialect-first:** each row of the doc 04 §7.2 translation table is a unit test asserting compiled SQL per engine before the dialect method exists.
- **Bug = test:** every defect fix starts with a failing regression test reproducing it; the test references the issue ID.

### 0.3 Test design rules

- Tests assert **observable behavior** (HTTP responses, compiled SQL, IR shapes, policy verdicts), never private implementation details; refactoring must not break tests.
- Prefer **hand-rolled fakes** of the segregated role interfaces (`IQueryExecutor`, `IPolicySource`, `IAuditSink`, …) over mocking frameworks; a mock may only configure members the test asserts. A test that needs to stub more than ~3 members of one dependency is treated as an ISP smell and triggers interface redesign, not a bigger mock.
- One behavior per test; name = behavior sentence (`Filter_on_denied_field_returns_403_FieldDenied`).
- Property-based tests (FsCheck) are the default for parsers, the SQL compiler, cursors, and type coercion — example-based tests alone are insufficient there.

### 0.4 Where TDD applies (and where it doesn't)

| Code | Discipline |
|------|-----------|
| Domain logic, parsers, compiler, policy engine, EDM factory, protocol handlers | Strict test-first |
| Connectors (dialect logic) | Test-first against captured-SQL unit tests; engine integration tests may follow implementation within the same PR |
| Thin DI/composition glue, EF migrations, generated code | Exempt from test-first; covered by integration tests |
| Admin UI components | Test-with (component tests alongside), Playwright E2E after; strict TDD not required for styling/layout |

### 0.5 Enforcement

- PR template requires linking each change to its tests; reviewers check the test-first evidence (test quality, failure-reason specificity), not just coverage numbers.
- CI fails a PR that adds/changes public behavior in TDD-scoped projects with no corresponding test delta (diff-coverage gate ≥ 90% on changed lines in those projects).
- The mutation-testing floor (§8) exists to catch assertion-free "TDD theater" tests.

## 1. Test Pyramid

| Layer | Framework | Scope | Gate |
|-------|-----------|-------|------|
| Unit | xUnit v3 + FsCheck (property tests) | Filter parsers, SQL compiler per dialect, policy engine, EDM factory, cursors, type coercion | PR, < 2 min |
| Integration | xUnit + Testcontainers (postgres, mysql, mssql; sqlite in-proc) + `WebApplicationFactory` | Full HTTP stack against real databases | PR, < 15 min |
| Conformance | Custom OData conformance suite (§3) | OData behavior matrix per engine | PR (postgres) / nightly (all engines) |
| Security | Dedicated suite (doc 08 §11) + ZAP baseline scan | Injection corpus, RBAC leak tests, authn edge cases | PR + nightly |
| Performance | k6 + BenchmarkDotNet micro-benches | NFR-1/2 latency + throughput budgets | Nightly, trend-tracked |
| E2E UI | Playwright | Setup wizard, service creation, role builder, key issuance, docs try-it | PR (smoke) / nightly (full) |
| Client compatibility | Scripted (§6) | Excel/Power BI (manual checklist), OData .NET client, JS clients, codegen tools | Release |

## 2. Reference Fixture Databases

A versioned fixture per engine, created by migration scripts in `tests/fixtures/`:

1. **northwind-ish** core: customers/orders/order_items/products with FKs, composite-key table, keyless view, self-referencing employees, every supported column type for the engine (the "type zoo" table), enum column, JSON column, generated/identity columns, table with no PK, snake_case + CamelCase mixed names, a table with 1M rows (perf profile only).
2. Edge-case schema: reserved-word table/column names (`order`, `select`, `group`), unicode identifiers, max-length identifiers, cyclic FKs, multi-column unique constraints.

The same fixture drives integration, conformance, performance, and the demo compose profile — one canonical dataset everywhere.

## 3. OData Conformance Suite

A table-driven matrix (`tests/EzOdata.ConformanceTests/cases/*.yaml`) of request → expected response assertions, executed against each engine:

```yaml
- name: filter-contains-paged
  request: "GET /customers?$filter=contains(name,'a')&$top=10&$count=true"
  expect:
    status: 200
    jsonpath:
      "$.value.length()": 10
      "$['@odata.count']": ">= 10"
    invariant: sql_parameterized          # asserts captured SQL used parameters
```

Coverage targets every row of doc 05 (§4 grammar items, paging, expand variants, $batch, ETags, error codes) and every dialect translation row of doc 04 §7.2. Failures print the compiled SQL for the engine — the suite doubles as a dialect regression net.

Additionally: run the OASIS OData v4 ABNF over our accepted/rejected URL corpus to confirm grammar conformance claims (Minimal + Intermediate checklists tracked as YAML with status per item).

## 4. SQL Safety Invariants (continuous)

- A test-only `SqlCaptureInterceptor` records every command; CI asserts: zero non-parameterized literals derived from request input (taint-check via marker values like `'EZTAINT_7f3a'` injected into every test request's strings — if a marker ever appears in SQL text rather than a parameter, the build fails).
- Fuzzing: nightly job feeds grammar-aware fuzzed `$filter`/REST `filter`/MCP args (SharpFuzz harness on the parsers) — crashes or taint leaks fail.

## 5. Policy Tests as Data

Role simulator cases (doc 07 §5) exported as JSON are executable fixtures: `tests/policy-cases/*.json` run against the real policy engine. The security leak matrix of doc 08 §11 item 2 is generated combinatorially (field policy × every read path) — ~400 generated cases.

## 6. Client Compatibility (release checklist)

| Client | Automated? | Cases |
|--------|-----------|-------|
| `Microsoft.OData.Client` | yes (CI) | metadata load, LINQ filter/expand/top, CUD, batch |
| `odata-query` / `o.js` (JS) | yes (CI) | query building round-trips |
| Kiota / openapi-generator / odata2ts codegen | yes (CI smoke) | generate client from exported docs, compile, one call |
| Excel + Power BI OData feed | manual checklist | connect, list, load, filter folding, refresh |
| Claude Desktop / Cursor MCP | manual checklist + automated MCP client SDK tests | tools list trimming, query, denial behavior, list_changed |
| SAP Cloud SDK | manual, release-only | basic reads |

## 7. Performance Budgets (k6, nightly trend)

| Scenario | Budget |
|----------|--------|
| `GET /customers(42)` warm, p50 / p95 | ≤ 15 ms / ≤ 40 ms added overhead vs raw query (NFR-1) |
| 1k req/s mixed reads, 4 vCPU, 30 min soak | 0 errors, p95 < 120 ms (NFR-2) |
| `$expand` two levels, 25 parents × 5 children | ≤ 3 SQL statements, p95 < 150 ms |
| Bulk insert 10k rows (batches of 500) | < 30 s, linear memory |
| Schema refresh 500-table DB | ≤ 60 s, zero failed in-flight requests (NFR-4) |
| Audit pipeline at 5k events/s | zero data-path latency impact > 1 ms, drops < 0.1% |

Regression rule: > 10% degradation vs 7-day baseline fails the nightly and pings the channel.

## 8. Static Quality Gates

- `TreatWarningsAsErrors`, nullable reference types enabled everywhere.
- Analyzers: .NET analyzers (latest-all), SecurityCodeScan, banned-API list (no `string.Format` into SQL APIs, no `DateTime.Now`, no raw `DbCommand.CommandText +=`).
- Coverage gate: line ≥ 80% on `Core`, `OData`, `Connectors.*` plus diff-coverage ≥ 90% on changed lines (§0.5); mutation testing (Stryker.NET) score ≥ 60% on the policy engine and SQL compiler (the two highest-risk components).
- ISP guardrails: an architecture-test suite (NetArchTest) asserts the dependency rules of doc 02 §3 and interface hygiene — protocol read paths must not reference `IWriteExecutor`; no interface in `Core`/`Connectors.Abstractions` may exceed 5 members without an ADR; `NotSupportedException` is on the banned-API list for interface implementations.
- UI: ESLint + typescript strict + Playwright a11y assertions (axe) per route.
- Spec conformance traceability: every requirement ID in these documents (OD-*, SEC-*, …) must be referenced by ≥ 1 test attribute `[Requirement("OD-4")]`; an inventory test fails on orphans.

## 9. CI/CD Pipeline (GitHub Actions)

1. **PR**: build + unit + integration (postgres, sqlite) + conformance (postgres) + UI smoke + lint/analyzers + coverage gate. ~20 min.
2. **main nightly**: full engine matrix (mysql, mssql added), security suite + ZAP, fuzzers (30 min cap), perf trend, mutation testing (weekly).
3. **Release tag**: full matrix + client compatibility automation + image build (multi-arch, SBOM, sign) + Helm chart lint/publish + compose smoke (`docker compose up` → setup → create service → query) on amd64 and arm64 runners.
