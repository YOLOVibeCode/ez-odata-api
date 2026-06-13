# 11 — Documentation Generation (CSDL, OpenAPI, Explorer)

Every service is self-documenting through three artifacts, all generated from the schema snapshot (single source of truth) and all **identity-trimmed** (a caller's docs only show what their role permits).

## 1. Requirements

| ID | Requirement |
|----|-------------|
| DOC-1 | CSDL `$metadata` (XML + JSON) per service — already specified in doc 05; shared generation core. |
| DOC-2 | OpenAPI 3.1 document per service per dialect: `/api/odata/{service}/openapi.json` and `/api/rest/{service}/openapi.json`. |
| DOC-3 | OpenAPI for the admin API at `/system/openapi.json` (generated from code, since the admin surface is compile-time known). |
| DOC-4 | Embedded interactive explorer at `/docs` with live "try it". |
| DOC-5 | Docs respect RBAC: anonymous access to docs is off by default (`settings.publicDocs=false`); with a key/session, content is trimmed to the identity's permissions. |
| DOC-6 | Regeneration is automatic on schema refresh or role change (cache keyed by `(schemaVersion, roleVersion, dialect)`). |
| DOC-7 | DB comments propagate to descriptions everywhere (CSDL `Core.Description`, OpenAPI `description`, MCP tool/field descriptions). |

## 2. OpenAPI Generation Rules (data services)

- OpenAPI **3.1** (JSON Schema dialect 2020-12).
- `info`: service label + description; `x-ez-schema-version` = snapshot hash.
- One `components.schemas` entry per entity type (post-field-policy), with formats: `int64→type:integer,format:int64`, `Edm.Decimal→type:string,format:decimal` (decimal-as-string to avoid float loss; flagged `x-ez-decimal`), `DateTimeOffset→format:date-time`, `Guid→format:uuid`, `Binary→format:byte`, JSON columns → `{}` (any).
- Write schemas differ from read schemas (`{Entity}Create`, `{Entity}Update`): auto-generated/computed columns excluded from create/update; non-nullable-without-default → `required`; `writeonly` fields appear only in write schemas; masked fields only in read schemas (annotated `x-ez-masked`).
- **OData dialect doc**: paths per doc 05 §2; query options modeled as parameters with enum/pattern constraints where finite; `$filter` documented with a grammar excerpt and per-type examples; responses wrap in `{ "@odata.context", "value": [...] }` envelope schemas. (Comparable in spirit to the OASIS `odata-openapi` mapping; we generate directly rather than converting CSDL.)
- **REST dialect doc**: paths per doc 06 §3 with the envelope schema of doc 06 §2.
- Security schemes: `apiKey` (header `X-API-Key`) + `http bearer`; per-operation security reflects whether the docs identity may call it (operations the role cannot call are omitted entirely, DOC-5).
- Examples: one realistic example per operation synthesized from column types/names (deterministic faker seeded by schema hash, so diffs are stable).

## 3. Caching & Invalidation

- Generated documents cached in memory (LRU, 100 entries) and persisted to the system DB cache table only if generation exceeds 500 ms (big schemas).
- ETag = `sha256(schemaVersion + roleVersion + dialect + generatorVersion)`; clients get `304` on `If-None-Match`.
- Invalidation triggers: schema snapshot swap, any change to the identity's role(s), generator version bump.

## 4. Explorer UX (`/docs`)

- Stack: the admin SPA hosts the explorer (shared build) using **Scalar**-style three-pane layout: nav (services → entity sets → operations), operation detail (params, schemas, examples), live console (request builder + response viewer).
- OData query builder helper: visual `$filter` composer (field/op/value rows → generated expression), `$select`/`$expand` pickers from metadata — lowers the OData learning curve, mirrors role restrictions.
- "Copy as": curl, JavaScript fetch, C# HttpClient, Python requests, Power Query M (OData feed snippet).
- Auth box: paste API key (kept in sessionStorage only) or use current admin session; banner reminds that try-it calls are real and audited.
- Deep-linkable: `/docs/{service}/{entitySet}/{operation}`.

## 5. Static Export

`GET /system/services/{id}/docs/export?dialect=odata|rest&format=openapi.json|openapi.yaml|csdl.xml` returns the artifact for offline use / client codegen pipelines (e.g. `openapi-generator`, `odata2ts`, Kiota — all three validated in CI smoke jobs, doc 13 §6).
