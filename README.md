# ez-odata-api

A self-hosted, governed data-access platform: connect a SQL database, and it introspects
the schema and instantly serves a fully documented **OData v4** API, a **REST/JSON** API,
and a built-in **MCP server** for AI clients — all behind one RBAC + audit pipeline.

Inspired by [DreamFactory](https://github.com/dreamfactorysoftware/dreamfactory), built on
.NET 10 with an engine that also targets `netstandard2.0` so it can be embedded in existing
.NET Framework 4.8 sites (see `spec/15-embedded-library.md`).

## What it does

- **Zero-code APIs**: connect PostgreSQL, MySQL/MariaDB, SQL Server, or SQLite; get CRUD,
  `$filter`/`$select`/`$orderby`/`$top`/`$skip`/`$count`, `$expand`, `$apply`, `$batch`, and
  OpenAPI 3.1 + CSDL docs — with no restarts.
- **Governed access**: role-based permissions down to table, verb, row (filters), and field
  (deny/mask/write-only). The same policy engine secures OData, REST, and MCP.
- **AI-ready**: an MCP endpoint exposes per-identity, role-scoped tools so LLMs query data
  through structured, audited calls instead of raw SQL.
- **Operable**: API keys + JWT, rate limiting, buffered audit log, Prometheus metrics,
  health checks, single container image.

## Quick start (Docker Compose)

```bash
cd deploy/compose
docker compose up -d                 # full profile: platform + Postgres + Redis + sample DB
# or the single-container profile:
docker compose -f docker-compose.minimal.yml up -d
```

Open http://localhost:8080 and complete the first-run setup wizard.

## Run from source

```bash
# Backend (set a dev master key + signing key; see appsettings.Development.json)
dotnet run --project src/EzOdata.Host

# Admin UI (dev server with API proxy)
cd ui && npm install && npm run dev
```

## Architecture

The engine (`Core`, connectors, `OData`, `Rest`, `Docs`) is `netstandard2.0`; the standalone
server, EF system store, MCP, and admin API are modern .NET. All three protocols compile to a
shared **Query IR** that flows through one policy engine and one set of connectors — there is
exactly one authorize-and-execute path. See `spec/` for the full specification.

```
src/
  EzOdata.Core                 # schema model, Query IR, policy engine, security contracts
  EzOdata.Connectors.*         # PostgreSQL / MySQL / SqlServer / Sqlite + shared SQL compiler
  EzOdata.OData / .Rest        # protocol engines (ODL-direct + REST dialect)
  EzOdata.Mcp                  # MCP JSON-RPC server
  EzOdata.Docs                 # CSDL + OpenAPI 3.1 generation
  EzOdata.Data                 # EF Core system store, background workers
  EzOdata.Admin                # /system management API
  EzOdata.AspNetCore           # ASP.NET Core host adapter (MapEzOData / MapEzODataRest)
  EzOdata.Host                 # standalone server (composition root)
  EzOdata.Cli                  # ez-admin operations CLI
ui/                            # React 19 admin console
deploy/                        # Dockerfile, compose, Helm
spec/                          # product & engineering specification (16 docs)
```

## Tests

```bash
dotnet test                    # unit + integration (Testcontainers) + OData conformance + security suite
cd ui && npx playwright test   # admin console smoke (requires a running host)
```

## License

Apache-2.0.
