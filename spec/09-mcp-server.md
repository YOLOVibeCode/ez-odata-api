# 09 — MCP Server (AI / LLM Data Access)

The platform embeds an MCP (Model Context Protocol) server so AI clients — Claude Desktop, Cursor, ChatGPT connectors, custom agents — can query connected databases through the **same governed pipeline** as HTTP clients. The core promise: *AI never writes SQL; it calls structured, role-scoped tools, and every call is authorized and audited.*

Implementation: official C# SDK, `ModelContextProtocol.AspNetCore` (≥ 1.4), **Streamable HTTP transport** hosted in-process at `https://{host}/mcp`. A companion stdio bridge (`npx`-style launcher) is roadmap; HTTP-capable clients connect directly.

## 1. Requirements

| ID | Requirement |
|----|-------------|
| MCP-1 | Single MCP endpoint `/mcp`; authentication via `X-API-Key` header (same keys as HTTP). Anonymous → JSON-RPC auth error. |
| MCP-2 | Tool list is **derived from the caller's role**: tools exist only for services/verbs the role can access; tool descriptions embed only permitted tables/fields. |
| MCP-3 | Every tool call flows through the standard policy engine and audit pipeline (`category=mcp`). |
| MCP-4 | Tool inputs are structured JSON (validated by JSON Schema); filters use the REST filter grammar (doc 06 §5) compiled to the IR — never raw SQL. |
| MCP-5 | Result payloads are size-capped (default 200 rows / 256 KB per call) with explicit truncation signals and pagination cursors. |
| MCP-6 | Write tools exist only when the role has write verbs AND the service/instance setting `mcpAllowWrites=true` (default false). Deletes additionally require `confirm: true` argument. |
| MCP-7 | MCP can be disabled instance-wide (`Mcp.Enabled=false`) or per app (`apps.mcp_enabled` flag — added to doc 03 §2.8 as boolean, default true). |
| MCP-8 | `tools/list_changed` notifications fire when a schema refresh or role change alters the tool set of a connected session. |

## 2. Tool Catalog

Tools are namespaced per service: `{service}_{tool}`. For an identity with access to service `sales`:

| Tool | Purpose |
|------|---------|
| `sales_list_tables` | Enumerate visible tables with descriptions and row-count estimates |
| `sales_describe_table` | Columns, types, keys, relationships, allowed values — post-field-policy |
| `sales_query` | Filtered/sorted/projected/paged read, with optional related expansion and group/aggregate |
| `sales_get_record` | Single record by primary key |
| `sales_count` | Count with filter |
| `sales_insert` (gated) | Insert one or more records |
| `sales_update` (gated) | Update by key with partial record |
| `sales_delete` (gated) | Delete by key; requires `confirm:true` |

Plus instance-level tools (no service prefix):

| Tool | Purpose |
|------|---------|
| `list_services` | Services visible to this identity with descriptions |
| `explain_filter_syntax` | Returns the filter grammar cheat-sheet (static doc) — reduces malformed-filter retries by LLMs |

### 2.1 `query` input schema (normative)

```json
{
  "type": "object",
  "required": ["table"],
  "properties": {
    "table":   { "type": "string", "enum": ["<visible tables injected here>"] },
    "filter":  { "type": "string", "description": "e.g. (status='open') and (total>250). See explain_filter_syntax." },
    "fields":  { "type": "array", "items": { "type": "string" } },
    "order":   { "type": "string", "description": "e.g. 'created_at desc'" },
    "limit":   { "type": "integer", "maximum": 200, "default": 25 },
    "cursor":  { "type": "string" },
    "related": { "type": "string", "description": "e.g. 'customer,items(limit:5)'" },
    "group":   { "type": "array", "items": {"type": "string"} },
    "aggregate": { "type": "string", "description": "e.g. 'sum(total) as revenue'" }
  }
}
```

The `enum` of visible tables and per-table field lists in `describe_table` are regenerated per identity, so the model cannot even name hidden resources.

### 2.2 Result shape

Tool results return `structuredContent` (MCP structured output) plus a compact text rendering:

```json
{
  "rows": [ { "id": 1, "name": "Acme", "total": 1023.50 } ],
  "rowCount": 25,
  "truncated": true,
  "nextCursor": "eyJrIjoxMjV9",
  "schemaNote": "amounts in EUR; 'status' ∈ open|shipped|cancelled"
}
```

Errors are returned as MCP tool errors with the platform error code (`Forbidden.FieldDenied`, `Validation.BadFilter` with the parser's caret position, `RateLimited` with retry seconds) — precise machine-readable errors materially improve agent self-correction.

## 3. Resources & Prompts

- **Resources**: each service exposes `ez://{service}/schema` (the identity-trimmed schema as JSON) and `ez://{service}/openapi` so clients that prefer resource reading can fetch context cheaply.
- **Prompts**: one built-in prompt `analyze-data` (args: service, question) that instructs the model on the recommended tool sequence (`list_tables → describe_table → query` with small limits first). Optional; clients may ignore.

## 4. Session & Transport Semantics

- Streamable HTTP per current MCP spec; stateless mode supported (each call re-authenticated by header) — required for serverless-style clients; session mode (with `Mcp-Session-Id`) used when client maintains one.
- Tool list caching: server sets `listChanged` capability; on role/schema change, active sessions get `notifications/tools/list_changed`.
- Concurrency: tool calls share the HTTP rate-limit buckets of the underlying app key, plus optional `Mcp.MaxConcurrentCallsPerSession` (default 4).
- Timeouts: tool call hard timeout 60 s; long queries are cancelled and reported as `Upstream.Timeout`.

## 5. Configuration

```jsonc
"Mcp": {
  "Enabled": true,
  "AllowWrites": false,          // instance master switch, AND-ed with per-service option
  "MaxRowsPerCall": 200,
  "MaxResultBytes": 262144,
  "MaxConcurrentCallsPerSession": 4,
  "ExposeRowCountEstimates": true
}
```

## 6. Prompt-Injection & Safety Posture

- The platform treats the LLM as an **untrusted client**, identical to a browser: nothing relies on model cooperation. RBAC, row filters, field masks, rate limits, and audit are enforced server-side per call (MCP-3).
- Write tools are opt-in twice (instance + service) and delete requires an explicit `confirm` argument — friction against injected "delete everything" instructions.
- Tool descriptions never include data values, only schema; `schemaNote` derives from column comments (admin-controlled).
- Audit events for MCP include the tool name, arguments hash, normalized filter, row count — enabling forensic reconstruction of what a model saw (without storing the data itself).

## 7. Client Onboarding (documented flows)

1. **Claude Desktop / Cursor**: add server URL `https://host/mcp` with header `X-API-Key: ez_live_…` (both support custom headers for HTTP servers). The console's App detail page renders ready-to-paste JSON config for popular clients.
2. **Custom agent (C#/TS/Python)**: standard MCP client SDK against the same URL.
3. Connectivity check: `GET /mcp/health` returns `{ok, toolsAvailableForKey: n}` for fast diagnostics.
