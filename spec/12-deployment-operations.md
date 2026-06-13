# 12 — Deployment, Configuration & Operations

## 1. Requirements

| ID | Requirement |
|----|-------------|
| OPS-1 | Single multi-arch (amd64/arm64) container image, distroless-style base (`mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`), non-root, read-only filesystem compatible. |
| OPS-2 | Docker Compose quick start (platform + Postgres system DB + sample database) working in one command. |
| OPS-3 | First-run setup wizard (doc 03 §4); zero manual DB steps (auto-migrate with multi-replica lock). |
| OPS-4 | Helm chart with HPA-ready stateless deployment, external system DB, optional Redis. |
| OPS-5 | Health endpoints: `/healthz/live` (process), `/healthz/ready` (system DB reachable, migrations applied, encryption key valid). |
| OPS-6 | Observability: structured JSON logs, OpenTelemetry traces (OTLP), Prometheus metrics at `/metrics` (auth-gated or separate port). |
| OPS-7 | Graceful shutdown: drain in-flight requests (30 s), flush audit channel, dispose pools. |
| OPS-8 | Documented backup/restore = system DB backup + master key; target databases are never written except via user-requested API writes. |

## 2. Distribution Channels

1. **Docker image**: `ghcr.io/<org>/ez-odata-api:{semver|latest}`; SBOM + provenance attestation; image signing (cosign).
2. **docker-compose.yml** (repo `deploy/compose/`): profiles `minimal` (SQLite system DB, single container) and `full` (Postgres system DB, Redis, sample Northwind PG database + seeded demo service/role/key).
3. **Helm** (`deploy/helm/`): values for replicas, resources, existing-secret references (system DB DSN, master key, JWT key), ingress, Redis toggle, ServiceMonitor.
4. **Bare .NET**: `dotnet EzOdata.Host.dll` for Windows/IIS-adjacent shops; documented but not packaged in v1.

## 3. Topologies

| Topology | Notes |
|----------|-------|
| Single node (default) | SQLite or PG system DB; in-memory caches/limits |
| HA (n replicas) | PG system DB + Redis (required for rate limits + cache invalidation pub/sub); sticky sessions NOT required (stateless JWT/key auth; MCP stateless mode or session affinity optional) |
| Air-gapped | Fully functional offline (UI has no CDN deps, UI-6); image mirroring instructions |

## 4. Configuration Reference (env prefix `EZODATA__`)

```jsonc
{
  "SystemDatabase": { "Provider": "postgres|sqlite", "ConnectionString": "..." },
  "Encryption": { "MasterKey": "<base64-32B>", "MasterKeyFile": "/run/secrets/ez-master-key" },
  "Auth": {
    "Jwt": { "Algorithm": "HS256|RS256", "SigningKey": "...", "RsaPrivateKeyFile": null,
             "AccessTokenMinutes": 15, "RefreshTokenDays": 30 },
    "AllowApiKeyInQuery": true,
    "Lockout": { "Threshold": 5, "BaseSeconds": 60, "MaxSeconds": 1800 }
  },
  "Redis": { "ConnectionString": null },          // null = single-node mode
  "RateLimiting": { "Enabled": true, "InstanceDefault": { "WindowSeconds": 60, "MaxRequests": 0 } }, // 0 = unlimited
  "Cors": { "DefaultAllowedOrigins": [] },
  "Http": { "TrustedProxies": ["10.0.0.0/8"], "MaxBodyBytes": 10485760, "BatchMaxBodyBytes": 26214400 },
  "Mcp": { /* doc 09 §5 */ },
  "Docs": { "PublicDocs": false, "PublicMetadata": false },
  "Audit": { "RetentionDays": 90, "RedactFilters": false, "ChannelCapacity": 10000 },
  "Telemetry": { "OtlpEndpoint": null, "ServiceName": "ez-odata-api", "PrometheusEnabled": true },
  "Limits": { "DefaultCommandTimeoutSeconds": 30, "MaxExpandDepth": 3, "MaxExpandWidth": 10, "MaxBatchRequests": 100 }
}
```

Validation: the host validates the full config at startup (fail fast with precise messages); `--validate-config` CLI flag for CI.

## 5. Reverse Proxy / TLS

- Kestrel serves HTTPS directly (cert paths in config) or sits behind nginx/Traefik/ALB. `X-Forwarded-For/Proto/Host` honored only from `Http.TrustedProxies` (affects absolute OData links, doc 05 §8).
- WebSocket not required (MCP uses streamable HTTP); SSE responses (MCP notifications) require proxy buffering off — documented snippets for nginx/Traefik.

## 6. Observability Details

### Metrics (Prometheus names)
- `ez_http_requests_total{route,method,status,service}`
- `ez_http_request_duration_seconds` (histogram, exemplars on)
- `ez_db_query_duration_seconds{service,operation}`
- `ez_db_pool_connections{service,state}`
- `ez_policy_denials_total{service,reason}`
- `ez_rate_limited_total{scope}`
- `ez_audit_dropped_total`, `ez_audit_queue_depth`
- `ez_schema_refresh_duration_seconds{service}`, `ez_schema_drift_detected_total`
- `ez_mcp_tool_calls_total{tool,outcome}`

### Traces
One span per request: `auth → policy → compile → db.execute (with db.system, db.name, sanitized db.statement) → serialize`. MCP tool calls create child spans. W3C tracecontext propagation inbound.

### Logs
JSON lines: `ts, level, msg, requestId, traceId, identity{appId,userId}, service, route, status, durationMs`. Secret-redaction filter (doc 08 §9). Log level per category at runtime via `/system/settings`.

## 7. Operational CLI (`ez-admin`, bundled in image)

| Command | Purpose |
|---------|---------|
| `ez-admin migrate` | Apply system DB migrations explicitly (CI/CD gating) |
| `ez-admin create-admin --email ...` | Break-glass admin creation (console access lost) |
| `ez-admin rotate-master-key --new-key ...` | Re-wrap DEKs (doc 08 §9) |
| `ez-admin export-config / import-config` | Same as `/system/config` endpoints |
| `ez-admin validate-config` | Startup validation only |
| `ez-admin prune-audit --before 2026-01-01` | Manual audit pruning |

## 8. Upgrade & Compatibility Policy

- SemVer for the platform. Minor releases: zero-downtime rolling upgrade (migrations are always backward-compatible one minor back — expand/contract pattern).
- Generated API contract stability: any change to default serialization or query semantics of generated endpoints is a **major** version event.
- The image tag, `/system/instance.version`, and OpenAPI `x-ez-generator-version` always agree.

## 9. Backup / DR

- Back up: system DB + master key (separately!). Schema snapshots are re-derivable; audit is not.
- Restore drill documented: fresh deploy + restore DB + same master key → services reconnect automatically (status probe re-validates connections at startup).
- RPO target = system DB backup cadence; RTO < 15 min with compose/Helm assets.
