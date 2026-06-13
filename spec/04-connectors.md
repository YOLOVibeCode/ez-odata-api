# 04 — Connectors & Schema Introspection

A **connector** is the engine-specific plugin that (a) introspects schema, (b) compiles Query IR to dialect SQL, and (c) executes it. v1 ships four connectors: PostgreSQL, MySQL/MariaDB, SQL Server, SQLite.

## 1. Requirements

| ID | Requirement |
|----|-------------|
| CON-1 | Each connector implements the segregated capability contracts of §2; adding an engine requires no changes to protocol layers. |
| CON-2 | Introspection captures tables, views, columns, primary keys, foreign keys, unique constraints, indexes (names only), comments, and auto-generation semantics. |
| CON-3 | All generated SQL is parameterized (NFR-3). Identifier quoting uses the dialect's quoting rules with identifiers validated against the schema snapshot — never client-supplied strings. |
| CON-4 | Connectors support: filtered/sorted/paged SELECT, projected columns, COUNT, single-level and nested JOIN-based expansion, INSERT (single + bulk + returning keys), UPDATE by key or predicate, DELETE by key or predicate, and groupby/aggregate. |
| CON-5 | A connection test (`TestConnectionAsync`) returns within 10 s with a structured result (ok / auth failed / unreachable / TLS error / db missing). |
| CON-6 | Connector errors are mapped to a shared error taxonomy (§8). |
| CON-7 | Read-only mode (`options.readOnly`) is enforced in the connector layer as defense-in-depth (writes throw even if policy misfires). |
| CON-8 | Type mapping to EDM (§6) is total: every column type maps to an EDM type or is explicitly exposed as `Edm.String` with a `fallback` annotation. |

## 2. Abstractions (`EzOdata.Connectors.Abstractions`)

The connector surface follows ISP (doc 02 §3.1): **one role interface per capability**, so consumers depend only on what they use and capabilities can be absent by type rather than by runtime guard.

```csharp
// Capability interfaces — each independently injectable and fakeable
public interface IConnectionTester
{
    Task<ConnectionTestResult> TestAsync(ConnectionSpec spec, CancellationToken ct);
}

public interface ISchemaIntrospector
{
    Task<SchemaSnapshot> IntrospectAsync(ConnectionSpec spec, IntrospectionOptions opts, CancellationToken ct);
}

public interface IQueryExecutor                       // the ONLY surface the read paths see
{
    Task<QueryResult> QueryAsync(ConnectionHandle h, QueryRequest ir, CancellationToken ct);
    Task<long> CountAsync(ConnectionHandle h, QueryRequest ir, CancellationToken ct);
}

public interface IWriteExecutor                       // absent for read-only capable engines/services
{
    Task<WriteResult> WriteAsync(ConnectionHandle h, WriteRequest ir, CancellationToken ct);
}

public interface ISqlDialect
{
    string QuoteIdentifier(string ident);
    string Paginate(string sql, int? top, int? skip);   // LIMIT/OFFSET vs OFFSET..FETCH vs TOP
    string ConcatOperator { get; }
    bool SupportsReturning { get; }                     // RETURNING vs OUTPUT vs last_insert_id
    string MapFunction(FilterFunction fn, IReadOnlyList<string> args); // contains→LIKE/ILIKE etc.
}

// Descriptor: composition + capability discovery, NOT a god interface.
// Members are nullable capabilities; protocol layers never call through the
// descriptor — they resolve the specific capability for a service and inject that.
public sealed record ConnectorDescriptor(
    string ConnectorType,                              // "postgresql" etc.
    IConnectionTester Tester,
    ISchemaIntrospector Introspector,
    IQueryExecutor Reader,
    IWriteExecutor? Writer,                            // null ⇒ engine is read-only (CON-7 by type)
    ISqlDialect Dialect);
```

`ConnectionHandle` wraps the pooled connection factory for a service; protocol layers never see raw connections.

ISP consequences (normative):

- The OData/REST/MCP **read** pipelines take a dependency on `IQueryExecutor` only; **write** pipelines on `IWriteExecutor` only. A read-only service resolves a `Writer` of `null` and the write route returns 403 before any connector code runs.
- The schema cache manager depends on `ISchemaIntrospector` + `IConnectionTester` only; it cannot execute data queries.
- Each capability is independently replaceable in tests: the conformance suite fakes `IQueryExecutor` for parser/compiler tests and uses the real one for engine tests; no test ever mocks a member it doesn't assert (doc 13 §0.3).

### Registration

Connectors self-register via DI: each connector package contributes one `ConnectorDescriptor` to `IConnectorRegistry`, keyed by `connector_type`. Out-of-tree connectors (future) load via standard assembly scanning of a plugins directory — the capability interfaces above are the compatibility contract.

## 3. Connection Specification

`ConnectionSpec` is the decrypted form of `services.connection_encrypted`:

```json
{
  "host": "db.internal", "port": 5432,
  "database": "sales",
  "username": "api_reader",
  "password": "•••",
  "tls": { "mode": "require", "caCertPem": null, "allowInvalid": false },
  "extra": { "ApplicationName": "ez-odata-api" }
}
```

- SQLite uses `{ "filePath": "/data/app.db", "readOnlyFile": false }`.
- `extra` passes through whitelisted provider keywords only (per-connector allowlist; arbitrary keywords rejected to prevent connection-string injection).
- Recommended practice surfaced in the UI: use a **least-privilege DB account** (SELECT-only for read services).

## 4. Introspection Procedure (per engine)

All engines produce the same `SchemaSnapshot` (§5). Sources of truth:

| Engine | Mechanism |
|--------|-----------|
| PostgreSQL | `information_schema` + `pg_catalog` (`pg_class`, `pg_attribute`, `pg_constraint`, `pg_index`, `pg_description` for comments, `pg_get_serial_sequence`/identity detection) |
| MySQL/MariaDB | `information_schema.TABLES/COLUMNS/KEY_COLUMN_USAGE/STATISTICS`, `EXTRA` column for `auto_increment`/generated |
| SQL Server | `sys.tables/views/columns/types/index_columns/foreign_key_columns`, `is_identity`, `is_computed`, extended properties `MS_Description` |
| SQLite | `pragma table_list / table_xinfo / foreign_key_list / index_list`; `AUTOINCREMENT`/rowid detection from `sqlite_master.sql` |

Rules:

1. Apply `includeSchemas` / `excludeTables` globs from service options **before** persisting (excluded objects never exist as far as the platform is concerned — not even for admins).
2. Views are included when `includeViews=true`; marked `isView=true`, `writable=false` (v1 treats all views read-only).
3. Tables without a primary key are exposed **read-only collection** (no by-key routes, no writes); flagged `noKey=true` and surfaced as a warning in the console.
4. Relationship discovery: every FK produces a pair of navigations — many-to-one on the child (`Order.Customer`) and one-to-many on the parent (`Customer.Orders`). Composite FKs supported. Self-referencing FKs supported (`Employee.Manager` / `Employee.Reports`). Name collisions resolved deterministically (§5.3).
5. Exposed-name policy (`exposedNameStyle`): `original` (default — keep DB casing) or `pascal` (PascalCase-ify snake_case). Mapping is recorded both directions in the snapshot; clients only ever see exposed names.

## 5. SchemaSnapshot Contract

Canonical JSON persisted in `schema_snapshots.snapshot_json`:

```json
{
  "version": 1,
  "engine": "postgresql",
  "collectedAt": "2026-06-12T16:00:00Z",
  "tables": [
    {
      "dbSchema": "public",
      "dbName": "customers",
      "exposedName": "customers",
      "isView": false,
      "writable": true,
      "comment": "CRM master table",
      "columns": [
        {
          "dbName": "id", "exposedName": "id",
          "dbType": "integer", "edmType": "Edm.Int32",
          "nullable": false, "maxLength": null, "precision": null, "scale": null,
          "isPrimaryKey": true, "isAutoGenerated": true, "isComputed": false,
          "defaultExpression": null, "comment": null
        },
        { "dbName": "email", "exposedName": "email", "dbType": "citext",
          "edmType": "Edm.String", "nullable": false, "maxLength": 320,
          "isPrimaryKey": false, "isAutoGenerated": false, "isComputed": false }
      ],
      "primaryKey": ["id"],
      "uniqueConstraints": [["email"]],
      "foreignKeys": [
        { "name": "fk_orders_customer", "columns": ["customer_id"],
          "refTable": "customers", "refColumns": ["id"],
          "navToOne": "customer", "navToMany": "orders" }
      ]
    }
  ]
}
```

### 5.1 Canonicalization & hashing
Snapshot JSON is canonicalized (sorted keys, sorted arrays by name) before SHA-256 hashing → `version_hash`. Identical schemas across refreshes produce identical hashes, so `$metadata` ETags stay stable.

### 5.2 Diffing
`SchemaDiff(old, new)` computes added/removed/changed tables and columns for the console "drift" view and for the audit event `service.schema.refreshed`.

### 5.3 Navigation naming
- Many-to-one nav = FK column name stripped of `_id`/`Id` suffix if the result is unambiguous, else the referenced table name, else `ref_{fkName}`.
- One-to-many nav = pluralized child exposed table name; on collision append `_{fkName}`.
- All names deduplicated within a type with deterministic ordering (FK name asc).

## 6. Type Mapping (DB → EDM)

The complete v1 mapping. Anything not listed maps to `Edm.String` with annotation `ez.fallbackType` (value = raw DB type) and is excluded from arithmetic/function filters.

| EDM type | PostgreSQL | MySQL | SQL Server | SQLite (affinity) |
|----------|-----------|-------|------------|-------------------|
| Edm.Int16 | smallint | smallint | smallint | — |
| Edm.Int32 | integer, serial | int, mediumint | int | INTEGER (when fits) |
| Edm.Int64 | bigint, bigserial | bigint | bigint | INTEGER |
| Edm.Decimal | numeric/decimal | decimal | decimal, numeric, money | NUMERIC |
| Edm.Double | double precision, real→Single | double, float | float | REAL |
| Edm.Single | real | float(p≤24) | real | — |
| Edm.Boolean | boolean | tinyint(1), bit(1) | bit | INTEGER 0/1 via `ez.bool` hint |
| Edm.String | text, varchar, char, citext, name, uuid-as-text? no | varchar, text, char, enum→String+allowedValues | varchar, nvarchar, text, ntext, char | TEXT |
| Edm.Guid | uuid | char(36) via hint | uniqueidentifier | TEXT via hint |
| Edm.DateTimeOffset | timestamptz, timestamp (assume UTC, configurable) | datetime, timestamp | datetime2, datetimeoffset, datetime | TEXT ISO-8601 |
| Edm.Date | date | date | date | TEXT |
| Edm.TimeOfDay | time | time | time | TEXT |
| Edm.Duration | interval | — | — | — |
| Edm.Binary | bytea | blob, varbinary | varbinary, image | BLOB |
| Edm.Stream (roadmap) | large objects | — | — | — |
| Edm.Untyped | json, jsonb | json | (nvarchar+json hint) | TEXT+json hint |

Notes:
- `json/jsonb` columns surface as `Edm.Untyped` (arbitrary JSON passthrough); filterable only by `eq null`/`ne null` in v1 (JSON-path filtering is roadmap).
- Arrays (PostgreSQL `int[]`, `text[]`) surface as `Collection(Edm.*)`, read-only in filters except `any()` with `eq` (translated to `= ANY(col)`).
- Enums (MySQL/PG) surface as `Edm.String` with `ez.allowedValues` annotation, enforced on write.
- Unsigned MySQL ints map up a size (`int unsigned` → Edm.Int64).

## 7. SQL Compilation Rules

The `SqlCompiler` (shared core + dialect hooks) compiles Query IR:

### 7.1 SELECT shape

```sql
SELECT <projected, quoted columns / aliased expansions>
FROM <schema>.<table> AS t0
LEFT JOIN ... (expansions, aliased t1..tn)
WHERE <filter tree, fully parameterized>
ORDER BY <orderby list, PK appended as tiebreaker>
<dialect pagination>
```

- **Stable ordering:** if the client gives no `$orderby`, the PK (or all columns for keyless views) is used so pagination is deterministic (OD-12).
- **Expansion strategy:** to-one expands compile to LEFT JOIN with column aliasing. To-many expands compile to a **second batched query** per expand level (`WHERE fk IN (keys of page)`) and are stitched in memory — avoids row explosion. `$top/$skip/$filter/$orderby` inside expand apply to the child query; per-parent `$top` uses `ROW_NUMBER() OVER (PARTITION BY fk)` where supported (all four engines support window functions).
- **$count:** separate `SELECT COUNT(*)` with the same WHERE (no joins unless filter references expansion — v1 rejects filters on expanded properties beyond `any/all` on first-level navs, OD-9).
- **$apply (subset):** `groupby((cols), aggregate(col with sum/avg/min/max/countdistinct as alias))` and `aggregate(...)` compile to GROUP BY queries. Nested transformations beyond one `filter` before `groupby` are rejected with 501.

### 7.2 Filter function translation (representative)

| OData | PostgreSQL | MySQL | SQL Server | SQLite |
|-------|-----------|-------|------------|--------|
| `contains(f,'x')` | `f ILIKE '%'||@p||'%'` (escaped) | `f LIKE CONCAT('%',?,'%') COLLATE ...` | `f LIKE '%'+@p+'%'` | `f LIKE '%'||?||'%'` |
| `startswith` / `endswith` | LIKE with anchored pattern, `%_\` escaped via `ESCAPE '\'` | same | same | same |
| `tolower/toupper` | `lower()/upper()` | same | `LOWER()/UPPER()` | same |
| `length` | `length()` | `CHAR_LENGTH()` | `LEN()` | `length()` |
| `indexof` | `position()-1` | `LOCATE()-1` | `CHARINDEX()-1` | `instr()-1` |
| `substring` | `substr(f, @p+1 [, @n])` | same | `SUBSTRING` | `substr` |
| `trim/concat` | native | native | `TRIM`/`+` | native |
| `year/month/day/hour/minute/second` | `EXTRACT` | `YEAR()` etc. | `DATEPART` | `strftime` |
| `date(f)` / `time(f)` | `::date` / `::time` | `DATE()`/`TIME()` | `CAST` | `strftime` |
| `round/floor/ceiling` | native | native | native (`CEILING`) | native (ceil emulated) |
| `add/sub/mul/div/mod` | native operators with type checks | same | same | same |
| `in` | `= ANY(@arr)` | `IN (...)` | `IN (...)` | `IN (...)` |
| `any/all` on to-many nav | `EXISTS (SELECT 1 FROM child WHERE fk = t0.pk AND <pred>)` | same | same | same |

Case sensitivity: string comparison follows the database collation; `contains/startswith/endswith` are case-insensitive on PostgreSQL via ILIKE to match common expectations, and a per-service option `caseInsensitiveLike` (default true) controls this on other engines (wrapping with LOWER()).

### 7.3 Writes

- **INSERT**: multi-row VALUES batches of ≤ 500 rows per statement; generated keys retrieved via `RETURNING` (PG/SQLite 3.35+), `OUTPUT INSERTED.*` (MSSQL), `LAST_INSERT_ID` + ordered fetch (MySQL). Returns full records when `?$return=representation` (default) or keys only (`minimal`).
- **UPDATE (PATCH)**: only provided columns set; key predicate + role row filter AND-ed. Affected-row count of 0 with existing key + row filter ⇒ `404` (record invisible to this role).
- **PUT (replace)**: all writable non-key columns set; missing nullable columns → NULL, missing non-nullable with default → default, missing non-nullable without default → 400.
- **DELETE**: by key, or bulk by `$filter` (REST dialect only, doc 06 §6; OData uses per-id batch).
- All multi-row writes run in a transaction; partial failure rolls back unless `Prefer: odata.continue-on-error` (then per-record status list is returned, doc 05 §8.4).

## 8. Error Taxonomy

Connectors map provider exceptions to `ConnectorError(code, isTransient, safeMessage)`:

| Code | Triggers (examples) | HTTP |
|------|---------------------|------|
| `Conflict.UniqueViolation` | 23505 (PG), 1062 (MySQL), 2627/2601 (MSSQL) | 409 |
| `Conflict.ForeignKeyViolation` | 23503 / 1451-1452 / 547 | 409 |
| `Validation.NotNullViolation` | 23502 / 1048 / 515 | 400 |
| `Validation.ValueTooLong` | 22001 / 1406 / 2628 | 400 |
| `Validation.InvalidValue` | cast/range errors | 400 |
| `Upstream.Unavailable` | network, auth, pool exhausted | 503 |
| `Upstream.Timeout` | command timeout | 504 |
| `Upstream.PermissionDenied` | DB-level grant missing | 502 (`safeMessage` says service account lacks rights) |
| `Internal.Unmapped` | anything else | 500 |

`safeMessage` includes constraint name and column where the provider exposes it (useful, non-sensitive); raw provider messages go only to server logs.

## 9. Connection Pool Management

- Pools are provider-native, keyed by service; created lazily on first request after Active, disposed on disable/delete/credential change.
- Health: a background probe (`SELECT 1`, every 60 s, jittered) flips a service to degraded state metrics (does not auto-disable). Exposed at `/system/services/{id}/health` and on the Prometheus endpoint.
- Pool exhaustion returns `503 Upstream.Unavailable` after `ConnectTimeout` (default 5 s) rather than queuing unboundedly.
