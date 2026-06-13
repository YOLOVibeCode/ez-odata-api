import { useEffect, useState } from "react";
import { Navigate, NavLink, Route, Routes, useNavigate } from "react-router-dom";
import { useAuth } from "./auth";
import { auth } from "./api";
import { Setup } from "./pages/Setup";
import { Login } from "./pages/Login";
import { Dashboard } from "./pages/Dashboard";
import { Services } from "./pages/Services";
import { Roles } from "./pages/Roles";
import { Apps } from "./pages/Apps";
import { Audit } from "./pages/Audit";
import { Docs } from "./pages/Docs";

export function App() {
  const { user, loading } = useAuth();
  const [setupRequired, setSetupRequired] = useState<boolean | null>(null);

  useEffect(() => {
    auth.setupStatus().then((s) => setSetupRequired(s.required)).catch(() => setSetupRequired(false));
  }, []);

  if (loading || setupRequired === null) {
    return <div className="center-screen muted">Loading…</div>;
  }

  if (setupRequired) {
    return (
      <Routes>
        <Route path="/setup" element={<Setup onComplete={() => setSetupRequired(false)} />} />
        <Route path="*" element={<Navigate to="/setup" replace />} />
      </Routes>
    );
  }

  if (!user) {
    return (
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route path="*" element={<Navigate to="/login" replace />} />
      </Routes>
    );
  }

  return <Shell />;
}

function Shell() {
  const { user, logout } = useAuth();
  const navigate = useNavigate();

  return (
    <div className="layout">
      <aside className="sidebar">
        <h1>ez-odata-api</h1>
        <nav className="nav">
          <NavLink to="/dashboard">Dashboard</NavLink>
          <NavLink to="/services">Services</NavLink>
          <NavLink to="/roles">Roles</NavLink>
          <NavLink to="/apps">Apps &amp; Keys</NavLink>
          <NavLink to="/audit">Audit</NavLink>
          <NavLink to="/docs">API Docs</NavLink>
        </nav>
        <div style={{ position: "absolute", bottom: 16, left: 16, right: 16 }}>
          <div className="muted" style={{ fontSize: 12, marginBottom: 6 }}>{user!.email}</div>
          <button style={{ width: "100%" }} onClick={() => { logout(); navigate("/login"); }}>Sign out</button>
        </div>
      </aside>
      <main className="main">
        <Routes>
          <Route path="/dashboard" element={<Dashboard />} />
          <Route path="/services" element={<Services />} />
          <Route path="/roles" element={<Roles />} />
          <Route path="/apps" element={<Apps />} />
          <Route path="/audit" element={<Audit />} />
          <Route path="/docs" element={<Docs />} />
          <Route path="*" element={<Navigate to="/dashboard" replace />} />
        </Routes>
      </main>
    </div>
  );
}
