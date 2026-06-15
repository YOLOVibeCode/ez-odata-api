# EzOdata.AspNetCore — embeddable OData/REST engine

Bolt a governed OData v4 + REST API onto an existing ASP.NET Core app, pointed at a
database, with no separate process and no system database (spec doc 15).

## Install

```bash
dotnet add package EzOdata.AspNetCore
```

## Use

```csharp
using EzOdata.AspNetCore;            // MapEzOData / MapEzODataRest
using EzOdata.AspNetCore.Embedded;   // AddEzOData
using EzOdata.Core.Policy;           // Verb, FieldRule
using EzOdata.Connectors.Abstractions; // ConnectionSpec

builder.Services.AddEzOData(ez =>
{
    ez.AddService("sales", s => s
        .UsePostgreSql(new ConnectionSpec { Host = "db", Database = "sales", Username = "api", Password = "..." })
        .Options(o => o.DefaultPageSize(50).ReadOnly()));

    ez.AddRole("readonly", r => r
        .Allow("sales", "*", Verb.Get,
            rowFilter: "owner_id eq @identity.sub",
            fieldRules: [new FieldRule("ssn", FieldAction.Deny, null)]));

    // Option A — your ASP.NET role claims map directly to ez role names:
    ez.UseHostRoles();

    // Option B — a specific claim (e.g. a JWT "scope" claim) maps to role names:
    ez.MapRolesFromClaim("scope", raw => raw.Replace("data:", ""));

    // Option C — full custom lambda (escape hatch):
    ez.ResolveRolesBy(user => user.IsInRole("Admin") ? ["readonly"] : []);

    // Option D — no-auth dev bypass (ONLY valid when ASPNETCORE_ENVIRONMENT=Development):
    // ez.AllowAnonymousInDevelopment();
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapEzOData("/api/odata");        // OData v4
app.MapEzODataRest("/api/data");     // optional REST dialect
```

The same RBAC, row filters, field masking, `$filter`/`$expand`/`$apply`, OpenAPI, and
injection-safe SQL compilation as the standalone server — running inside your process.

## Host-auth pass-through

`UseHostRoles()` and `MapRolesFromClaim(...)` are convenience wrappers that:
- read the role names from the host's `ClaimsPrincipal` (standard `ClaimTypes.Role` or any custom claim),
- flow `sub`/`email` and all other claims automatically so `@identity.<claim>` row-filter
  expressions resolve without extra wiring,
- map a `sub` or `NameIdentifier` claim to `RequestIdentity.UserId` when it parses as a long.

## Dev no-auth mode

```csharp
ez.AllowAnonymousInDevelopment();
```

Unauthenticated requests receive a full-access bypass identity — no policy evaluation,
no trimming, everything visible. **Hard-blocked outside `ASPNETCORE_ENVIRONMENT=Development`;
the application refuses to start if the flag is set in any other environment.**

## Capability notes

- No system database: services and roles are declared in code; schema is introspected on
  startup and cached in memory.
- Security composes with your host's authentication via `UseHostRoles`, `MapRolesFromClaim`,
  or the `ResolveRolesBy` escape hatch. Row filters reference `@identity.<claim>`.
- MCP and the EF-backed admin store are not part of the embedded package (modern-.NET,
  standalone-server concerns).
- The engine targets `netstandard2.0`; this adapter targets modern .NET. A classic
  ASP.NET Web API 2 adapter (`EzOdata.WebApi`, net48) follows the same shape.
