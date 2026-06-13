# 07 — Admin / System API

Management plane at `https://{host}/system/*`. Everything the admin console does goes through this API (UI parity rule: **no hidden capabilities**). JSON only; errors are RFC 9457 problem+json; all endpoints require an authenticated identity whose role has `is_admin=true` (or `is_system_admin` user), except `/system/setup` and `/system/auth/*`.

## 1. Requirements

| ID | Requirement |
|----|-------------|
| SVC-1 | Full CRUD for services, roles, apps, keys, users, rate-limit policies, settings. |
| SVC-2 | Connection test and schema introspection are explicit, observable operations (job status). |
| SVC-3 | All mutations are audited (`category=admin`) with before/after diff summaries (no secrets). |
| SVC-4 | List endpoints support `?filter=`, `?sort=`, `?limit/offset`, `?fields=` using the REST dialect conventions of doc 06. |
| SVC-5 | Optimistic concurrency on all mutable resources via `row_version` ETags (`If-Match`). |
| SVC-6 | Export/import of the security configuration (roles, apps sans secrets, services sans credentials) as a single JSON document for environment promotion. |

## 2. Authentication Endpoints (`/system/auth`)

| Endpoint | Notes |
|----------|-------|
| `POST /system/auth/login` | `{email, password}` → `{accessToken (JWT, 15 min), refreshToken (httpOnly cookie or body for non-browser), user}`; lockout per doc 08 §3.3 |
| `POST /system/auth/refresh` | Rotates refresh token |
| `POST /system/auth/logout` | Revokes refresh token |
| `GET /system/auth/me` | Current identity: user, roles, effective permissions summary |
| `POST /system/auth/password` | Change own password (requires current) |
| `GET/DELETE /system/auth/sessions` | List / revoke own refresh sessions |

## 3. Setup (`/system/setup`)

`GET` → `{required: bool}`. `POST {email, displayName, password}` — allowed only when no users exist; creates system admin; transitions instance out of setup mode. See doc 03 §4.

## 4. Services (`/system/services`)

| Endpoint | Notes |
|----------|-------|
| `GET /system/services` | List with status, table counts, last introspection |
| `POST /system/services` | Create. Body: `{name, label, connectorType, connection:{...}, options:{...}}`. Credentials accepted **only** on create/update, encrypted immediately, never echoed (responses show `connection: {host, port, database, username, tls.mode}` only). Creation enqueues introspection. |
| `GET /system/services/{id}` | Detail incl. current snapshot summary + health |
| `PATCH /system/services/{id}` | Update label/options/connection (connection change re-tests + re-introspects) |
| `DELETE /system/services/{id}` | Soft delete (doc 03 §5) |
| `POST /system/services/{id}/test` | Run connection test now → structured result (CON-5) |
| `POST /system/services/{id}/refresh` | Enqueue schema refresh → `{jobId}` |
| `GET /system/services/{id}/schema` | Current snapshot (full JSON, admin view — pre-field-policy) |
| `GET /system/services/{id}/schema/diff` | Diff vs previous snapshot |
| `POST /system/services/{id}/enable` / `disable` | Status toggles |
| `GET /system/services/{id}/health` | Probe status, pool stats, last error |
| `GET /system/connectors` | Available connector types + their connection/options JSON Schemas (drives dynamic UI forms) |

## 5. Roles (`/system/roles`)

| Endpoint | Notes |
|----------|-------|
| CRUD `/system/roles[/{id}]` | Role body embeds its access rules and field policies as a nested document (saved atomically): |

```json
{
  "name": "sales-readonly",
  "description": "Read-only sales data, no PII",
  "isActive": true,
  "access": [
    {
      "serviceName": "sales",
      "resourcePattern": "*",
      "verbs": ["GET"],
      "effect": "allow",
      "priority": 0,
      "rowFilter": null,
      "fieldPolicies": [
        { "fieldPattern": "ssn", "action": "deny" },
        { "fieldPattern": "email", "action": "mask", "maskValue": "***@***" }
      ]
    },
    { "serviceName": "sales", "resourcePattern": "salaries", "verbs": ["GET","POST","PUT","PATCH","DELETE"], "effect": "deny", "priority": 100 }
  ]
}
```

| Endpoint | Notes |
|----------|-------|
| `POST /system/roles/{id}/simulate` | **Permission simulator**: body `{serviceName, table, verb, fields?, identityClaims?}` → `{allowed, matchedRule, effectiveRowFilter, deniedFields, maskedFields}`. Powers the UI "test this role" feature and CI policy tests. |
| `GET /system/roles/{id}/usage` | Apps and users bound to the role |

## 6. Apps & API Keys (`/system/apps`)

| Endpoint | Notes |
|----------|-------|
| CRUD `/system/apps[/{id}]` | `{name, description, roleId, isActive, allowedOrigins, requireUserSession}` |
| `POST /system/apps/{id}/keys` | Create key: `{name, expiresAt?}` → returns **full key once**: `{key: "ez_live_AbC1...", keyPrefix, id}` |
| `GET /system/apps/{id}/keys` | List (prefix, name, lastUsedAt, expiresAt, revokedAt) |
| `DELETE /system/apps/{id}/keys/{keyId}` | Revoke (sets `revoked_at`; cache invalidated instantly via pub/sub) |

Key format: `ez_{env}_{22 base62 chars}` where env ∈ `live|test` (cosmetic only).

## 7. Users (`/system/users`)

CRUD; `{email, displayName, isActive, roleIds[]}`; `POST /system/users/{id}/password-reset` issues a one-time reset token (returned to admin; email delivery is roadmap). Self-deactivation and removal of the last system admin are rejected (409).

## 8. Rate Limits (`/system/rate-limits`)

CRUD over `rate_limit_policies` (doc 03 §2.10). `GET /system/rate-limits/effective?appId=...` shows the resolved chain for debugging.

## 9. Audit (`/system/audit`)

| Endpoint | Notes |
|----------|-------|
| `GET /system/audit` | Filterable: `category, action, outcome, serviceId, appId, userId, from, to, requestId`; keyset-paged; max range 31 days per query |
| `GET /system/audit/export` | NDJSON streaming export, same filters |
| `GET /system/audit/stats` | Time-bucketed counts (drives dashboard charts): `?bucket=hour&from=&to=&groupBy=service|app|outcome` |

## 10. Settings & Instance (`/system/settings`, `/system/instance`)

- `GET/PATCH /system/settings` — mutable settings (doc 03 §2.13) with JSON-Schema validation.
- `GET /system/instance` — version, uptime, system DB status, Redis status, license string, feature flags.
- `GET /system/instance/metrics-summary` — last-hour req counts, error rate, p95 latency per service (UI dashboard; Prometheus remains the real metrics interface).

## 11. Config Export / Import (`/system/config`)

- `GET /system/config/export` → `{version, roles[], apps[] (no keys), services[] (no credentials), rateLimits[], settings}`.
- `POST /system/config/import?mode=merge|replace` with dry-run support (`?dryRun=true` returns the change plan). Imported services arrive `disabled` until credentials are supplied.

## 12. Jobs (`/system/jobs`)

`GET /system/jobs[/{id}]` — introspection/pruning job status: `{id, kind, serviceId, status, startedAt, finishedAt, error}`. Long-poll via `?waitSeconds=30` for UI progress.
