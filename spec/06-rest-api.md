# 06 — REST/JSON API Surface (Secondary Dialect)

A simpler, DreamFactory-style REST dialect over the **same Query IR / policy / connector pipeline** as OData. Intended for consumers that don't want OData semantics (mobile apps, scripts, webhooks). It is a strict subset in power: anything REST can do, OData can do.

Root: `https://{host}/api/rest/{service}/_table/{table}`

## 1. Requirements

| ID | Requirement |
|----|-------------|
| RST-1 | CRUD on tables/views with the parameter set in §3, semantically identical to the OData equivalents (same IR). |
| RST-2 | Identical security behavior to OData (same policy engine, same error codes). |
| RST-3 | Responses use a stable envelope (§2); errors use RFC 9457 problem+json. |
| RST-4 | Discovery endpoints for tables and fields, identity-trimmed. |
| RST-5 | Bulk write operations with transactional and continue-on-error modes. |

## 2. Envelope

Collections:

```json
{
  "resource": [ { "id": 1, "name": "Acme" } ],
  "meta": { "count": 5417, "next": "/api/rest/sales/_table/customers?cursor=...", "schemaVersion": "sha256:ab12…" }
}
```

Single records are returned bare (no envelope). `meta.count` present only when `include_count=true`.

## 3. Endpoints

| Method + Path | Meaning |
|---------------|---------|
| `GET /_table` | List tables visible to caller: `[{name, label, isView, writable, description}]` |
| `GET /_table/{table}/_schema` | Field metadata (name, type, nullable, pk, autoIncrement, maxLength, allowedValues, description) — post-field-policy |
| `GET /_table/{table}` | Query records |
| `GET /_table/{table}/{id}` | Single record by PK (composite: `id` = comma-joined in PK column order, or use `?id_field=`) |
| `POST /_table/{table}` | Create one record (object body) or many (array body or `{"resource":[...]}`) |
| `PATCH /_table/{table}/{id}` | Partial update by id |
| `PATCH /_table/{table}` | Bulk partial update: body `{"resource":[{...with pk...}]}` **or** `filter` param + single patch object |
| `PUT /_table/{table}/{id}` | Replace by id |
| `DELETE /_table/{table}/{id}` | Delete by id |
| `DELETE /_table/{table}` | Bulk delete: `ids=1,2,3` **or** `filter=` (refuses empty filter unless `force=true` AND role has DELETE on the table with no row filter) |

## 4. Query Parameters (GET /_table/{table})

| Param | Example | Notes |
|-------|---------|-------|
| `filter` | `filter=(status='open') and (total>250)` | SQL-ish grammar (§5), parsed to the same FilterNode IR |
| `fields` | `fields=id,name,total` | Projection; `*` default |
| `order` | `order=created_at desc,id` | |
| `limit` / `offset` | `limit=50&offset=100` | Same clamping as OData paging |
| `cursor` | opaque keyset cursor from `meta.next` | Preferred over offset |
| `include_count` | `true` | Adds `meta.count` |
| `related` | `related=customer,items(limit:5,fields:sku,qty)` | Expansion; depth ≤ 2 in REST dialect |
| `group` / `aggregate` | `group=status&aggregate=sum(total) as revenue,count(*) as n` | Mirrors `$apply` subset |
| `distinct` | `distinct=true` with `fields` | SELECT DISTINCT |
| `id_field` | override key column(s) for `{id}` routes | Validated against unique constraint |

## 5. REST Filter Grammar

A deliberately small SQL-like grammar (familiar to DreamFactory users), compiled to FilterNode IR — **never** concatenated into SQL:

```
expr      := orExpr
orExpr    := andExpr ( "or" andExpr )*
andExpr   := term ( "and" term )*
term      := "not"? ( comparison | "(" expr ")" )
comparison:= field op value
           | field "in" "(" value ("," value)* ")"
           | field ("is null" | "is not null")
           | field "like" string          // % and _ wildcards
op        := "=" | "!=" | ">" | ">=" | "<" | "<=" | "contains" | "starts with" | "ends with"
value     := string | number | true | false | null | datetime'...'
field     := identifier ( "." identifier )?   // to-one nav path, depth 1
```

Identifiers are validated against the (field-policy-filtered) schema; unknown field → 400 listing valid names. String literals use single quotes with `''` escaping.

## 6. Write Semantics

- Identical coercion/validation rules as OData (doc 05 §5).
- Bulk POST/PATCH/DELETE: transactional by default; `?continue=true` switches to per-record results:

```json
{ "resource": [
  { "status": 201, "record": { "id": 7 } },
  { "status": 409, "error": { "code": "Conflict.UniqueViolation", "detail": "customers_email_key" } }
] }
```

- `?return=minimal|representation` (default representation) mirrors `Prefer`.
- Optimistic concurrency: `If-Match` with the same ETag values as OData when the table has a version column.

## 7. Relationship to OData

| Concern | OData | REST dialect |
|---------|-------|--------------|
| Metadata | `$metadata` CSDL | `_schema` JSON |
| Filter | OData grammar | SQL-ish grammar |
| Expansion | `$expand`, depth 3 | `related`, depth 2 |
| Batch | `$batch` changesets | bulk arrays only |
| Aggregation | `$apply` | `group`/`aggregate` params |
| Concurrency | ETag/If-Match | same |
| Pagination | `@odata.nextLink` | `meta.next` cursor |

Both dialects are generated into the same OpenAPI document set (doc 11) and exercised by the same integration test matrix (doc 13).
