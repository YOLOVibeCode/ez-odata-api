# 05 — OData v4 API Surface

The primary protocol. Each Active service exposes an OData v4 service rooted at:

```
https://{host}/api/odata/{service}/
```

## 1. Requirements

| ID | Requirement |
|----|-------------|
| OD-1 | Serve a service document at the root and CSDL `$metadata` (XML and JSON) per OData v4.01 CSDL specs. |
| OD-2 | Every exposed table/view is an entity set with GET (collection + by-key); writable tables additionally support POST, PATCH, PUT, DELETE. |
| OD-3 | Support query options: `$filter`, `$select`, `$orderby`, `$top`, `$skip`, `$count`, `$expand` (with nested `$filter/$select/$orderby/$top/$skip`), `$search` (optional, §5.8), `$apply` (groupby/aggregate subset), `$format`. |
| OD-4 | Server-driven paging with `@odata.nextLink` when results exceed page size. |
| OD-5 | `$batch` (JSON batch format) with changesets and atomicity. |
| OD-6 | ETag-based optimistic concurrency when a table has a designated version column (§7). |
| OD-7 | Errors in OData v4 JSON error format with stable inner codes. |
| OD-8 | Full RBAC integration: denied tables absent from `$metadata` for that identity (§9); denied fields stripped from types; row filters silently applied. |
| OD-9 | Unsupported constructs fail loudly with `501 Not Implemented` and a precise message — never silently return wrong data. |
| OD-10 | Conformance target: OData v4 **Intermediate Conformance Level**, plus the Minimal level in full. |
| OD-11 | Honor `Prefer: return=minimal|representation`, `odata.maxpagesize`, `odata.count`, `odata.continue-on-error`. |
| OD-12 | Deterministic pagination: implicit PK tiebreaker ordering. |

## 2. URL Space

| Pattern | Methods | Meaning |
|---------|---------|---------|
| `/api/odata/{service}` | GET | Service document (JSON): list of entity sets visible to caller |
| `/api/odata/{service}/$metadata` | GET | CSDL XML (default) or JSON (`$format=json` / `Accept: application/json`) |
| `/api/odata/{service}/{EntitySet}` | GET, POST | Collection query / create |
| `/api/odata/{service}/{EntitySet}({key})` | GET, PATCH, PUT, DELETE | Single entity. Composite keys: `(k1=v1,k2=v2)`. Key-as-segment also accepted: `/EntitySet/{key}` (4.01 style) |
| `/api/odata/{service}/{EntitySet}({key})/{nav}` | GET | Related entity / collection |
| `/api/odata/{service}/{EntitySet}({key})/{nav}/$ref` | GET | References (read-only in v1) |
| `/api/odata/{service}/{EntitySet}({key})/{prop}` | GET | Single property value (`{prop}/$value` for raw) |
| `/api/odata/{service}/{EntitySet}/$count` | GET | Count with optional `$filter` |
| `/api/odata/{service}/$batch` | POST | JSON batch |

Notes:
- v1 does **not** expose: singletons, functions/actions, `$crossjoin`, `$all`, delta links, `$ref` mutation, media entities, stream properties. All return `501` with explanatory message (OD-9).
- `{service}` resolution is case-insensitive; entity set and property names are case-sensitive as exposed (matching CSDL), with a 404 hint listing near-miss names.

## 3. EDM Mapping Rules (schema snapshot → CSDL)

1. Namespace: `EzOdata.{serviceName}` (sanitized to a valid identifier); container `Container`.
2. Each table/view → `EntityType` + `EntitySet`. Entity type name = singular-ish exposed name (no aggressive inflection: the exposed table name is used as-is for the set; the type name appends `Type` only on collision).
3. Columns → structural properties with EDM types per doc 04 §6, `Nullable`, `MaxLength`, `Precision`, `Scale` populated.
4. PK columns → `<Key>`. Keyless views get no key and are queryable only as collections (no by-key segment).
5. FKs → `NavigationProperty` pairs with `ReferentialConstraint`; `Partner` set both ways.
6. Auto-generated columns annotated `Core.Computed`; computed columns `Core.Computed` + omitted from writes.
7. DB comments → `Core.Description` annotations (tables and columns) — these also feed OpenAPI and MCP descriptions.
8. Custom annotations under the `ez.*` vocabulary: `ez.dbType`, `ez.fallbackType`, `ez.allowedValues`, `ez.readOnlyService`.
9. Per-identity model trimming (OD-8): the effective CSDL served to a caller excludes entity sets the caller cannot `GET` at all, and excludes denied/`writeonly` properties. Cache key = `(schemaVersion, roleVersion)`.

## 4. Reading Data

### 4.1 Collection response (example)

`GET /api/odata/sales/customers?$top=2&$select=id,name&$count=true`

```json
{
  "@odata.context": "https://host/api/odata/sales/$metadata#customers(id,name)",
  "@odata.count": 5417,
  "value": [
    { "id": 1, "name": "Acme Corp" },
    { "id": 2, "name": "Globex" }
  ],
  "@odata.nextLink": "https://host/api/odata/sales/customers?$top=2&$select=id,name&$count=true&$skiptoken=..."
}
```

### 4.2 Paging
- Default page size: service option `defaultPageSize` (25). Client `$top` is honored up to `maxPageSize` (1000); larger values are clamped and signaled via `Preference-Applied: odata.maxpagesize=...`.
- `$skiptoken` is an opaque, HMAC-signed cursor `{lastOrderByValues, lastPk}` enabling keyset pagination for the implicit-order case; falls back to OFFSET for arbitrary `$orderby`. Tampered tokens → 400.

### 4.3 `$filter` — supported grammar
- Comparison: `eq ne gt ge lt le`, `in`
- Logical: `and or not`, parentheses
- Arithmetic: `add sub mul div mod` (numeric properties)
- String functions: `contains startswith endswith tolower toupper trim length indexof substring concat`
- Date functions: `year month day hour minute second date time now()`
- Math: `round floor ceiling`
- Null literal: `eq null` / `ne null`
- Lambda: `any(...)`/`all(...)` on first-level to-many navigations; `any()` without predicate = existence
- Navigation property paths to **to-one** navs in predicates (`customer/country eq 'DE'`) compile to JOINs, depth ≤ 2

Rejected with 501 + message: `isof`, `cast` (except literal-compatible implicit), geo functions, `$it` cross-references, lambda depth > 1.

### 4.4 `$expand`
- Depth limit 3 (configurable per service); width limit 10 expands per request.
- Nested options: `$select`, `$filter`, `$orderby`, `$top`, `$skip`, `$count`, nested `$expand`.
- Example: `/customers?$expand=orders($filter=total gt 100;$top=5;$select=id,total;$expand=items($select=sku,qty))`
- RBAC: an expand to a table the role cannot GET → `403 Forbidden.ExpandDenied` (explicit, not silent omission, because silently dropping data the client asked for is worse than failing).

### 4.5 `$apply` (aggregation subset)
Supported transformation pipelines: `[filter(...)/] groupby((p1,p2), aggregate(p with sum|avg|min|max|countdistinct as alias, $count as n))` and bare `aggregate(...)`. Result entities are open types with the grouped/aliased properties. Anything else → 501.

### 4.6 `$search` (off by default)
Per-service opt-in. When enabled, `$search=term` compiles to OR-ed `contains()` across string columns annotated searchable (default: all string columns ≤ 1024 max length). Combined with `$filter` via AND. No ranking in v1.

## 5. Writing Data

### 5.1 Create
`POST /{EntitySet}` with JSON body. Server fills auto-generated keys/defaults. Response `201` + `Location` + representation (or `204` with `Prefer: return=minimal`). Unknown properties → 400 (`Validation.UnknownProperty`) unless the property is a `writeonly`-policy field. Type coercion is strict (no string→int silently); `ez.allowedValues` enforced.

**Deep insert** (entity with nested to-many collection) is supported one level deep, transactional. Deep insert into a table the role cannot POST → 403.

### 5.2 Update
- `PATCH /{EntitySet}({key})` — partial update (preferred).
- `PUT` — full replace per doc 04 §7.3.
- Key properties in the body must match the URL or be absent (else 400). Auto/computed columns in body → 400.

### 5.3 Delete
`DELETE /{EntitySet}({key})` → `204`. FK violation → `409 Conflict.ForeignKeyViolation` with constraint detail.

### 5.4 Bulk via collection POST
`POST /{EntitySet}` with a JSON array body is a vendor extension (flagged in docs): inserts up to `maxBatchRecords` (default 1000) transactionally; response is an array of created representations. With `Prefer: odata.continue-on-error`, response is `200` with per-record `{status, error?}` envelope.

## 6. `$batch`

- JSON batch format only (no multipart in v1). Max 100 requests per batch; changesets execute in a single transaction **per service** (cross-service changesets → 400).
- `continue-on-error` preference honored outside changesets.
- Request IDs and `dependsOn` supported; reference of new entity ids (`$1/orders`) supported within a changeset.

## 7. Concurrency (ETags)

- A table participates in optimistic concurrency if the service config designates a **version column** (auto-detected: `xmin` system column on PG (opt-in), `rowversion/timestamp` on MSSQL, or any column named in `options.concurrencyColumns`).
- Entities then carry `@odata.etag`; `PATCH/PUT/DELETE` require `If-Match` (or `If-Match: *`). Mismatch → `412`. Missing If-Match when required → `428 Precondition Required`.

## 8. Protocol Details

- `OData-Version: 4.0` response header; requests with `OData-MaxVersion < 4.0` → 400.
- Formats: `application/json` with `odata.metadata=minimal` (default) | `full` | `none`. `$format=json` shortcut. XML payloads (Atom) are **not** supported (501) except CSDL XML.
- `omitNulls` not applied (nulls always serialized) for client predictability.
- All absolute links (`@odata.context`, `nextLink`) respect `X-Forwarded-Proto/Host` (proxy-aware, doc 12 §5).

## 9. Security Integration Summary

(Details in doc 08.) For every request: identity → role → matching `role_service_access` rows → verb check → field policy application (`$select=*` minus denied; explicit `$select` of a denied field → 403 `Forbidden.FieldDenied`; denied field in `$filter`/`$orderby`/`$expand` path → 403) → row filter AND-ed into IR → execution. `$metadata` and service documents are identity-trimmed (OD-8); `$metadata` for anonymous callers requires the service option `publicMetadata=true` (default false).

## 10. Compatibility Targets

Acceptance: the following clients must work against a demo service (verified in CI where feasible, manually otherwise — doc 13 §6):

1. Excel / Power BI "OData feed" (auth via custom header) — list tables, load, filter folding.
2. `Microsoft.OData.Client` (C#) — LINQ queries with `$filter/$expand/$top`, CUD round-trip.
3. `o.js` or `odata-query` JS clients — query building.
4. SAP Cloud SDK OData v4 client — basic read scenarios.
5. Postman/curl — everything in this doc literally.

## 11. Examples (normative)

```http
### Filter + expand + order
GET /api/odata/sales/orders?$filter=status eq 'open' and total gt 250.0
    &$expand=customer($select=name,country)
    &$orderby=created_at desc&$top=25&$count=true

### Composite key
GET /api/odata/sales/order_items(order_id=1001,line_no=2)

### Property value raw
GET /api/odata/sales/customers(42)/name/$value      → text/plain "Acme Corp"

### Create with deep insert
POST /api/odata/sales/orders
{ "customer_id": 42, "status": "open",
  "items": [ { "sku": "X-1", "qty": 2 }, { "sku": "Y-9", "qty": 1 } ] }

### Conditional update
PATCH /api/odata/sales/orders(1001)
If-Match: W/"AAAAAAB9J4E="
{ "status": "shipped" }

### JSON batch
POST /api/odata/sales/$batch
{ "requests": [
  { "id": "1", "method": "POST", "url": "customers",
    "headers": {"content-type":"application/json"},
    "body": { "name": "Initech" } },
  { "id": "2", "dependsOn": ["1"], "method": "POST", "url": "$1/orders",
    "atomicityGroup": "g1", "body": { "status": "open" } }
] }
```
