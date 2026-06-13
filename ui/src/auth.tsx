import { createContext, useContext, useEffect, useState, type ReactNode } from "react";
import { auth, setTokens, getStoredRefresh, type User } from "./api";

interface AuthState {
  user: User | null;
  loading: boolean;
  login: (email: string, password: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthState>(null!);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const stored = getStoredRefresh();
    if (!stored) {
      setLoading(false);
      return;
    }
    auth
      .refresh(stored)
      .then((res) => {
        setTokens(res.accessToken, res.refreshToken);
        setUser(res.user);
      })
      .catch(() => setTokens(null, null))
      .finally(() => setLoading(false));
  }, []);

  async function login(email: string, password: string) {
    const res = await auth.login(email, password);
    setTokens(res.accessToken, res.refreshToken);
    setUser(res.user);
  }

  function logout() {
    setTokens(null, null);
    setUser(null);
  }

  return <AuthContext.Provider value={{ user, loading, login, logout }}>{children}</AuthContext.Provider>;
}

export const useAuth = () => useContext(AuthContext);
