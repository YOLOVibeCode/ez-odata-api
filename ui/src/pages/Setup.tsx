import { useState } from "react";
import { auth } from "../api";
import { useAuth } from "../auth";
import { ErrorBanner, Field } from "../components";

export function Setup({ onComplete }: { onComplete: () => void }) {
  const { login } = useAuth();
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await auth.setup(email, displayName, password);
      await login(email, password);
      onComplete();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Setup failed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="center-screen">
      <form className="card auth-card" onSubmit={submit}>
        <h2 style={{ marginTop: 0 }}>Welcome — create your admin account</h2>
        <p className="muted">This is the first run. The account you create becomes the system administrator.</p>
        <ErrorBanner error={error} />
        <Field label="Email"><input value={email} onChange={(e) => setEmail(e.target.value)} type="email" required /></Field>
        <Field label="Display name"><input value={displayName} onChange={(e) => setDisplayName(e.target.value)} required /></Field>
        <Field label="Password (min 12 characters)"><input value={password} onChange={(e) => setPassword(e.target.value)} type="password" required minLength={12} /></Field>
        <button className="primary" style={{ width: "100%", marginTop: 16 }} disabled={busy}>
          {busy ? "Creating…" : "Create admin & continue"}
        </button>
      </form>
    </div>
  );
}
