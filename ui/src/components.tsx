import { type ReactNode } from "react";

export function StatusPill({ status }: { status: string }) {
  return <span className={`pill ${status.toLowerCase()}`}>{status}</span>;
}

export function ErrorBanner({ error }: { error: string | null }) {
  if (!error) return null;
  return <div className="error-banner" role="alert">{error}</div>;
}

export function Modal({ title, children, onClose }: { title: string; children: ReactNode; onClose: () => void }) {
  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <div className="spread" style={{ marginBottom: 16 }}>
          <h2 style={{ margin: 0, fontSize: 18 }}>{title}</h2>
          <button onClick={onClose} aria-label="Close">✕</button>
        </div>
        {children}
      </div>
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}

export function Field({ label, children }: { label: string; children: ReactNode }) {
  // Wrap the control in the label so it is implicitly associated (a11y + testability).
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      {children}
    </label>
  );
}
