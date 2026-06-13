import { useEffect, useState } from "react";
import { apps, roles, type ApiKey, type App, type CreatedKey, type Role } from "../api";
import { ErrorBanner, Field, Modal } from "../components";

export function Apps() {
  const [list, setList] = useState<App[]>([]);
  const [roleList, setRoleList] = useState<Role[]>([]);
  const [showNew, setShowNew] = useState(false);
  const [managing, setManaging] = useState<App | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reload = () => apps.list().then(setList).catch((e) => setError(e.message));
  useEffect(() => { reload(); roles.list().then(setRoleList).catch(() => {}); }, []);

  return (
    <div>
      <h1 className="page-title">
        Apps &amp; Keys
        <button className="primary" onClick={() => setShowNew(true)} disabled={roleList.length === 0}>+ New app</button>
      </h1>
      <ErrorBanner error={error} />
      <div className="card">
        {list.length === 0 ? <div className="empty">No apps yet. Apps own API keys and are bound to one role.</div> : (
          <table>
            <thead><tr><th>Name</th><th>Role</th><th>Active</th><th>MCP</th><th></th></tr></thead>
            <tbody>
              {list.map((a) => (
                <tr key={a.id}>
                  <td><strong>{a.name}</strong></td>
                  <td><code>{a.roleName}</code></td>
                  <td>{a.isActive ? <span className="pill ok">yes</span> : <span className="pill disabled">no</span>}</td>
                  <td>{a.mcpEnabled ? "✓" : "—"}</td>
                  <td><button onClick={() => setManaging(a)}>Manage keys</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {showNew && <NewAppModal roles={roleList} onClose={() => setShowNew(false)} onCreated={() => { setShowNew(false); reload(); }} />}
      {managing && <KeysModal app={managing} onClose={() => setManaging(null)} />}
    </div>
  );
}

function NewAppModal({ roles: roleList, onClose, onCreated }: { roles: Role[]; onClose: () => void; onCreated: () => void }) {
  const [name, setName] = useState("");
  const [roleId, setRoleId] = useState(roleList[0]?.id ?? 0);
  const [mcpEnabled, setMcpEnabled] = useState(true);
  const [error, setError] = useState<string | null>(null);

  async function create() {
    setError(null);
    try {
      await apps.create({ name, roleId, isActive: true, requireUserSession: false, mcpEnabled });
      onCreated();
    } catch (e) { setError(e instanceof Error ? e.message : "Create failed."); }
  }

  return (
    <Modal title="New app" onClose={onClose}>
      <ErrorBanner error={error} />
      <Field label="Name"><input value={name} onChange={(e) => setName(e.target.value)} /></Field>
      <Field label="Role">
        <select value={roleId} onChange={(e) => setRoleId(Number(e.target.value))}>
          {roleList.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
        </select>
      </Field>
      <label className="row" style={{ marginTop: 12 }}><input type="checkbox" style={{ width: "auto" }} checked={mcpEnabled} onChange={(e) => setMcpEnabled(e.target.checked)} /> Enable MCP access</label>
      <div className="row" style={{ marginTop: 18, justifyContent: "flex-end" }}>
        <button onClick={onClose}>Cancel</button>
        <button className="primary" onClick={create} disabled={!name}>Create</button>
      </div>
    </Modal>
  );
}

function KeysModal({ app, onClose }: { app: App; onClose: () => void }) {
  const [keys, setKeys] = useState<ApiKey[]>([]);
  const [newName, setNewName] = useState("default");
  const [revealed, setRevealed] = useState<CreatedKey | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reload = () => apps.keys(app.id).then(setKeys).catch((e) => setError(e.message));
  useEffect(() => { reload(); }, []);

  async function createKey() {
    setError(null);
    try {
      const created = await apps.createKey(app.id, newName);
      setRevealed(created);
      reload();
    } catch (e) { setError(e instanceof Error ? e.message : "Failed."); }
  }

  async function revoke(k: ApiKey) {
    if (!confirm(`Revoke key "${k.name}"? Clients using it will stop working immediately.`)) return;
    await apps.revokeKey(app.id, k.id);
    reload();
  }

  return (
    <Modal title={`Keys — ${app.name}`} onClose={onClose}>
      <ErrorBanner error={error} />
      {revealed && (
        <div className="card" style={{ borderColor: "var(--accent)" }}>
          <strong>Copy this key now — it is shown only once.</strong>
          <div className="key-reveal">{revealed.key}</div>
          <button onClick={() => navigator.clipboard.writeText(revealed.key)}>Copy</button>
          <button className="primary" style={{ marginLeft: 8 }} onClick={() => setRevealed(null)}>I stored it</button>
        </div>
      )}
      <div className="row" style={{ margin: "12px 0" }}>
        <input value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="Key name" />
        <button className="primary" onClick={createKey}>Create key</button>
      </div>
      <table>
        <thead><tr><th>Prefix</th><th>Name</th><th>Last used</th><th>Status</th><th></th></tr></thead>
        <tbody>
          {keys.map((k) => (
            <tr key={k.id}>
              <td><code>{k.keyPrefix}…</code></td>
              <td>{k.name}</td>
              <td className="muted">{k.lastUsedAt ? new Date(k.lastUsedAt).toLocaleString() : "never"}</td>
              <td>{k.revokedAt ? <span className="pill failed">revoked</span> : <span className="pill ok">active</span>}</td>
              <td>{!k.revokedAt && <button className="danger" onClick={() => revoke(k)}>Revoke</button>}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {app.mcpEnabled && (
        <div className="card" style={{ marginTop: 16 }}>
          <strong>MCP client config</strong>
          <p className="muted" style={{ fontSize: 13 }}>Point Claude Desktop / Cursor at this server:</p>
          <div className="key-reveal" style={{ fontSize: 12 }}>{`{ "mcpServers": { "ez-odata": { "url": "${location.origin}/mcp", "headers": { "X-API-Key": "<your key>" } } } }`}</div>
        </div>
      )}
    </Modal>
  );
}
