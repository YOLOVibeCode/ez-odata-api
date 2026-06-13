# 01 — Product Overview

## 1. Product Name and One-Line Definition

**ez-odata-api** is a self-hosted, governed data-access platform that connects to existing SQL databases, introspects their schemas, and instantly exposes them as fully documented **OData v4** and **REST/JSON** APIs — plus a built-in **MCP server** so AI assistants can query the same data under the same security model.

It is directly inspired by [DreamFactory](https://github.com/dreamfactorysoftware/dreamfactory), re-imagined on a modern .NET stack with OData v4 as the primary protocol.

## 2. Problem Statement

Organizations hold most of their operational data in relational databases, but:

1. **Every new consumer requires backend work.** Internal tools, mobile apps, BI integrations, and partner integrations each demand hand-written CRUD endpoints, which are repetitive, error-prone, and expensive to maintain.
2. **AI/LLM access to data is ungoverned.** Teams either paste data into prompts, let agents generate raw SQL (unpredictable, unsafe), or build one-off integrations with no central access control or audit trail.
3. **Standards adoption is hard.** OData v4 gives consumers a rich, standardized query language (`$filter`, `$expand`, `$select`, `$orderby`, `$batch`…) and out-of-the-box compatibility with Excel, Power BI, SAP, Salesforce Connect, and many client libraries — but implementing a correct OData service by hand is a large effort.
4. **Security is bolted on per-project.** Authentication, role-based authorization, field-level masking, rate limiting, and auditing get re-implemented (inconsistently) in every API project.

## 3. Solution

ez-odata-api is a single deployable server that:

1. **Connects** to one or more existing databases (PostgreSQL, MySQL/MariaDB, SQL Server, SQLite in v1) using admin-supplied connection details.
2. **Introspects** tables, views, columns, primary/foreign keys, and relationships into a normalized schema model, cached and refreshable.
3. **Generates, at runtime**, a complete OData v4 service per database service — entity sets, `$metadata` (CSDL), full query options, CRUD, batch — with **no code generation or restart required**.
4. **Mirrors** the same data through a simpler REST/JSON dialect for consumers that don't speak OData.
5. **Secures** everything with API keys, JWT sessions, role-based access control down to table/verb/field granularity, rate limits, and audit logging.
6. **Documents** every endpoint automatically: OData CSDL `$metadata` plus generated OpenAPI 3.1 documents and an embedded API explorer.
7. **Serves AI** through a built-in MCP (Model Context Protocol) server that exposes governed query/CRUD tools per service, so LLMs make deterministic, policy-checked calls instead of writing raw SQL.
8. **Administers** itself through a web admin console and a complete management API (everything the UI can do, the API can do).

## 4. Goals (v1)

| # | Goal | Measure |
|---|------|---------|
| G1 | Zero-code API generation from an existing SQL database | Connect → working OData endpoint in < 5 minutes |
| G2 | OData v4 compliance sufficient for major clients | Works with Excel/Power BI "OData feed", Apache Olingo client, OData .NET/JS client libraries |
| G3 | Governed access | All data paths (OData, REST, MCP) flow through one RBAC + audit pipeline |
| G4 | Live schema, no restarts | New service or schema refresh visible without process restart |
| G5 | Self-hosted, ops-friendly | Single container image; works with Docker Compose and Kubernetes |
| G6 | AI-ready | Claude Desktop / Cursor / any MCP client can query a connected database with role-scoped tools |

## 5. Non-Goals (v1)

These are explicitly out of scope for v1 (see `14-roadmap.md` for sequencing):

- NoSQL connectors (MongoDB, Cassandra, etc.)
- File storage services (S3, Azure Blob, SFTP), email, push notifications, SOAP-to-REST
- Server-side scripting engine (user-supplied Python/JS hooks)
- Stored procedure / function invocation endpoints
- Schema **management** via API (creating/altering tables in target DBs) — v1 is read-only with respect to DDL
- Multi-tenant isolation of admin domains (v1 is single-tenant with multiple services)
- SSO federation (SAML/OIDC/LDAP as identity *providers*) — v1 ships local users + API keys + JWT; OIDC login is a fast-follow
- Horizontal write scaling guarantees beyond what the target database provides

## 6. Personas

1. **Platform Admin (primary).** DevOps/data engineer who installs the server, connects databases, defines roles and keys, and monitors usage. Interacts via the admin console and admin API.
2. **API Consumer Developer.** Frontend/integration developer consuming the generated OData/REST endpoints with an API key. Interacts via docs explorer and HTTP.
3. **AI/Agent Builder.** Developer wiring an LLM agent (Claude Desktop, Cursor, custom MCP client) to enterprise data. Interacts via the MCP endpoint with an API key.
4. **Data Analyst.** Non-developer pulling data into Excel/Power BI via OData feed URL + key.
5. **Security Reviewer.** Audits role definitions, key scopes, and the audit log.

## 7. Key User Journeys

### J1 — Connect a database and get an API (Admin)
1. Admin logs into the console, clicks **Services → New Service**, picks "PostgreSQL", enters host/credentials, clicks **Test Connection**, then **Save**.
2. The platform introspects the schema (progress shown), and the service becomes **Active**.
3. Admin opens the **API Docs** tab and sees every table as an entity set with live "try it" support.
4. `GET /api/odata/{service}/$metadata` now returns the CSDL document; `GET /api/odata/{service}/Customers?$top=5` returns rows.

### J2 — Issue scoped access (Admin)
1. Admin creates a Role "ReadOnly-Sales" granting `GET` on `Customers` and `Orders` only, with the `Customers.ssn` field denied.
2. Admin creates an App/API key bound to that role, with a 100 req/min rate limit.
3. Consumer calls the API with `X-API-Key`; writes and other tables return `403`; `ssn` never appears in any response shape (including `$select`, `$expand`).

### J3 — Excel / Power BI (Analyst)
1. Analyst pastes `https://host/api/odata/sales` into Power BI "OData feed", supplies the API key header.
2. Tables appear; filters and column selection fold to server-side `$filter`/`$select`.

### J4 — AI agent (Agent Builder)
1. Builder adds `https://host/mcp` with their API key to Claude Desktop / Cursor.
2. The MCP client lists tools like `sales_query_table`, `sales_get_record`, `sales_list_tables`, `sales_describe_table`.
3. The LLM answers "top 10 customers by order value this quarter" by calling `sales_query_table` with structured arguments — every call RBAC-checked and audit-logged.

## 8. Competitive / Reference Landscape

| Product | Relation |
|---------|----------|
| DreamFactory | Direct functional inspiration (REST generation, RBAC, scripting, MCP). PHP/Laravel; we differ by being OData-first and .NET-native. |
| Hasura / PostGraphile | GraphQL equivalents; validate the "instant API from DB" category. |
| Microsoft RESTier / ASP.NET Core OData | Library-level OData; require compiled, code-first models. We provide runtime, schema-driven models with governance on top. |
| Supabase / PostgREST | Postgres-only instant REST; no OData, narrower governance, not multi-database. |
| SAP Gateway | Enterprise OData provisioning; heavyweight, SAP-centric. |

Differentiators: **OData v4 + REST + MCP from one governed pipeline**, multi-engine SQL support, self-hosted single binary/container, Apache-2.0-style openness.

## 9. Glossary

| Term | Definition |
|------|------------|
| **Service** | A named connection to one data source (e.g. `sales` → a PostgreSQL DB). Owns its schema cache, endpoints, and docs. |
| **Connector** | Engine-specific plugin implementing introspection + query translation (PostgreSQL, MySQL, SQL Server, SQLite). |
| **Schema Cache** | The normalized, introspected model (tables, columns, keys, relationships) persisted per service. |
| **EDM** | Entity Data Model — the OData type system; we build an `IEdmModel` dynamically from the schema cache. |
| **CSDL** | The XML/JSON serialization of an EDM, served at `$metadata`. |
| **Entity Set** | OData collection corresponding to a table or view (e.g. `Customers`). |
| **App** | A registered API consumer that owns an API key and is bound to exactly one Role. |
| **Role** | A named bundle of permissions: per-service, per-table, per-verb, with optional row filters and field masks. |
| **Service Access Rule** | One row of a Role's permission matrix (service, resource pattern, verbs, filter, field policy). |
| **System Database** | The platform's own metadata store (services, roles, users, apps, audit) — distinct from target databases. |
| **MCP** | Model Context Protocol — the standard by which AI clients discover and invoke tools. |
| **Identity** | The authenticated principal of a request: an App (API key), a User (JWT), or both (key + session). |

## 10. High-Level Requirements Summary

Functional requirement IDs used throughout the spec are prefixed by area:

- `SVC-*` service management (doc 07)
- `CON-*` connectors/introspection (doc 04)
- `OD-*` OData surface (doc 05)
- `RST-*` REST surface (doc 06)
- `SEC-*` security (doc 08)
- `MCP-*` MCP server (doc 09)
- `UI-*` admin console (doc 10)
- `DOC-*` documentation generation (doc 11)
- `OPS-*` deployment/operations (doc 12)

Non-functional requirements (NFRs):

| ID | Requirement |
|----|-------------|
| NFR-1 | P50 latency overhead added by the platform (vs. raw DB query) ≤ 15 ms for single-entity reads on warm cache. |
| NFR-2 | Sustain ≥ 1,000 req/s read traffic on a 4-vCPU node against a warm Postgres service (simple `$top=25` queries). |
| NFR-3 | All SQL issued to target databases MUST be parameterized; no string-interpolated values, ever. |
| NFR-4 | Schema cache refresh for a 500-table database completes in ≤ 60 s and does not block in-flight requests. |
| NFR-5 | The platform runs as a single container; only external dependency is its system database (PostgreSQL or SQLite for dev). Redis is optional for distributed rate limiting/caching. |
| NFR-6 | Secrets (DB credentials) are encrypted at rest (AES-256-GCM envelope, see doc 08 §9). |
| NFR-7 | Builds are reproducible; the server targets .NET 10 (LTS) and runs on linux-x64/arm64, macOS, Windows. |
| NFR-8 | 99.9% of audit events durably recorded; audit writes must not block the data path (buffered channel + background writer). |
