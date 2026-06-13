# ez-odata-api — Product & Engineering Specification

A self-hosted, governed data-access platform: connect SQL databases, get instant **OData v4 + REST APIs** and a governed **MCP server** for AI — with RBAC, field/row security, rate limiting, auditing, auto-docs, and a web admin console. Inspired by [DreamFactory](https://github.com/dreamfactorysoftware/dreamfactory), built on .NET 10.

## Reading Order

| Doc | Contents |
|-----|----------|
| [01 — Overview](01-overview.md) | Vision, problem, goals/non-goals, personas, journeys, glossary, NFRs |
| [02 — Architecture](02-architecture.md) | Stack, components, solution layout, request pipeline, dynamic EDM strategy, Query IR, error model |
| [03 — System Data Model](03-system-data-model.md) | Metadata-store schema: services, snapshots, roles, apps, keys, users, rate limits, audit |
| [04 — Connectors](04-connectors.md) | Connector abstraction, introspection per engine, snapshot contract, EDM type mapping, SQL compilation, error taxonomy |
| [05 — OData API](05-odata-api.md) | URL space, query options, writes, `$batch`, ETags, conformance targets, examples |
| [06 — REST API](06-rest-api.md) | Secondary JSON dialect: endpoints, filter grammar, bulk semantics |
| [07 — Admin API](07-admin-api.md) | `/system/*` management plane incl. role simulator, config export/import |
| [08 — Security](08-security.md) | Threat model, identity, authn, RBAC semantics, rate limiting, audit, secrets/encryption, acceptance tests |
| [09 — MCP Server](09-mcp-server.md) | Tool catalog, schemas, identity-trimming, safety posture, client onboarding |
| [10 — Admin UI](10-admin-ui.md) | Console IA, screens, UX quality bar |
| [11 — Docs Generation](11-openapi-and-docs.md) | CSDL, OpenAPI 3.1, explorer, caching |
| [12 — Deployment & Ops](12-deployment-operations.md) | Packaging, topologies, config reference, observability, CLI, backup/DR |
| [13 — Testing & Quality](13-testing-quality.md) | Test pyramid, conformance suite, SQL-safety invariants, perf budgets, CI/CD |
| [14 — Roadmap](14-roadmap.md) | v1 milestones, post-v1 roadmap, risks |
| [15 — Embedded Library](15-embedded-library.md) | NuGet bolt-on for existing ASP.NET Core apps: package map, fluent host API, host-auth integration |

## Decisions Locked In

- **Protocols:** OData v4 primary, REST/JSON secondary, MCP for AI — all over one shared Query IR + policy engine + audit pipeline.
- **Stack:** engine on `netstandard2.0` using ODataLib 7.x directly (runs on .NET Framework 4.8 **and** modern .NET); standalone server on .NET 10 LTS / ASP.NET Core; host adapters for ASP.NET Core (net8/net10) and classic ASP.NET Web API 2 (net48); official MCP C# SDK (modern hosts only); React 19 admin UI. See doc 02 §1.1.
- **v1 connectors:** PostgreSQL, MySQL/MariaDB, SQL Server, SQLite — via our own connector abstraction and dialect-aware SQL compiler (no ORM on the data path; everything parameterized).
- **v1 scope:** instant CRUD APIs, RBAC down to field/row level, API keys + JWT, rate limiting, audit logging, auto OpenAPI/CSDL docs + explorer, MCP server, admin console. Stored procedures, scripting, NoSQL, file services, SSO federation: roadmap (doc 14).
- **Two distributions:** the standalone server (Docker/Helm) and an embeddable NuGet package family (`EzOdata.AspNetCore.*`) that bolts the same engine onto an existing ASP.NET Core site (doc 15).
- **Engineering discipline:** strict TDD for all engine code (red/green/refactor, spec-as-failing-tests — doc 13 §0) and the Interface Segregation Principle for every abstraction (role interfaces like `IQueryExecutor`/`IWriteExecutor`/`IAuditSink`, enforced by architecture tests — doc 02 §3.1).

## Requirement ID Index

Requirements carry stable IDs (`OD-*`, `SEC-*` as section tables in each doc) and are traceable to tests via `[Requirement("…")]` attributes (doc 13 §8).
