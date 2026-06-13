# 10 — Admin Console (Web UI)

A React 19 + TypeScript SPA served from the host process at `/` (admin) and `/docs` (API explorer). It is a pure client of the admin API (doc 07) — UI parity rule applies. Visual direction: clean, dense-but-breathable dashboard aesthetic; dark/light themes; keyboard-friendly.

## 1. Stack & Conventions

- React 19, TypeScript strict, Vite build → emitted into `EzOdata.Host/wwwroot`.
- TanStack Router + TanStack Query (server state, optimistic updates with ETag/If-Match retry on 412).
- Tailwind CSS v4 + shadcn/ui component set; Recharts for dashboard charts; Monaco editor for filter/JSON editing.
- All times displayed in local tz with UTC tooltip. All lists virtualized over keyset pagination.
- Auth: JWT in memory, refresh via httpOnly cookie; idle logout after refresh expiry; 401 → login screen preserving deep link.
- i18n scaffolding (English only in v1). WCAG 2.1 AA targets: full keyboard nav, focus rings, aria labels on all interactive elements.

## 2. Information Architecture

```
/login, /setup
/dashboard
/services            /services/:id (tabs: Overview | Schema | Options | Health | API Docs)
/roles               /roles/:id (tabs: Rules | Field Policies | Simulator | Usage)
/apps                /apps/:id (tabs: Settings | API Keys | MCP Setup | CORS)
/users               /users/:id
/rate-limits
/audit
/settings            (instance settings, config export/import)
/docs                (public-ish API explorer, see doc 11 §4)
```

## 3. Screens (key behaviors)

### 3.1 Setup (`/setup`)
First-run wizard: create admin → optional "connect your first database" inline (same form as Services/New) → optional "create a read-only role + key" quick path. Goal: J1 journey under 5 minutes.

### 3.2 Dashboard
- Cards: requests/min, error rate, p95 latency, active services, top apps (from `/system/instance/metrics-summary` + `/system/audit/stats`).
- Time-series chart (1h/24h/7d) of requests by outcome; table of recent denied/error audit events with deep links.
- Health strip: system DB, Redis (if configured), each service's probe state.

### 3.3 Services
- List: name, connector icon, status pill (Pending/Introspecting/Active/Failed/Refreshing/Disabled), tables count, last refresh, drift warning badge.
- **New Service wizard**: 1) pick connector (cards from `/system/connectors`), 2) connection form generated from the connector's JSON Schema with **Test Connection** button (structured pass/fail with actionable detail), 3) options (schema globs with live preview of matched tables once test passes, read-only toggle, page sizes), 4) confirm → introspection progress (job long-poll) → success screen with copyable endpoint URLs + "open API docs".
- Detail/Schema tab: searchable table list → column grid (name, db type, EDM type, nullable, PK/FK badges); relationship graph (mini ER diagram); snapshot diff viewer ("3 columns added since previous snapshot").
- Danger zone: disable, delete (typed-name confirmation), rotate credentials.

### 3.4 Roles
- **Rule builder**: grid of rules (service, resource pattern with autocomplete from live schema, verb checkboxes, effect, priority, row filter). Row-filter editor = Monaco with OData-filter syntax highlighting + validation against the selected service schema (server-side parse on blur) + claim variable picker (`@identity.userId`…).
- **Field policies** sub-editor per rule: pattern, action (deny/mask/writeonly), mask literal.
- **Simulator tab** (backed by `/system/roles/{id}/simulate`): choose service/table/verb/fields + sample identity claims → verdict panel showing matched rule, effective row filter, denied/masked fields. One-click "save as policy test" exports the case as JSON (consumed by CI, doc 13 §5).
- Usage tab: bound apps/users with warnings before destructive edits.

### 3.5 Apps & Keys
- App form: name, role select, active, require-user-session, MCP toggle, CORS origins editor.
- Keys tab: create (name + optional expiry) → **one-time full-key reveal modal** with copy button and "I stored it" confirmation; list shows prefix, last used, expiry; revoke with confirm.
- **MCP Setup tab**: rendered, copy-pastable config snippets for Claude Desktop, Cursor, generic HTTP MCP clients, pre-filled with the instance URL and key prefix placeholder; live "test connection" button calling `/mcp/health` with a pasted key.
- **Quick start tab**: curl + JS + C# snippets for OData and REST calls using this app's key, pre-filled with a real table name from a service the role can read.

### 3.6 Users
List/detail with role assignment, activation, password reset (one-time token modal), session list with revoke.

### 3.7 Rate Limits
Policy table grouped by scope; "effective limits" inspector (pick an app → resolved chain). Inline 60-second sparkline of current consumption when Redis is present.

### 3.8 Audit
- Filter bar (category, action, outcome, service, app, user, date range, request id) → virtualized table → row expands to full detail JSON.
- Saved filters; NDJSON export button (streams `/system/audit/export`).
- Outcome color coding; "denied" events link to the role simulator pre-filled with the event's parameters ("why was this denied?").

### 3.9 Settings
Instance settings form (JSON-Schema-driven), config export (download) / import (upload → dry-run diff view → apply), license/version info.

## 4. API Docs Explorer (`/docs`)

Per doc 11: service picker → entity sets → operation playground. Reuses the app's session or an entered API key ("try it" calls are real and rate-limited/audited as such, with a visible banner).

## 5. UX Quality Bar (acceptance)

| ID | Requirement |
|----|-------------|
| UI-1 | Every admin API capability reachable in the UI (parity audit checklist in repo). |
| UI-2 | All destructive actions: explicit confirm; service delete requires typed name. |
| UI-3 | All forms: inline validation mirroring server JSON Schemas; server errors mapped to fields, never toast-only. |
| UI-4 | Empty states teach: each list's empty state explains the concept and links the next step (e.g. Roles empty state explains deny-by-default). |
| UI-5 | p75 route transition < 200 ms on cached data; skeletons over spinners. |
| UI-6 | Works without external network (no CDN fonts/scripts) — self-hosted deployments may be air-gapped. |
| UI-7 | Lighthouse a11y score ≥ 95 on all routes. |
