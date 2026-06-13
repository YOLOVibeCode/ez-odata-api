import { useEffect, useState } from "react";
import { instance, services, type Service } from "../api";
import { StatusPill } from "../components";

export function Dashboard() {
  const [info, setInfo] = useState<Awaited<ReturnType<typeof instance.info>> | null>(null);
  const [metrics, setMetrics] = useState<Awaited<ReturnType<typeof instance.metrics>> | null>(null);
  const [svcs, setSvcs] = useState<Service[]>([]);

  useEffect(() => {
    instance.info().then(setInfo).catch(() => {});
    instance.metrics().then(setMetrics).catch(() => {});
    services.list().then(setSvcs).catch(() => {});
  }, []);

  return (
    <div>
      <h1 className="page-title">Dashboard</h1>
      <div className="cards">
        <div className="card"><div className="stat">{metrics?.requests ?? "—"}</div><div className="stat-label">Requests (last hour)</div></div>
        <div className="card"><div className="stat">{metrics?.errors ?? "—"}</div><div className="stat-label">Errors</div></div>
        <div className="card"><div className="stat">{metrics?.denied ?? "—"}</div><div className="stat-label">Denied</div></div>
        <div className="card"><div className="stat">{metrics?.avgDurationMs ?? "—"} ms</div><div className="stat-label">Avg duration</div></div>
        <div className="card"><div className="stat">{svcs.filter((s) => s.status === "Active").length}/{svcs.length}</div><div className="stat-label">Active services</div></div>
      </div>

      <div className="card">
        <h2 style={{ marginTop: 0, fontSize: 16 }}>Services</h2>
        {svcs.length === 0 ? <div className="muted">No services yet.</div> : (
          <table>
            <thead><tr><th>Name</th><th>Connector</th><th>Status</th><th>Connection</th></tr></thead>
            <tbody>
              {svcs.map((s) => (
                <tr key={s.id}>
                  <td>{s.label}</td>
                  <td><code>{s.connectorType}</code></td>
                  <td><StatusPill status={s.status} /></td>
                  <td className="muted">{s.connection}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {info && (
        <div className="card muted" style={{ fontSize: 13 }}>
          v{info.version} · system DB: {info.systemDatabase.provider?.split(".").pop()} ({info.systemDatabase.connected ? "connected" : "down"}) ·
          connectors: {info.features.connectors.join(", ")}
        </div>
      )}
    </div>
  );
}
