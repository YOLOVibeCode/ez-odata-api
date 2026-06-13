// Thin typed client for the admin API. Access token kept in memory (spec 10 §1).

let accessToken: string | null = null;
let refreshToken: string | null = null;

export function setTokens(access: string | null, refresh: string | null) {
  accessToken = access;
  refreshToken = refresh;
  if (refresh) sessionStorage.setItem("ez_refresh", refresh);
  else sessionStorage.removeItem("ez_refresh");
}

export function getStoredRefresh(): string | null {
  return refreshToken ?? sessionStorage.getItem("ez_refresh");
}

export class ApiError extends Error {
  constructor(public status: number, public title: string, public detail?: string) {
    super(title);
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { "Content-Type": "application/json" };
  if (accessToken) headers["Authorization"] = `Bearer ${accessToken}`;

  const response = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (response.status === 204) return undefined as T;

  const text = await response.text();
  const data = text ? JSON.parse(text) : undefined;

  if (!response.ok) {
    throw new ApiError(response.status, data?.title ?? `Request failed (${response.status})`, data?.detail);
  }

  return data as T;
}

export const api = {
  get: <T>(path: string) => request<T>("GET", path),
  post: <T>(path: string, body?: unknown) => request<T>("POST", path, body),
  patch: <T>(path: string, body?: unknown) => request<T>("PATCH", path, body),
  put: <T>(path: string, body?: unknown) => request<T>("PUT", path, body),
  del: <T>(path: string) => request<T>("DELETE", path),
};

// ---- typed endpoints ----

export interface SetupStatus { required: boolean; }
export interface User { id: number; email: string; displayName: string; isSystemAdmin: boolean; roles: string[]; }
export interface AuthResponse { accessToken: string; expiresAt: string; refreshToken: string; user: User; }

export const auth = {
  setupStatus: () => api.get<SetupStatus>("/system/setup"),
  setup: (email: string, displayName: string, password: string) =>
    api.post<User>("/system/setup", { email, displayName, password }),
  login: (email: string, password: string) => api.post<AuthResponse>("/system/auth/login", { email, password }),
  refresh: (refreshToken: string) => api.post<AuthResponse>("/system/auth/refresh", { refreshToken }),
  me: () => api.get<User>("/system/auth/me"),
};

export interface Service {
  id: number; name: string; label: string; description?: string;
  connectorType: string; connection: string; status: string; statusDetail?: string;
  options: Record<string, unknown>; rowVersion: number;
}
export interface Connector { type: string; displayName: string; }

export const services = {
  list: () => api.get<Service[]>("/system/services"),
  get: (id: number) => api.get<Service>(`/system/services/${id}`),
  create: (body: unknown) => api.post<Service>("/system/services", body),
  remove: (id: number) => api.del(`/system/services/${id}`),
  test: (id: number) => api.post<{ ok: boolean; category: string; message: string }>(`/system/services/${id}/test`),
  refresh: (id: number) => api.post(`/system/services/${id}/refresh`),
  connectors: () => api.get<Connector[]>("/system/connectors"),
};

export interface Role {
  id: number; name: string; description?: string; isActive: boolean;
  isAdmin: boolean; bypassDataRules: boolean; access: AccessRule[]; rowVersion: number;
}
export interface AccessRule {
  id?: number; serviceName?: string | null; resourcePattern: string;
  verbs: string[]; effect: string; priority: number; rowFilter?: string | null;
  fieldPolicies: { fieldPattern: string; action: string; maskValue?: string | null }[];
}
export interface SimulateResult {
  allowed: boolean; hidden: boolean; denialCode?: string; bypass: boolean;
  deniedFields: string[]; maskedFields: Record<string, string>; effectiveRowFilter?: string;
}

export const roles = {
  list: () => api.get<Role[]>("/system/roles"),
  get: (id: number) => api.get<Role>(`/system/roles/${id}`),
  create: (body: unknown) => api.post<Role>("/system/roles", body),
  replace: (id: number, body: unknown) => api.put<Role>(`/system/roles/${id}`, body),
  remove: (id: number) => api.del(`/system/roles/${id}`),
  simulate: (id: number, body: unknown) => api.post<SimulateResult>(`/system/roles/${id}/simulate`, body),
};

export interface App {
  id: number; name: string; description?: string; roleId: number; roleName: string;
  isActive: boolean; allowedOrigins: string[]; requireUserSession: boolean; mcpEnabled: boolean;
}
export interface ApiKey {
  id: number; keyPrefix: string; name: string; expiresAt?: string;
  revokedAt?: string; lastUsedAt?: string; createdAt: string;
}
export interface CreatedKey { id: number; key: string; keyPrefix: string; name: string; }

export const apps = {
  list: () => api.get<App[]>("/system/apps"),
  create: (body: unknown) => api.post<App>("/system/apps", body),
  update: (id: number, body: unknown) => api.patch<App>(`/system/apps/${id}`, body),
  keys: (id: number) => api.get<ApiKey[]>(`/system/apps/${id}/keys`),
  createKey: (id: number, name: string) => api.post<CreatedKey>(`/system/apps/${id}/keys`, { name }),
  revokeKey: (appId: number, keyId: number) => api.del(`/system/apps/${appId}/keys/${keyId}`),
};

export interface AuditEvent {
  id: number; occurredAt: string; requestId: string; category: string; action: string;
  outcome: string; serviceId?: number; appId?: number; userId?: number; resource?: string;
  detailJson: string; durationMs?: number;
}

export const audit = {
  query: (params: Record<string, string>) => {
    const qs = new URLSearchParams(params).toString();
    return api.get<{ resource: AuditEvent[]; meta: { next?: number } }>(`/system/audit?${qs}`);
  },
};

export interface InstanceInfo {
  version: string; uptimeSeconds: number;
  systemDatabase: { provider: string; connected: boolean };
  features: { connectors: string[] };
}
export const instance = {
  info: () => api.get<InstanceInfo>("/system/instance"),
  metrics: () => api.get<{ windowMinutes: number; requests: number; errors: number; denied: number; avgDurationMs: number }>("/system/instance/metrics-summary"),
};
