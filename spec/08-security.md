# 08 — Security: Authentication, RBAC, Rate Limiting, Audit, Secrets

Security is the product's core value proposition: **every data path (OData, REST, MCP) is authorized by one policy engine and recorded by one audit pipeline.**

## 1. Threat Model (summary)

| Threat | Mitigation |
|--------|-----------|
| SQL injection via filters/payloads | All values parameterized (NFR-3); identifiers resolved only from schema snapshot; filter grammars parsed to typed AST, never string-spliced |
| Credential theft of target DBs | AES-256-GCM envelope encryption at rest (§9); credentials never serialized to APIs/logs/audit |
| Stolen/leaked API key | Keys hashed at rest, revocable, expirable, rate-limited, prefix-identifiable; optional `requireUserSession` two-factor binding |
| Over-broad data exposure | Deny-by-default RBAC; field deny/mask; row filters; identity-trimmed metadata |
| Enumeration of hidden resources | Denied tables behave exactly like nonexistent ones (404, absent from metadata/docs/MCP) when role has zero access; 403 only where partial access exists |
| Brute-force login | Argon2id, lockout (§3.3), constant-time comparisons, uniform error messages |
| Cross-origin abuse | Default-deny CORS; per-app origin whitelists |
| DoS via expensive queries | Page-size clamps, expand depth/width limits, command timeouts, `$apply` limits, rate limiting |
| Replay/tamper of pagination cursors | HMAC-signed skiptokens |
| Privilege escalation via admin API | Admin role separation, last-admin protection, audit of all admin mutations |
| LLM prompt-injection exfiltration (MCP) | MCP inherits the key's role; no tool exists beyond role scope; row/field policies identical to HTTP (doc 09 §6) |

## 2. Identity Model

A request resolves to a `RequestIdentity`:

```
RequestIdentity {
  App?      (from API key)
  User?     (from JWT)
  Roles[]   (App.role ∪ User.roles)   — see §5.6 for combination
  Claims{}  (user id/email, app id, custom claims)  — usable in row filters
  AuthKind  (ApiKey | Jwt | ApiKeyPlusJwt | Anonymous)
}
```

Accepted credentials:

| Mechanism | Transport | Use |
|-----------|-----------|-----|
| API key | `X-API-Key` header (preferred) or `api_key` query param (discouraged; allowed for OData feed clients that can't set headers — logged with a warning flag) | Apps/data plane & MCP |
| JWT access token | `Authorization: Bearer` | Admin console users; named-user data access |
| Both together | key identifies App (role + limits + CORS), JWT identifies the human | Apps with `requireUserSession=true` |

Anonymous requests reach only: docs landing (if public), `/system/setup` (setup mode), health endpoints.

## 3. Authentication Details

### 3.1 JWT
- Access tokens: 15 min lifetime; claims `sub` (user id), `email`, `roles`, `typ=access`, `jti`. HS256 with ≥ 256-bit key from config, or RS256 with mounted keypair. Clock skew ±60 s.
- No sliding renewal: refresh flow only.

### 3.2 Passwords
- Argon2id, parameters: memory 64 MiB, iterations 3, parallelism 4 (configurable floor, never below OWASP minimums). Per-user random salt. Rehash-on-login when parameters change.
- Policy: min length 12, no composition rules, deny top-10k common passwords list.

### 3.3 Lockout
- 5 consecutive failures → 1 min lock; doubling to a 30 min cap; counter resets on success. Lockout responses identical to wrong-password (`401 Invalid credentials`) to avoid user enumeration; lock state visible to admins only.

### 3.4 Refresh tokens
- 30-day lifetime, single-use rotation: each refresh issues a new token and revokes the old; reuse of a rotated token revokes the whole family (theft detection) and audits `auth.refresh.reuse_detected`.

### 3.5 API keys
- 128-bit random, base62, stored SHA-256. Lookup via constant-time hash compare on indexed `key_hash`. In-memory cache (60 s TTL) with pub/sub invalidation so revocation is near-instant across replicas.

## 4. Authorization Pipeline

For every data operation (`identity`, `service`, `table`, `verb`, `fields`, `filter`):

```
1. Collect rules: role_service_access rows of all identity roles
   where (service_id = service OR service_id IS NULL)
     and resource_pattern matches table (case-insensitive glob)
2. If no rule matches at all              → 404 (resource hidden)
3. Partition matched rules by effect; take highest priority;
   tie → deny wins
4. Winning rule must include the verb     → else 403 Forbidden.Verb
5. Field policies of ALL matching allow rules are unioned (most restrictive):
   - deny: field removed from readable+writable+filterable+sortable sets
   - mask: readable as mask literal; not filterable/sortable; not writable
   - writeonly: writable; never readable/filterable/sortable
6. Validate request against field sets    → 403 Forbidden.FieldDenied (explicit $select/$filter/$orderby/write of a denied field)
7. Row filters: every matching allow rule's rowFilter is AND-ed
   (multiple roles → OR of each role's AND-chain; see 5.6)
8. Rewrite IR: trim Select, inject mask projections, AND row filter
   into Filter (reads) / Precondition (writes)
9. Emit audit context (matched rule ids, rewritten=true/false)
```

## 5. RBAC Semantics — Normative Details

### 5.1 Deny by default
No matching allow rule ⇒ no access. New services are invisible to every non-admin role until granted.

### 5.2 Priority & effect
`effect=deny` rules let admins carve exceptions out of broad allows (e.g. allow `*`, deny `salaries`, deny verbs ≠ GET on `audit_*`). Evaluation picks the single highest-priority matching rule for the allow/deny decision; **field policies union across all matching allow rules; row filters AND within a role**.

### 5.3 Wildcards
`resource_pattern` supports `*` and `?` globs. `service_id NULL` = all services (used for "read everything" analyst roles). Glob matching is against exposed table names.

### 5.4 Row filters
- Syntax: OData `$filter` grammar (doc 05 §4.3) — one grammar everywhere, including for REST and MCP requests.
- May reference identity claims: `owner_id eq @identity.userId`, `tenant eq @identity.claims.tenant`. Unresolvable claim at request time ⇒ rule evaluates to deny (fail closed) + audit `denied` with reason `ClaimMissing`.
- Applied to reads (WHERE), updates/deletes (predicate — you cannot mutate rows you cannot see), and inserts (inserted record must satisfy the filter, validated post-bind pre-execute; violation → 403 `Forbidden.RowFilter`).

### 5.5 Verb mapping
OData/REST methods map to the 5 verb bits; `$batch`/bulk items are authorized per item. `GET /$metadata` requires at least one readable table in the service (else 404).

### 5.6 Multiple roles
A user with several roles (or key+JWT both present): the identity may act under the **union** — evaluation runs per role, the request is allowed if any role allows it; the effective row filter is the OR of allowing roles' filters; effective field sets are the union of allowing roles' readable/writable sets. (Rationale: matches user expectation "more roles = more access"; deny rules still bind within each role's own evaluation.)

### 5.7 Admin bypass
`bypass_data_rules=true` skips steps 1–8 but still audits with flag `bypass=true`. The UI badges such roles prominently.

## 6. Rate Limiting

- Algorithm: token bucket per (policy, subject) with burst = `max_requests`, refill = `max_requests/window_seconds`.
- Store: in-memory per node by default; Redis (atomic Lua) when configured — required for multi-replica correctness.
- Resolution: all applicable policies are enforced simultaneously (app, role, user, service, instance) — the request must pass **all** buckets; the strictest failing policy is reported.
- Response: `429` + `Retry-After` + headers `RateLimit-Limit/Remaining/Reset` (IETF draft format). MCP tool calls receive a structured tool error with the same fields.
- Failure mode: if Redis is down, fall back to in-memory per-node limits and log loudly (availability over strictness; configurable to fail-closed).

## 7. CORS

- Global default: deny all cross-origin.
- Per-app `allowed_origins` (exact origins or `https://*.example.com` single-wildcard); preflight responses derive from the key presented in the request (preflight without key → instance-level default list from settings).
- `Access-Control-Expose-Headers` includes OData and RateLimit headers.

## 8. Audit Logging

- Every authenticated request to data/admin/MCP routes emits exactly one event (doc 03 §2.12), built in middleware, enriched by the policy engine and connector (duration, row count, outcome).
- Writes are buffered through a bounded channel (capacity 10k) and flushed in batches of ≤ 500 every ≤ 2 s by a background service. On overflow: drop-with-counter (metric `ez_audit_dropped_total`) — the data path is never blocked (NFR-8).
- What is recorded for data ops: service, table, verb, normalized filter text (literals replaced by `?` when `auditRedactFilters=true`), row count, duration, outcome, matched rule id. **Never row contents.**
- Auth events: login success/failure (with IP), lockouts, token refresh anomalies, key usage with revoked/expired keys.
- Tamper resistance (roadmap note): hash-chained audit batches are deferred to v2; v1 relies on DB-level protections.

## 9. Secrets & Encryption

- **Master key**: 256-bit, supplied via `EZODATA__ENCRYPTION__MASTERKEY` (base64) or a mounted file; optional AWS KMS/Azure Key Vault wrapping is roadmap. Startup fails if data exists but the key is wrong (probe value check).
- **Envelope**: each secret encrypted with a random DEK (AES-256-GCM, unique nonce); DEK wrapped by master key; format `v1:{wrapped_dek}:{nonce}:{ciphertext}:{tag}`. Allows master-key rotation by re-wrapping DEKs (`ez-admin rotate-master-key` CLI, doc 12 §7).
- Encrypted at rest: service connection specs, refresh token hashes (hashed not encrypted), SMTP/etc. future credentials.
- Logging hygiene: a serializer-level redaction filter strips fields named `password|secret|key|token|connection` from any logged object; CI test asserts this.

## 10. Transport & Headers

- TLS termination at the platform (Kestrel certs) or proxy; HSTS on when HTTPS. `X-Forwarded-*` honored only from configured trusted proxies.
- Security headers on UI/docs responses: `Content-Security-Policy` (self + inline-styles for docs explorer), `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`.
- Request size limits: 10 MB JSON bodies default (configurable per service for bulk loads); `$batch` 25 MB.

## 11. Security Acceptance Tests (must pass before release)

1. SQLi corpus (sqlmap-style payloads) through `$filter`, REST `filter`, field names, MCP args → zero executions, all 400/403.
2. A role with `deny ssn` can never observe `ssn` via: `$select`, omitted-select, `$filter=ssn eq …`, `$orderby=ssn`, `$expand` of a related table re-exposing it, REST `fields`, `_schema`, `$metadata`, OpenAPI, MCP `describe_table`, MCP query results.
3. Row-filtered role cannot read/update/delete out-of-scope rows by id probing (404, not 403, for invisible rows).
4. Revoked key fails within ≤ 2 s on all replicas.
5. Login brute-force locks; lock state not exposed to attacker.
6. Cursor/skiptoken tampering → 400, never altered query.
7. Master-key-encrypted blobs unreadable with wrong key; startup probe fails closed.
