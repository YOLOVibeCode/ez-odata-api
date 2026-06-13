import { useEffect, useState } from "react";
import { audit, type AuditEvent } from "../api";
import { ErrorBanner } from "../components";

export function Audit() {
  const [events, setEvents] = useState<AuditEvent[]>([]);
  const [category, setCategory] = useState("");
  const [outcome, setOutcome] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<number | null>(null);

  function load() {
    const params: Record<string, string> = { limit: "100" };
    if (category) params.category = category;
    if (outcome) params.outcome = outcome;
    audit.query(params).then((r) => setEvents(r.resource)).catch((e) => setError(e.message));
  }
  useEffect(() => { load(); }, [category, outcome]);

  return (
    <div>
      <h1 className="page-title">Audit</h1>
      <ErrorBanner error={error} />
      <div className="card row" style={{ gap: 16 }}>
        <div>
          <label>Category</label>
          <select value={category} onChange={(e) => setCategory(e.target.value)}>
            <option value="">all</option>
            {["data.read", "data.write", "auth", "admin", "mcp", "system"].map((c) => <option key={c}>{c}</option>)}
          </select>
        </div>
        <div>
          <label>Outcome</label>
          <select value={outcome} onChange={(e) => setOutcome(e.target.value)}>
            <option value="">all</option>
            {["ok", "denied", "error"].map((o) => <option key={o}>{o}</option>)}
          </select>
        </div>
        <div style={{ alignSelf: "flex-end" }}><button onClick={load}>Refresh</button></div>
      </div>
      <div className="card">
        <table>
          <thead><tr><th>Time</th><th>Category</th><th>Action</th><th>Outcome</th><th>Resource</th><th>ms</th></tr></thead>
          <tbody>
            {events.map((e) => (
              <>
                <tr key={e.id} style={{ cursor: "pointer" }} onClick={() => setExpanded(expanded === e.id ? null : e.id)}>
                  <td className="muted">{new Date(e.occurredAt).toLocaleTimeString()}</td>
                  <td><code>{e.category}</code></td>
                  <td>{e.action}</td>
                  <td><span className={`pill ${e.outcome}`}>{e.outcome}</span></td>
                  <td className="muted">{e.resource}</td>
                  <td className="muted">{e.durationMs ?? "—"}</td>
                </tr>
                {expanded === e.id && (
                  <tr key={`${e.id}-d`}><td colSpan={6}><pre style={{ margin: 0, fontSize: 12, overflow: "auto" }}>{JSON.stringify({ requestId: e.requestId, detail: JSON.parse(e.detailJson || "{}") }, null, 2)}</pre></td></tr>
                )}
              </>
            ))}
          </tbody>
        </table>
        {events.length === 0 && <div className="empty">No audit events match.</div>}
      </div>
    </div>
  );
}
