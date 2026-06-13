import { useEffect, useState } from "react";
import { roles, services, type Role, type Service, type SimulateResult } from "../api";
import { ErrorBanner, Field, Modal } from "../components";

export function Roles() {
  const [list, setList] = useState<Role[]>([]);
  const [svcs, setSvcs] = useState<Service[]>([]);
  const [editing, setEditing] = useState<Role | null>(null);
  const [simulating, setSimulating] = useState<Role | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reload = () => roles.list().then(setList).catch((e) => setError(e.message));
  useEffect(() => { reload(); services.list().then(setSvcs).catch(() => {}); }, []);

  async function remove(r: Role) {
    if (!confirm(`Delete role "${r.name}"?`)) return;
    try { await roles.remove(r.id); reload(); }
    catch (e) { setError(e instanceof Error ? e.message : "Delete failed."); }
  }

  return (
    <div>
      <h1 className="page-title">
        Roles
        <button className="primary" onClick={() => setEditing({ id: 0, name: "", isActive: true, isAdmin: false, bypassDataRules: false, access: [], rowVersion: 0 })}>+ New role</button>
      </h1>
      <ErrorBanner error={error} />
      <div className="card">
        <table>
          <thead><tr><th>Name</th><th>Rules</th><th>Flags</th><th></th></tr></thead>
          <tbody>
            {list.map((r) => (
              <tr key={r.id}>
                <td><strong>{r.name}</strong>{!r.isActive && <span className="pill disabled" style={{ marginLeft: 8 }}>inactive</span>}<br /><span className="muted">{r.description}</span></td>
                <td>{r.access.length} rule(s)</td>
                <td>{r.bypassDataRules && <span className="pill failed">bypass</span>} {r.isAdmin && <span className="pill ok">admin</span>}</td>
                <td className="row">
                  <button onClick={() => setSimulating(r)}>Simulate</button>
                  <button onClick={() => setEditing(r)}>Edit</button>
                  <button className="danger" onClick={() => remove(r)}>Delete</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {editing && <RoleEditor role={editing} services={svcs} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(); }} />}
      {simulating && <Simulator role={simulating} services={svcs} onClose={() => setSimulating(null)} />}
    </div>
  );
}

function RoleEditor({ role, services: svcs, onClose, onSaved }: { role: Role; services: Service[]; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(role.name);
  const [description, setDescription] = useState(role.description ?? "");
  const [isActive, setIsActive] = useState(role.isActive);
  const [access, setAccess] = useState(role.access);
  const [error, setError] = useState<string | null>(null);

  function addRule() {
    setAccess([...access, { resourcePattern: "*", verbs: ["GET"], effect: "allow", priority: 0, fieldPolicies: [] }]);
  }
  function updateRule(i: number, patch: Partial<typeof access[0]>) {
    setAccess(access.map((r, idx) => (idx === i ? { ...r, ...patch } : r)));
  }
  function toggleVerb(i: number, verb: string) {
    const rule = access[i];
    const verbs = rule.verbs.includes(verb) ? rule.verbs.filter((v) => v !== verb) : [...rule.verbs, verb];
    updateRule(i, { verbs });
  }

  async function save() {
    setError(null);
    const body = { name, description, isActive, isAdmin: role.isAdmin, bypassDataRules: role.bypassDataRules, access };
    try {
      if (role.id === 0) await roles.create(body);
      else await roles.replace(role.id, body);
      onSaved();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Save failed.");
    }
  }

  return (
    <Modal title={role.id === 0 ? "New role" : `Edit ${role.name}`} onClose={onClose}>
      <ErrorBanner error={error} />
      <Field label="Name"><input value={name} onChange={(e) => setName(e.target.value)} /></Field>
      <Field label="Description"><input value={description} onChange={(e) => setDescription(e.target.value)} /></Field>
      <label className="row" style={{ marginTop: 12 }}><input type="checkbox" style={{ width: "auto" }} checked={isActive} onChange={(e) => setIsActive(e.target.checked)} /> Active</label>

      <h3 style={{ fontSize: 14, marginTop: 20 }}>Access rules</h3>
      {access.map((rule, i) => (
        <div key={i} className="card" style={{ padding: 12 }}>
          <div className="grid-2">
            <Field label="Service">
              <select value={rule.serviceName ?? ""} onChange={(e) => updateRule(i, { serviceName: e.target.value || null })}>
                <option value="">(any service)</option>
                {svcs.map((s) => <option key={s.id} value={s.name}>{s.name}</option>)}
              </select>
            </Field>
            <Field label="Resource pattern"><input value={rule.resourcePattern} onChange={(e) => updateRule(i, { resourcePattern: e.target.value })} /></Field>
          </div>
          <label>Verbs</label>
          <div className="row">
            {["GET", "POST", "PUT", "PATCH", "DELETE"].map((v) => (
              <label key={v} className="row" style={{ fontSize: 13 }}><input type="checkbox" style={{ width: "auto" }} checked={rule.verbs.includes(v)} onChange={() => toggleVerb(i, v)} /> {v}</label>
            ))}
          </div>
          <div className="grid-2">
            <Field label="Effect">
              <select value={rule.effect} onChange={(e) => updateRule(i, { effect: e.target.value })}><option value="allow">allow</option><option value="deny">deny</option></select>
            </Field>
            <Field label="Row filter (OData $filter)"><input value={rule.rowFilter ?? ""} onChange={(e) => updateRule(i, { rowFilter: e.target.value || null })} placeholder="country eq 'US'" /></Field>
          </div>
          <button className="danger" style={{ marginTop: 8 }} onClick={() => setAccess(access.filter((_, idx) => idx !== i))}>Remove rule</button>
        </div>
      ))}
      <button onClick={addRule}>+ Add rule</button>

      <div className="row" style={{ marginTop: 18, justifyContent: "flex-end" }}>
        <button onClick={onClose}>Cancel</button>
        <button className="primary" onClick={save} disabled={!name}>Save</button>
      </div>
    </Modal>
  );
}

function Simulator({ role, services: svcs, onClose }: { role: Role; services: Service[]; onClose: () => void }) {
  const [serviceName, setServiceName] = useState(svcs[0]?.name ?? "");
  const [table, setTable] = useState("");
  const [verb, setVerb] = useState("GET");
  const [result, setResult] = useState<SimulateResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function run() {
    setError(null);
    try {
      setResult(await roles.simulate(role.id, { serviceName, table, verb }));
    } catch (e) {
      setError(e instanceof Error ? e.message : "Simulation failed.");
    }
  }

  return (
    <Modal title={`Simulate: ${role.name}`} onClose={onClose}>
      <ErrorBanner error={error} />
      <p className="muted">Test whether this role can access a table — without running any query.</p>
      <div className="grid-2">
        <Field label="Service">
          <select value={serviceName} onChange={(e) => setServiceName(e.target.value)}>
            {svcs.map((s) => <option key={s.id} value={s.name}>{s.name}</option>)}
          </select>
        </Field>
        <Field label="Verb">
          <select value={verb} onChange={(e) => setVerb(e.target.value)}>{["GET", "POST", "PUT", "PATCH", "DELETE"].map((v) => <option key={v}>{v}</option>)}</select>
        </Field>
      </div>
      <Field label="Table"><input value={table} onChange={(e) => setTable(e.target.value)} placeholder="customers" /></Field>
      <button className="primary" style={{ marginTop: 12 }} onClick={run} disabled={!table}>Run simulation</button>

      {result && (
        <div className="card" style={{ marginTop: 16 }}>
          <div style={{ fontSize: 18, marginBottom: 8 }}>
            {result.allowed ? <span className="pill ok">ALLOWED</span> : <span className="pill failed">{result.hidden ? "HIDDEN (404)" : "DENIED (403)"}</span>}
          </div>
          {result.bypass && <p className="muted">Bypasses data rules (superuser).</p>}
          {result.effectiveRowFilter && <p>Row filter: <code>{result.effectiveRowFilter}</code></p>}
          {result.deniedFields.length > 0 && <p>Denied fields: {result.deniedFields.map((f) => <code key={f} style={{ marginRight: 4 }}>{f}</code>)}</p>}
          {Object.keys(result.maskedFields).length > 0 && <p>Masked: {Object.keys(result.maskedFields).map((f) => <code key={f} style={{ marginRight: 4 }}>{f}</code>)}</p>}
        </div>
      )}
    </Modal>
  );
}
