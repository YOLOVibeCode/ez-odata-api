import { useEffect, useState } from "react";
import { services, type Connector, type Service } from "../api";
import { ErrorBanner, Field, Modal, StatusPill } from "../components";

export function Services() {
  const [list, setList] = useState<Service[]>([]);
  const [connectors, setConnectors] = useState<Connector[]>([]);
  const [showNew, setShowNew] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const reload = () => services.list().then(setList).catch((e) => setError(e.message));
  useEffect(() => {
    reload();
    services.connectors().then(setConnectors).catch(() => {});
    const t = setInterval(reload, 3000); // poll while introspecting
    return () => clearInterval(t);
  }, []);

  async function remove(s: Service) {
    if (!confirm(`Delete service "${s.label}"? This cannot be undone.`)) return;
    await services.remove(s.id);
    reload();
  }

  return (
    <div>
      <h1 className="page-title">
        Services
        <button className="primary" onClick={() => setShowNew(true)}>+ New service</button>
      </h1>
      <ErrorBanner error={error} />

      <div className="card">
        {list.length === 0 ? (
          <div className="empty">No services yet. Connect a database to generate an API.</div>
        ) : (
          <table>
            <thead><tr><th>Name</th><th>Connector</th><th>Status</th><th>Connection</th><th></th></tr></thead>
            <tbody>
              {list.map((s) => (
                <tr key={s.id}>
                  <td><strong>{s.label}</strong><br /><code>{s.name}</code></td>
                  <td><code>{s.connectorType}</code></td>
                  <td><StatusPill status={s.status} />{s.statusDetail && <div className="muted" style={{ fontSize: 12 }}>{s.statusDetail}</div>}</td>
                  <td className="muted">{s.connection}</td>
                  <td className="row">
                    <button onClick={() => services.refresh(s.id).then(reload)} disabled={s.status === "Disabled"}>Refresh</button>
                    <button className="danger" onClick={() => remove(s)}>Delete</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {showNew && <NewServiceModal connectors={connectors} onClose={() => setShowNew(false)} onCreated={() => { setShowNew(false); reload(); }} />}
    </div>
  );
}

function NewServiceModal({ connectors, onClose, onCreated }: { connectors: Connector[]; onClose: () => void; onCreated: () => void }) {
  const [name, setName] = useState("");
  const [label, setLabel] = useState("");
  const [connectorType, setConnectorType] = useState("postgresql");
  const [host, setHost] = useState("");
  const [port, setPort] = useState("5432");
  const [database, setDatabase] = useState("");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [filePath, setFilePath] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const isSqlite = connectorType === "sqlite";

  async function create() {
    setError(null);
    setBusy(true);
    try {
      const connection = isSqlite
        ? { filePath }
        : { host, port: Number(port), database, username, password, tls: { mode: "prefer" } };
      await services.create({ name, label: label || name, connectorType, connection });
      onCreated();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to create service.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal title="New service" onClose={onClose}>
      <ErrorBanner error={error} />
      <Field label="Connector">
        <select value={connectorType} onChange={(e) => setConnectorType(e.target.value)}>
          {connectors.map((c) => <option key={c.type} value={c.type}>{c.displayName}</option>)}
        </select>
      </Field>
      <div className="grid-2">
        <Field label="Service name (URL slug)"><input value={name} onChange={(e) => setName(e.target.value.toLowerCase())} placeholder="sales" /></Field>
        <Field label="Label"><input value={label} onChange={(e) => setLabel(e.target.value)} placeholder="Sales DB" /></Field>
      </div>
      {isSqlite ? (
        <Field label="File path"><input value={filePath} onChange={(e) => setFilePath(e.target.value)} placeholder="/data/app.db" /></Field>
      ) : (
        <>
          <div className="grid-2">
            <Field label="Host"><input value={host} onChange={(e) => setHost(e.target.value)} /></Field>
            <Field label="Port"><input value={port} onChange={(e) => setPort(e.target.value)} /></Field>
          </div>
          <Field label="Database"><input value={database} onChange={(e) => setDatabase(e.target.value)} /></Field>
          <div className="grid-2">
            <Field label="Username"><input value={username} onChange={(e) => setUsername(e.target.value)} /></Field>
            <Field label="Password"><input value={password} onChange={(e) => setPassword(e.target.value)} type="password" /></Field>
          </div>
        </>
      )}
      <div className="row" style={{ marginTop: 18, justifyContent: "flex-end" }}>
        <button onClick={onClose}>Cancel</button>
        <button className="primary" onClick={create} disabled={busy || !name}>{busy ? "Creating…" : "Create"}</button>
      </div>
    </Modal>
  );
}
