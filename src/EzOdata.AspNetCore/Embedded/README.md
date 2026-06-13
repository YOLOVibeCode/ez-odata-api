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

    // Bridge YOUR app's identity to ez roles (fail-closed if omitted).
    ez.ResolveRolesBy(user => user.IsInRole("Admin") ? ["readonly"] : []);
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapEzOData("/api/odata");        // OData v4
app.MapEzODataRest("/api/data");     // optional REST dialect
```

The same RBAC, row filters, field masking, `$filter`/`$expand`/`$apply`, OpenAPI, and
injection-safe SQL compilation as the standalone server — running inside your process.

## Capability notes

- No system database: services and roles are declared in code; schema is introspected on
  startup and cached in memory.
- Security composes with your host's authentication via `ResolveRolesBy`. Row filters can
  reference `@identity.<claim>` (resolved from the host `ClaimsPrincipal`).
- MCP and the EF-backed admin store are not part of the embedded package (modern-.NET,
  standalone-server concerns).
- The engine targets `netstandard2.0`; this adapter targets modern .NET. A classic
  ASP.NET Web API 2 adapter (`EzOdata.WebApi`, net48) follows the same shape.
