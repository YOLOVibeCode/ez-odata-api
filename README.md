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

## Using it (end to end)

Everything below is also doable in the admin console at `/`; here it is as raw HTTP so
the flow is explicit. Assume the server is at `http://localhost:8080`.

### 1. Sign in (get an admin token)

```bash
# First run only — creates the system administrator:
curl -X POST http://localhost:8080/system/setup \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@example.com","displayName":"Admin","password":"a-strong-password-1"}'

# Log in to get a JWT:
TOKEN=$(curl -s -X POST http://localhost:8080/system/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@example.com","password":"a-strong-password-1"}' | jq -r .accessToken)
```

### 2. Connect a database (creates a service)

```bash
curl -X POST http://localhost:8080/system/services \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{
        "name": "sales",
        "label": "Sales DB",
        "connectorType": "postgresql",
        "connection": { "host": "db.internal", "port": 5432, "database": "sales",
                        "username": "api_reader", "password": "secret",
                        "tls": { "mode": "prefer" } }
      }'
```

The schema is introspected in the background; the service flips to `Active` and the APIs
go live with no restart. (`connectorType` is one of `postgresql`, `mysql`, `sqlserver`,
`sqlite`; SQLite uses `"connection": { "filePath": "/data/app.db" }`.)

### 3. Create a role and an API key

```bash
# A read-only role, US rows only, ssn hidden:
ROLE=$(curl -s -X POST http://localhost:8080/system/roles \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"analyst","isActive":true,"access":[
        {"serviceName":"sales","resourcePattern":"*","verbs":["GET"],"effect":"allow",
         "rowFilter":"country eq '\''US'\''",
         "fieldPolicies":[{"fieldPattern":"ssn","action":"deny"}]}]}' | jq -r .id)

APP=$(curl -s -X POST http://localhost:8080/system/apps \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"name\":\"analyst-app\",\"roleId\":$ROLE,\"isActive\":true,\"mcpEnabled\":true}" | jq -r .id)

# The full key is returned exactly once:
KEY=$(curl -s -X POST http://localhost:8080/system/apps/$APP/keys \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"default"}' | jq -r .key)
```

### 4. Query your data

```bash
# OData v4 (clients: Excel, Power BI, Microsoft.OData.Client, etc.)
curl "http://localhost:8080/api/odata/sales/customers?\$filter=country eq 'US'&\$top=10&\$expand=orders" \
  -H "X-API-Key: $KEY"

curl "http://localhost:8080/api/odata/sales/\$metadata"            -H "X-API-Key: $KEY"  # CSDL
curl "http://localhost:8080/api/odata/sales/openapi.json"          -H "X-API-Key: $KEY"  # OpenAPI 3.1

# REST/JSON dialect (simpler, SQL-ish filter)
curl "http://localhost:8080/api/rest/sales/_table/customers?filter=country='US'&limit=10" \
  -H "X-API-Key: $KEY"

# Write (subject to the same RBAC)
curl -X POST "http://localhost:8080/api/odata/sales/customers" \
  -H "X-API-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"name":"Acme","email":"ops@acme.example","country":"US"}'
```

The key only sees what its role allows: ungranted tables 404, denied fields never appear,
row filters are always applied — identically across OData, REST, and MCP.

### 5. Use it from an AI client (MCP)

Point Claude Desktop / Cursor at the MCP endpoint with the same key:

```json
{ "mcpServers": { "ez-odata": {
    "url": "http://localhost:8080/mcp",
    "headers": { "X-API-Key": "<your key>" } } } }
```

The tool catalog is generated per-key, so the model only sees `sales_query`,
`sales_list_tables`, etc. for tables that key can actually read.

## Embed in an existing app (no separate server)

Add the OData/REST engine to an ASP.NET Core app you already run — pointed at a database,
with services and roles declared in code and no system database. See
[`src/EzOdata.AspNetCore/Embedded/README.md`](src/EzOdata.AspNetCore/Embedded/README.md):

```csharp
builder.Services.AddEzOData(ez =>
{
    ez.AddService("sales", s => s.UsePostgreSql(connection).Options(o => o.ReadOnly()));
    ez.AddRole("readonly", r => r.Allow("sales", "*", Verb.Get, rowFilter: "owner_id eq @identity.sub"));
    ez.ResolveRolesBy(user => user.IsInRole("Admin") ? ["readonly"] : []);
});
app.MapEzOData("/api/odata");
```

The engine targets `netstandard2.0`, so the same packages run on **.NET Framework 4.8**
(classic ASP.NET Web API 2 via `EzOdata.WebApi`) as well as modern .NET.

The embeddable packages (`EzOdata.AspNetCore`, `EzOdata.WebApi`, `EzOdata.Core`, the
connectors, etc.) are published to **GitHub Packages** by CI. To consume them, add the
source:

```bash
dotnet nuget add source "https://nuget.pkg.github.com/YOLOVibeCode/index.json" \
  --name ezodata --username <you> --password <github-token-with-read:packages>
dotnet add package EzOdata.AspNetCore
```

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
