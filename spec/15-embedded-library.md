# 15 — Embedded Library Distribution (NuGet "Bolt-On")

In addition to the standalone server, the platform ships as a family of NuGet packages that let any existing **ASP.NET Core** application mount auto-generated OData/REST/MCP endpoints for a database with a few lines of startup code — no separate process, no admin console, no system database required.

Think Swashbuckle/Hangfire-style integration: add a package, add `AddEzOData()` + `MapEzOData()`, point at a connection string, get a governed API.

## 1. Requirements

| ID | Requirement |
|----|-------------|
| EMB-1 | A host app adds ≤ 2 packages and ≤ 10 lines of startup code to expose a working OData v4 endpoint for an existing database. |
| EMB-2 | The embedded pipeline is **the same code** as the standalone server (Query IR, policy engine, SQL compiler, serializers) — no fork. The standalone Host becomes just another consumer of these packages. |
| EMB-3 | No system database in embedded mode: services are defined in code/appsettings; schema snapshots cache to memory + optional file; audit goes to `ILogger`/custom sink. |
| EMB-4 | Security composes with the host's existing ASP.NET Core authentication: endpoints can require host authorization policies, and RBAC rules (verbs, field policies, row filters) are declared via a fluent API that can read the host's `ClaimsPrincipal`. |
| EMB-5 | Everything is opt-in per feature: OData, REST dialect, docs explorer, MCP endpoint, schema-refresh endpoint — each is a separate `Map*` call. |
| EMB-6 | The package never takes over the host pipeline: it registers no global middleware except within its own route branches, honors the host's routing, DI, logging, and `IHostApplicationLifetime`. |
| EMB-7 | Introspection runs on startup in the background by default; mapped endpoints return `503 + Retry-After` until ready. A blocking `WarmUpAsync()` is available for hosts that prefer readiness gating. |
| EMB-8 | Multi-targeting: the engine packages target `netstandard2.0` (usable from .NET Framework 4.8 and all modern .NET); the ASP.NET Core adapter targets `net8.0;net10.0`; the classic Web API adapter targets `net48`. See doc 02 §1.1. |
| EMB-9 | A .NET Framework 4.8 ASP.NET (Web API 2 / OWIN) site can mount the OData + REST endpoints via `EzOdata.WebApi` with the same fluent configuration model; MCP and the EF-backed system store are unavailable on net48 (documented capability matrix, doc 02 §1.1). |

## 2. Package Map

| Package | Contents |
|---------|----------|
| `EzOdata.AspNetCore` | Meta-package for modern .NET hosts: core engine, OData + REST endpoints, fluent configuration, in-memory/file metadata store (`net8.0;net10.0`) |
| `EzOdata.WebApi` | Meta-package for .NET Framework 4.8 hosts: same engine, exposed through a classic ASP.NET Web API 2 `HttpMessageHandler`/OWIN middleware; `config.MapEzOData("api/odata")` (`net48`) |
| `EzOdata.AspNetCore.PostgreSql` / `.MySql` / `.SqlServer` / `.Sqlite` | One connector each (thin wrappers over the existing `EzOdata.Connectors.*` assemblies) |
| `EzOdata.AspNetCore.Docs` | Embedded docs explorer + OpenAPI endpoints (static assets included) |
| `EzOdata.AspNetCore.Mcp` | MCP endpoint (`ModelContextProtocol.AspNetCore` dependency lives only here) |
| `EzOdata.AspNetCore.SystemStore` | Optional: EF-backed metadata store + admin API, for hosts that want runtime-managed services/roles inside their own app (the full platform minus the UI) |

The standalone server (`EzOdata.Host`) is re-composed from exactly these packages plus the admin UI — EMB-2 keeps one implementation.

## 3. Host Integration API (normative)

```csharp
// Program.cs of an existing website
builder.Services.AddEzOData(ez =>
{
    // 1. Define data services (the "point it at a database" step)
    ez.AddService("sales", s => s
        .UsePostgreSql(builder.Configuration.GetConnectionString("Sales")!)
        .IncludeSchemas("public")
        .ExcludeTables("audit_*", "tmp_*")
        .DefaultPageSize(50).MaxPageSize(500)
        .ReadOnly(false));

    // Or bind from configuration: "EzOdata:Services:sales": { connector, connectionStringName, options... }
    ez.AddServicesFromConfiguration(builder.Configuration.GetSection("EzOdata"));

    // 2. Security — compose with the host's auth
    ez.Security
      .RequireAuthorizationPolicy("ApiAccess")          // host-defined policy gate for all endpoints
      .AddRole("readonly", r => r
          .ForService("sales")
          .Allow("*", Verbs.Get)
          .DenyFields("customers", "ssn", "salary")
          .MaskField("customers", "email", "***@***"))
      .AddRole("owner-data", r => r
          .ForService("sales")
          .Allow("orders", Verbs.All)
          .WithRowFilter("owner_id eq @identity.claims.sub"))
      .ResolveRolesBy(principal =>                      // map host identity → ez roles
          principal.IsInRole("Admin") ? ["owner-data", "readonly"] : ["readonly"]);

    // 3. Cross-cutting opt-ins
    ez.Audit.UseLogger();                               // or .UseSink<TCustomSink>()
    ez.SchemaCache.UseFile("App_Data/ez-schema.json");  // survive restarts without re-introspection
    ez.RateLimiting.Disable();                          // host may already have its own
});

var app = builder.Build();

app.MapEzOData("/odata");          // /odata/{service}/... per spec doc 05
app.MapEzODataRest("/api/data");   // optional, doc 06
app.MapEzODataDocs("/api-docs");   // optional, doc 11 explorer + openapi.json
app.MapEzODataMcp("/mcp");         // optional, doc 09 (auth note: §5)
```

### 3.1 Semantics

- `AddService` registrations are immutable at startup in pure-embedded mode (no runtime CRUD of services). Hosts that need runtime management add `EzOdata.AspNetCore.SystemStore`.
- `ResolveRolesBy` is the single bridge from host identity to the policy engine; default (if omitted): a deny-all role, forcing an explicit decision (fail closed, consistent with doc 08 §5.1).
- Row filters use the same grammar and `@identity.*` claim references as the platform (doc 08 §5.4); claims come from the host's `ClaimsPrincipal`.
- All doc 05/06 behavior (query options, paging, ETags, error formats, limits) applies unchanged; per-service options map 1:1 to `options_json` of doc 03 §2.1.

## 4. Architectural Changes Required (delta to docs 02/03)

1. **Metadata store abstraction.** Introduce the metadata-store role interfaces in `EzOdata.Core` (`IServiceCatalogReader`, `IServiceCatalogWriter`, `ISchemaSnapshotStore` — segregated per doc 02 §3.1) with two implementation sets: EF-backed (system DB, used by the standalone Host and `SystemStore` package) and in-memory/file-backed (built from the fluent/config model, embedded default). Pure-embedded mode registers **no** `IServiceCatalogWriter` at all — immutability by construction, not by guard.
2. **Policy source abstraction.** The policy engine already evaluates rule objects; rules now arrive from `IPolicySource` (DB-backed or code-declared). The evaluation algorithm (doc 08 §4–5) is shared and unchanged.
3. **Identity adapter.** `RequestIdentity` (doc 08 §2) gains a third construction path: host-`ClaimsPrincipal` + `ResolveRolesBy`, alongside API-key and platform-JWT paths. API-key auth is **not** registered in embedded mode unless the host opts in (`ez.Security.AddApiKeys(...)` with a host-supplied key validator).
4. **Audit sink abstraction.** `IAuditSink` with `DbAuditSink` (platform) and `LoggerAuditSink`/custom (embedded). The buffered-channel pipeline (doc 08 §8) is shared.
5. **Route registration.** OData per-service model injection (doc 02 §5) is already route-branch-scoped; `MapEzOData` mounts the same branch under a host-chosen prefix and returns an `IEndpointConventionBuilder` so hosts can chain `.RequireAuthorization(...)`, `.RequireCors(...)`, etc.

These abstractions are introduced during **M1–M2** (they cost little when designed in; retrofitting later is expensive). The embedded packages themselves ship as milestone **M9** after v1 hardening.

## 5. Constraints & Caveats (documented to users)

- **MCP auth:** MCP clients authenticate with headers, not the host's cookie auth. Embedded MCP therefore requires the host to configure a header-based scheme (`AddApiKeys` or bearer) for the `/mcp` branch; `MapEzODataMcp` throws at startup if only cookie auth would apply (fail loud, not open).
- **No admin UI in embedded mode** — configuration is code/appsettings. The docs explorer is the only UI shipped.
- **Migrations/ownership:** the package never writes to the target database except through user-invoked API writes, and creates no tables anywhere (unless `SystemStore` is added, which owns its own schema with a configurable prefix `ez_`).
- **Version skew:** OData behavior is tied to the package version, so the host controls upgrade timing — contract changes follow the platform's SemVer policy (doc 12 §8).
- **Performance isolation:** the engine shares the host's thread pool and Kestrel limits; per-service command timeouts and pool sizes still apply (doc 04 §9). Hosts with strict SLAs are advised to run the standalone server instead; the docs include a decision table (embedded vs standalone).

## 6. Testing Additions (delta to doc 13)

- A `WebApplicationFactory`-based **embedded-host fixture**: a representative MVC + cookie-auth + existing-middleware app with the packages mounted; the full conformance YAML suite (doc 13 §3) runs against this fixture for PostgreSQL in CI — proving EMB-2 (same behavior embedded as standalone).
- Startup contract tests: 503-until-ready (EMB-7), fail-closed when `ResolveRolesBy` omitted, MCP cookie-auth startup guard (§5).
- Package smoke test in release CI: `dotnet new webapp` + `dotnet add package` from the freshly built nupkgs + scripted query round-trip on net10.0 and net8.0 TFMs.

## 7. Distribution

- Packages published to NuGet.org with SourceLink, deterministic builds, signed; versioned in lockstep with the platform release.
- A `dotnet new ezodata-sample` template package demonstrates the canonical integration (used in docs and as the smoke-test seed).
