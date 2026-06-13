# 14 — Delivery Plan & Roadmap

## 1. v1 Delivery Milestones

Milestones are vertical slices, each ending in a demo-able state. Estimates assume a team of 2–3 engineers; treat as sequencing, not commitments.

### M0 — Walking skeleton (2–3 wks)
Host project, system DB + migrations, setup flow, JWT login, services CRUD (no introspection), health endpoints, CI pipeline, Docker image.
**Demo:** create admin, log in, create a (non-functional) service via API.

### M1 — Introspection + read-only OData on PostgreSQL (4–5 wks)
PostgreSQL connector, snapshot model + persistence, dynamic EDM factory, OData GET pipeline: service doc, `$metadata`, `$filter/$select/$orderby/$top/$skip/$count`, paging with skiptokens. Conformance suite bootstrapped.
**Demo:** J1 journey end-to-end on Postgres (read-only), Excel loads a feed.

### M2 — Security core (3–4 wks)
API keys + apps, role engine (rules, field policies, row filters), identity-trimmed metadata, audit pipeline, rate limiting (in-memory).
**Demo:** J2 journey; security leak matrix green.

### M3 — Writes + batch (3 wks)
POST/PATCH/PUT/DELETE, bulk, deep insert, `$batch`, ETag concurrency, error taxonomy.
**Demo:** full CRUD from OData .NET client with conflict handling.

### M4 — Engine breadth (4 wks, parallelizable)
MySQL, SQL Server, SQLite connectors through the full conformance matrix; `$expand` (JOIN + batched children); `$apply` subset.
**Demo:** same conformance YAML green on 4 engines.

### M5 — REST dialect + docs (2–3 wks)
REST endpoints over the IR, OpenAPI generation (both dialects), docs explorer with try-it.
**Demo:** J3-style consumer onboarding without OData knowledge.

### M6 — MCP server (2–3 wks)
Tool catalog, per-identity trimming, structured results, writes gating, client config helpers in UI.
**Demo:** J4 journey in Claude Desktop and Cursor.

### M7 — Admin console completion (3–4 wks, overlaps M3–M6)
All screens of doc 10; role simulator; dashboard; audit browser.
**Demo:** UI-parity checklist complete; non-API-literate admin completes J1+J2.

### M8 — Hardening & release (3 wks)
Perf budgets met, security suite + ZAP clean, Redis multi-replica mode, Helm chart, backup/restore drill, docs site, v1.0.0.

### M9 — Embedded NuGet packages (3 wks, post-v1.0)
`EzOdata.AspNetCore.*` package family per doc 15: fluent host API, in-memory/file metadata store, host-auth bridge, embedded-host conformance fixture, NuGet publishing pipeline. **Prerequisite work lands earlier:** the `IMetadataStore` / `IPolicySource` / `IAuditSink` abstractions (doc 15 §4) are built into M1–M2 so the engine is host-agnostic from the start.
**Demo:** `dotnet add package` into an existing webapp → working `/odata` endpoint in under 10 lines.

Total: ~6 months calendar to v1.0 with overlap; +1 month to the embedded package GA.

## 2. Post-v1 Roadmap (priority order)

| # | Feature | Notes |
|---|---------|-------|
| R1 | **Stored procedures & functions** | DreamFactory parity headline; expose as OData actions/functions + MCP tools; per-proc RBAC |
| R2 | **OIDC SSO login** (admin + named users) | Generic OIDC first; SAML later |
| R3 | **Scripting hooks** | Pre/post-process event scripts; sandboxed JS (V8 isolates / Jint) first; queue-backed webhooks on data events |
| R4 | **MongoDB connector** | First NoSQL: maps collections → open entity types; filter translation to Mongo query AST |
| R5 | **Schema management API** | Create/alter tables via API + UI (DreamFactory `_schema` write parity) |
| R6 | **JSON-path filtering** | `$filter` into `jsonb`/JSON columns |
| R7 | **Delta links / change tracking** | Engine-specific CDC (PG logical decoding, SQL Server CT) |
| R8 | **File services** | S3/Azure Blob/local as REST resources |
| R9 | **Multi-tenancy** | Isolated admin domains on one instance |
| R10 | **Hash-chained audit + SIEM export** | Tamper-evidence; OCSF/CEF exporters |
| R11 | **Snowflake / Databricks connectors** | Analytics engines; read-mostly profiles |
| R12 | **stdio MCP bridge + npx installer** | `npx create-ez-odata` parity with DreamFactory's installer |
| R13 | **Caching layer for hot queries** | Per-service TTL cache with table-write invalidation |
| R14 | **OData 4.01 full + ASP.NET Core OData 10** | DateOnly/TimeOnly natives; track GA |

## 3. Explicit Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| We own the OData serving layer (ODL-direct, no `Microsoft.AspNetCore.OData`) to gain net48 hosting; writer/reader edge cases are ours to get right | Conformance suite from M1 is the safety net; ODL 7.x (`ODataMessageWriter`, `ODataUriParser`, `CsdlWriter`) still does the heavy protocol lifting; surface kept to the doc 05 subset |
| ODL 7.x and Npgsql 8.x are maintenance-mode lines (the price of net48 support) | Both are explicitly supported by their vendors for .NET Framework consumers; engine isolates them behind `Connectors.Abstractions`/the OData engine so future ODL8/Npgsql10 dual-targeting is a TFM addition, not a redesign |
| SQL dialect drift (esp. SQLite limitations) | Dialect matrix in conformance tests is the contract; document per-engine capability flags in `$metadata` annotations |
| RBAC complexity → footguns | Simulator + policy-tests-as-data + deny-by-default + UI teaching empty states |
| Perf of expand stitching on large pages | Budgeted k6 scenario from M4; keyset pagination default |
| Scope creep toward DreamFactory full parity | This spec's Non-Goals list is the contract; roadmap items require their own spec docs |
