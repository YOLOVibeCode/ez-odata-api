import { useEffect, useState } from "react";
import { services, type Service } from "../api";

// Minimal docs explorer: pick a service, view its OData $metadata link and the
// generated OpenAPI documents. A full try-it console is a fast follow.
export function Docs() {
  const [list, setList] = useState<Service[]>([]);
  const [selected, setSelected] = useState<string>("");

  useEffect(() => {
    services.list().then((s) => {
      const active = s.filter((x) => x.status === "Active");
      setList(active);
      if (active[0]) setSelected(active[0].name);
    }).catch(() => {});
  }, []);

  return (
    <div>
      <h1 className="page-title">API Docs</h1>
      {list.length === 0 ? (
        <div className="empty">No active services. Connect a database to generate API documentation.</div>
      ) : (
        <>
          <div className="card">
            <label>Service</label>
            <select value={selected} onChange={(e) => setSelected(e.target.value)} style={{ maxWidth: 320 }}>
              {list.map((s) => <option key={s.id} value={s.name}>{s.label}</option>)}
            </select>
          </div>
          {selected && (
            <div className="card">
              <h2 style={{ fontSize: 16, marginTop: 0 }}>Endpoints for <code>{selected}</code></h2>
              <table>
                <tbody>
                  <tr><td>OData service root</td><td><code>/api/odata/{selected}/</code></td></tr>
                  <tr><td>OData metadata (CSDL)</td><td><a href={`/api/odata/${selected}/$metadata`} target="_blank" rel="noreferrer"><code>/api/odata/{selected}/$metadata</code></a></td></tr>
                  <tr><td>OData OpenAPI</td><td><a href={`/api/odata/${selected}/openapi.json`} target="_blank" rel="noreferrer"><code>/api/odata/{selected}/openapi.json</code></a></td></tr>
                  <tr><td>REST tables</td><td><code>/api/rest/{selected}/_table</code></td></tr>
                  <tr><td>REST OpenAPI</td><td><a href={`/api/rest/${selected}/openapi.json`} target="_blank" rel="noreferrer"><code>/api/rest/{selected}/openapi.json</code></a></td></tr>
                  <tr><td>MCP endpoint</td><td><code>/mcp</code></td></tr>
                </tbody>
              </table>
              <p className="muted" style={{ fontSize: 13, marginTop: 12 }}>
                Documentation is identity-trimmed: it reflects exactly what your credentials can access. Pass an API key (<code>X-API-Key</code>) or your session to call these endpoints.
              </p>
            </div>
          )}
        </>
      )}
    </div>
  );
}
