/**
 * Auth bridge unit tests — all three modes including the production guard.
 */

import { describe, expect, it, vi, afterEach, beforeEach } from "vitest";
import jwt from "jsonwebtoken";
import { createAuthMiddleware, hashRoleName, type EzRequest } from "../../../src/express/auth.js";
import type { Response } from "express";

// ---- helpers ----

function makeReq(overrides: Record<string, unknown> = {}): EzRequest {
  return {
    headers: {},
    ...overrides,
  } as unknown as EzRequest;
}

function makeRes(): Response & { _status?: number; _json?: unknown } {
  const res: Record<string, unknown> = {};
  res["status"] = vi.fn().mockReturnValue(res);
  res["json"] = vi.fn().mockReturnValue(res);
  return res as unknown as Response & { _status?: number; _json?: unknown };
}

const next = vi.fn();

beforeEach(() => next.mockClear());

afterEach(() => {
  // Restore original env
  delete process.env["NODE_ENV"];
});

// ---- mode: none ----

describe('auth mode "none"', () => {
  it("sets bypass identity in non-production", () => {
    process.env["NODE_ENV"] = "development";
    const mw = createAuthMiddleware({ mode: "none" });
    const req = makeReq();
    const res = makeRes();
    mw(req, res, next);
    expect(req.ezIdentity?.bypass).toBe(true);
    expect(next).toHaveBeenCalledOnce();
  });

  it("throws at startup in production", () => {
    process.env["NODE_ENV"] = "production";
    expect(() => createAuthMiddleware({ mode: "none" })).toThrow(/production/);
  });
});

// ---- mode: jwt ----

describe('auth mode "jwt"', () => {
  const SECRET = "test-secret-32-bytes-for-testing!!";

  function makeToken(payload: object, opts?: jwt.SignOptions): string {
    return jwt.sign(payload, SECRET, opts);
  }

  it("accepts a valid token and populates identity", () => {
    const token = makeToken({ sub: "42", email: "alice@example.com", roles: ["viewer", "editor"] });
    const mw = createAuthMiddleware({ mode: "jwt", secret: SECRET });
    const req = makeReq({ headers: { authorization: `Bearer ${token}` } });
    const res = makeRes();
    mw(req, res, next);
    expect(next).toHaveBeenCalledOnce();
    expect(req.ezIdentity!.email).toBe("alice@example.com");
    expect(req.ezIdentity!.roleIds).toHaveLength(2);
    expect(req.ezIdentity!.claims["email"]).toBe("alice@example.com");
  });

  it("returns 401 for missing Authorization header", () => {
    const mw = createAuthMiddleware({ mode: "jwt", secret: SECRET });
    const req = makeReq({ headers: {} });
    const res = makeRes();
    mw(req, res, next);
    expect(next).not.toHaveBeenCalled();
    expect((res.status as ReturnType<typeof vi.fn>).mock.calls[0]![0]).toBe(401);
  });

  it("returns 401 for expired token", () => {
    const token = makeToken({ sub: "1" }, { expiresIn: -10 });
    const mw = createAuthMiddleware({ mode: "jwt", secret: SECRET });
    const req = makeReq({ headers: { authorization: `Bearer ${token}` } });
    const res = makeRes();
    mw(req, res, next);
    expect(next).not.toHaveBeenCalled();
    expect((res.status as ReturnType<typeof vi.fn>).mock.calls[0]![0]).toBe(401);
  });

  it("returns 401 for invalid token", () => {
    const mw = createAuthMiddleware({ mode: "jwt", secret: SECRET });
    const req = makeReq({ headers: { authorization: "Bearer not.a.token" } });
    const res = makeRes();
    mw(req, res, next);
    expect(next).not.toHaveBeenCalled();
    expect((res.status as ReturnType<typeof vi.fn>).mock.calls[0]![0]).toBe(401);
  });

  it("checks issuer when configured", () => {
    const token = makeToken({ sub: "1" }, { issuer: "wrong-issuer" });
    const mw = createAuthMiddleware({ mode: "jwt", secret: SECRET, issuer: "my-app" });
    const req = makeReq({ headers: { authorization: `Bearer ${token}` } });
    const res = makeRes();
    mw(req, res, next);
    expect(next).not.toHaveBeenCalled();
    expect((res.status as ReturnType<typeof vi.fn>).mock.calls[0]![0]).toBe(401);
  });

  it("accepts a token with correct issuer", () => {
    const token = makeToken({ sub: "1" }, { issuer: "my-app" });
    const mw = createAuthMiddleware({ mode: "jwt", secret: SECRET, issuer: "my-app" });
    const req = makeReq({ headers: { authorization: `Bearer ${token}` } });
    const res = makeRes();
    mw(req, res, next);
    expect(next).toHaveBeenCalledOnce();
  });

  it("maps comma-separated roles claim", () => {
    const token = makeToken({ sub: "1", roles: "admin,viewer" });
    const mw = createAuthMiddleware({ mode: "jwt", secret: SECRET });
    const req = makeReq({ headers: { authorization: `Bearer ${token}` } });
    const res = makeRes();
    mw(req, res, next);
    expect(req.ezIdentity!.roleIds).toHaveLength(2);
  });
});

// ---- mode: hostRoles ----

describe('auth mode "hostRoles"', () => {
  it("populates identity from req.user", () => {
    const mw = createAuthMiddleware({ mode: "hostRoles" });
    const req = makeReq({
      user: { roles: ["viewer"], email: "bob@example.com", sub: "99" },
    });
    const res = makeRes();
    mw(req, res, next);
    expect(next).toHaveBeenCalledOnce();
    expect(req.ezIdentity!.email).toBe("bob@example.com");
    expect(req.ezIdentity!.roleIds).toHaveLength(1);
    expect(req.ezIdentity!.roleIds[0]).toBe(hashRoleName("viewer"));
  });

  it("produces anonymous identity when req.user is absent", () => {
    const mw = createAuthMiddleware({ mode: "hostRoles" });
    const req = makeReq({});
    const res = makeRes();
    mw(req, res, next);
    expect(next).toHaveBeenCalledOnce();
    expect(req.ezIdentity!.bypass).toBe(false);
    expect(req.ezIdentity!.roleIds).toHaveLength(0);
  });

  it("applies roleTransform to filter/rename roles", () => {
    const mw = createAuthMiddleware({
      mode: "hostRoles",
      roleTransform: (r) => (r.startsWith("app:") ? r.slice(4) : null),
    });
    const req = makeReq({ user: { roles: ["app:admin", "unrelated"] } });
    const res = makeRes();
    mw(req, res, next);
    expect(req.ezIdentity!.roleIds).toHaveLength(1);
    expect(req.ezIdentity!.roleIds[0]).toBe(hashRoleName("admin"));
  });
});

// ---- hashRoleName ----

describe("hashRoleName", () => {
  it("is deterministic", () => {
    expect(hashRoleName("viewer")).toBe(hashRoleName("viewer"));
  });

  it("differs for different names", () => {
    expect(hashRoleName("viewer")).not.toBe(hashRoleName("admin"));
  });

  it("never returns 0", () => {
    expect(hashRoleName("")).toBeGreaterThan(0);
  });
});
