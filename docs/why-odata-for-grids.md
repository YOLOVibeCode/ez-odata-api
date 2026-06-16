# Why OData is the Right Protocol for Data Grids

Most teams building a data grid face the same friction: the backend team writes a custom
endpoint, the frontend team adds sort/filter/page parameters that differ from every other
endpoint in the codebase, and the whole exercise repeats for every new grid. ez-odata-api
eliminates that cycle by giving every table in your database a standards-compliant OData v4
endpoint the moment you connect it — with no code to write.

This document shows, with concrete URL examples, how the OData capabilities that ez-odata-api
exposes map to the features your grid or reporting tool already expects.

---

## The core problem with hand-rolled grid APIs

A typical hand-rolled grid endpoint looks something like this on the backend:

```
GET /api/customers?page=2&pageSize=25&sortBy=name&sortDir=asc&nameFilter=acme
```

The frontend team negotiates the parameter names, implements the SQL yourself, tests edge
cases like empty sorts and simultaneous filter + page combinations, and then repeats the
entire process for the Orders grid, the Products grid, and every other table. Each
implementation is slightly different; none of it is reusable by third-party tools.

OData solves this with a **standardized query grammar** that grid libraries, reporting tools,
Excel, and Power BI already speak natively.

---

## Feature-by-feature: what OData gives your grid

All examples below assume a service named `sales` connected to a database with a `customers`
table. Base URL: `https://your-host/api/odata/sales`.

### 1. Pagination — built-in, server-enforced

OData uses server-driven pagination. You request a page size and the server tells you where
the next page is — the client never needs to calculate offsets or know the total row count.

**Request the first 25 customers:**
```
GET /customers?$top=25
```

**Response (abbreviated):**
```json
{
  "@odata.context": "/$metadata#customers",
  "value": [ ... 25 rows ... ],
  "@odata.nextLink": "/customers?$skiptoken=eyJuZXh0IjoyNX0"
}
```

The `@odata.nextLink` is a signed, tamper-proof cursor — clicking "next page" in your grid
is just a fetch of that URL. You never construct pagination URLs by hand, and the server
always enforces the maximum page size you configured (e.g. 1,000 rows max, regardless of
what the client requests).

**Explicitly skip to an offset (e.g. Excel-style row-number paging):**
```
GET /customers?$top=25&$skip=50
```

---

### 2. Column selection — only fetch what the grid shows

A grid showing Name, Email, and Country doesn't need every column in the row. `$select`
lets the client request exactly the columns it needs. The server projects them all the way
to the SQL `SELECT` clause — no over-fetching, no bandwidth waste.

```
GET /customers?$select=id,name,email,country&$top=25
```

The SQL generated internally is:
```sql
SELECT "id", "name", "email", "country" FROM "customers" ORDER BY "id" LIMIT 26
```

Combine with pagination — this is the pattern virtually every data grid uses:
```
GET /customers?$select=id,name,email,country&$top=25&$orderby=name asc
```

---

### 3. Filtering — the full SQL-style grammar, no backend code

`$filter` is a rich expression language. Your grid's filter bar maps directly to it without
any server-side work.

**Simple equality (dropdown filter):**
```
GET /customers?$filter=country eq 'US'
```

**Multi-column text search (search box):**
```
GET /customers?$filter=contains(name,'acme') or contains(email,'acme')
```

**Numeric range (price slider):**
```
GET /orders?$filter=total ge 100 and total le 500
```

**Date range (date picker):**
```
GET /orders?$filter=created_at ge 2026-01-01T00:00:00Z and created_at lt 2026-06-01T00:00:00Z
```

**Multi-value "in" filter (checkbox facets):**
```
GET /customers?$filter=country in ('US','DE','JP')
```

**Null checks (empty-state filters):**
```
GET /customers?$filter=ssn ne null
```

All of these push the filtering to the database — no rows are loaded into application memory
and then discarded.

---

### 4. Sorting — multi-column, server-side

```
GET /customers?$orderby=country asc,name asc&$top=25
```

The server appends a deterministic tiebreaker (the primary key) to every ORDER BY, so
pagination is always stable even when two rows share the same sort values. No duplicates or
missing rows when the user clicks to page 2.

---

### 5. Total row count — without a second request

Grids that show "showing 26–50 of 1,247 rows" need the total count. Without OData you make
two round trips: one for the page of data, one for `SELECT COUNT(*)`. With `$count=true`
you get both in a single request:

```
GET /customers?$filter=country eq 'US'&$top=25&$count=true
```

```json
{
  "@odata.count": 847,
  "value": [ ... 25 rows ... ]
}
```

The server runs the COUNT in a separate but parallel query and inlines it into the response.

---

### 6. Related data — no N+1 problem

A customer grid that shows the customer's most recent order status would normally require
either a JOIN endpoint or a second request per row (the N+1 problem). `$expand` fetches the
related records in a single batched query:

```
GET /customers?$select=id,name,country&$expand=orders($top=1;$orderby=created_at desc;$select=status,total)&$top=25
```

Each customer row in the response includes its most recent order inline:
```json
{
  "id": 1,
  "name": "Acme Corp",
  "country": "US",
  "orders": [{ "status": "shipped", "total": 258.00 }]
}
```

ez-odata-api executes this as two queries (customers page, then children batch by parent
key) — never a SELECT per row.

---

### 7. Aggregation — groupby and totals without a custom endpoint

Dashboards and summary rows need aggregated data. `$apply` gives you groupby/aggregate
without writing a stored procedure or a custom endpoint:

**Sales by country:**
```
GET /orders?$apply=groupby((customer_id),aggregate(total with sum as revenue))
```

**Order count by status:**
```
GET /orders?$apply=groupby((status),aggregate($count as n))
```

**Grand total:**
```
GET /orders?$apply=aggregate(total with sum as grand_total)
```

---

### 8. Excel and Power BI — zero additional work

Because ez-odata-api exposes a standards-compliant OData v4 feed, Excel and Power BI can
connect to it directly using their built-in connectors — no plugins, no custom code.

**Excel** (Data → Get Data → From OData Feed):
```
https://your-host/api/odata/sales
```

Excel will display all entity sets as tables, let the user pick columns, and handle paging
automatically. Refreshing the spreadsheet re-queries the live database.

**Power BI** (Get Data → OData Feed):
```
https://your-host/api/odata/sales/customers?$select=id,name,country
```

Power BI respects the `$filter` and `$select` you pre-set in the URL, so the analyst only
sees the data they're permitted to see — the same RBAC rules that apply to API consumers
apply here too.

---

### 9. OpenAPI spec — free documentation for every grid endpoint

Every service automatically generates an OpenAPI 3.1 document, identity-trimmed to the
caller's permissions. Tools like Postman, Insomnia, and API platforms can import it directly.

```
GET /api/odata/sales/openapi.json        # OData-style paths
GET /api/rest/sales/openapi.json         # REST-style paths
```

---

## Comparison: hand-rolled vs. OData

| Capability | Hand-rolled endpoint | OData via ez-odata-api |
|---|---|---|
| Pagination | Implement per endpoint | `$top` + signed nextLink, automatic |
| Column selection | Rarely implemented | `$select`, SQL-projected |
| Filtering | Custom param per endpoint | Standard `$filter` grammar |
| Sorting | Custom param per endpoint | `$orderby`, stable with PK tiebreaker |
| Total count | Second query, second endpoint | `$count=true`, same request |
| Related data | N+1 or JOIN endpoint | `$expand` with batched child query |
| Aggregation | Custom stored proc or endpoint | `$apply` groupby/aggregate |
| Excel / Power BI | Custom connector or CSV export | Native OData feed URL |
| Documentation | Manual Swagger maintenance | Auto-generated OpenAPI 3.1 |
| Security | Re-implemented per endpoint | One RBAC + audit pipeline for all |
| New table | Write a new endpoint | Connect a database, done |

---

## A complete grid request

Putting it all together — a grid showing paginated, sorted, filtered orders with inline
customer name, a total-count badge, and only the columns the grid actually renders:

```
GET /api/odata/sales/orders
  ?$select=id,status,total,created_at
  &$expand=customer($select=name,country)
  &$filter=status ne 'cancelled' and total gt 50
  &$orderby=created_at desc
  &$top=25
  &$count=true
  &X-API-Key: ez_live_...
```

One HTTP request. The server:
1. Validates the key and enforces the role's row filter (e.g. `country eq 'US'`)
2. Projects only the four requested columns to SQL
3. Joins and returns the customer name inline
4. Filters cancelled orders and small totals at the database level
5. Sorts by date, pages to 25 rows, appends a signed next-page cursor
6. Returns the total count of matching rows

The frontend grid needs no backend changes for any of this — it constructs the URL from
whatever the user has set in the filter bar, sort header, and page controls.

---

## Getting started

Connect your database and get all of this in under five minutes:

```bash
# 1. Start the server (Docker)
docker compose -f deploy/compose/docker-compose.minimal.yml up -d

# 2. Open http://localhost:8080 and complete first-run setup

# 3. Connect your database and copy the API key

# 4. Point your grid at the OData endpoint
https://localhost:8080/api/odata/{service}/{table}
```

See the main [README](../README.md) for the full walkthrough and the
[OData API spec](05-odata-api.md) for the complete query-option reference.
