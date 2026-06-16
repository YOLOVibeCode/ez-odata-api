/**
 * Auth bridge (port of EzOdataBuilder auth modes, spec 15 §4):
 * factory that returns an Express middleware producing a RequestIdentity.
 *
 * Three modes:
 *   - "hostRoles" — map req.user roles/claims to ez roles; pick up sub/email for @identity.*
 *   - "jwt"       — verify a Bearer token; extract roles and claims from JWT payload
 *   - "none"      — full bypass; gated to NODE_ENV !== "production" (throws at startup otherwise)
 */

import type { Request, Response, NextFunction } from "express";
import jwt from "jsonwebtoken";
import {
  ANONYMOUS_IDENTITY,
  DEV_BYPASS_IDENTITY,
  type RequestIdentity,
} from "../core/policy/model.js";

// ---- Mode configurations ----

export interface HostRolesAuthConfig {
  readonly mode: "hostRoles";
  /**
   * Claim type that holds role names in req.user.
   * Defaults to "roles" / standard role claim.
   */
  readonly roleClaimType?: string;
  /** Optional transform applied to each raw role claim value → ez role name (null = skip). */
  readonly roleTransform?: (raw: string) => string | null;
}

export interface JwtAuthConfig {
  readonly mode: "jwt";
  readonly secret: string;
  readonly issuer?: string;
  readonly audience?: string;
  /** Claim that holds role names in the JWT payload. Defaults to "roles". */
  readonly rolesClaim?: string;
}

export interface NoneAuthConfig {
  readonly mode: "none";
}

export type AuthConfig = HostRolesAuthConfig | JwtAuthConfig | NoneAuthConfig;

/**
 * Identity-enriched request: after `createAuthMiddleware` runs, `req.ezIdentity`
 * is guaranteed to be set.
 */
export interface EzRequest extends Request {
  ezIdentity?: RequestIdentity;
}

export type EzAuthMiddleware = (req: EzRequest, res: Response, next: NextFunction) => void;

/**
 * Creates an Express middleware that populates `req.ezIdentity` from the
 * configured auth mode.
 *
 * @throws {Error} if mode is "none" and NODE_ENV is "production".
 */
export function createAuthMiddleware(config: AuthConfig): EzAuthMiddleware {
  switch (config.mode) {
    case "none":
      return createNoneMiddleware();
    case "hostRoles":
      return createHostRolesMiddleware(config);
    case "jwt":
      return createJwtMiddleware(config);
  }
}

// ---- none mode ----

function createNoneMiddleware(): EzAuthMiddleware {
  if (process.env["NODE_ENV"] === "production") {
    throw new Error(
      'auth mode "none" is not allowed in production (NODE_ENV=production). ' +
        'Use "hostRoles" or "jwt" in production environments.',
    );
  }

  return (req: EzRequest, _res: Response, next: NextFunction): void => {
    req.ezIdentity = DEV_BYPASS_IDENTITY;
    next();
  };
}

// ---- hostRoles mode ----

function createHostRolesMiddleware(config: HostRolesAuthConfig): EzAuthMiddleware {
  const roleClaimType = config.roleClaimType ?? "roles";
  const transform = config.roleTransform ?? ((r) => r);

  return (req: EzRequest, _res: Response, next: NextFunction): void => {
    const user = (req as unknown as Record<string, unknown>)["user"] as
      | Record<string, unknown>
      | undefined;

    if (user === undefined) {
      req.ezIdentity = ANONYMOUS_IDENTITY;
      next();
      return;
    }

    const rawRoles = extractClaim(user, roleClaimType);
    const roleNames = rawRoles
      .map((r) => transform(r))
      .filter((r): r is string => r !== null && r !== undefined);

    const email = extractSingle(user, "email") ?? extractSingle(user, "upn");
    const sub = extractSingle(user, "sub") ?? extractSingle(user, "oid") ?? extractSingle(user, "userId");
    const parsedUserId = sub !== undefined ? parseInt(sub, 10) : NaN;

    const claims: Record<string, string> = {};
    for (const [k, v] of Object.entries(user)) {
      if (typeof v === "string") claims[k.toLowerCase()] = v;
    }

    req.ezIdentity = {
      ...(email !== undefined ? { email } : {}),
      isAdmin: false,
      roleIds: roleNames.map((name) => hashRoleName(name)),
      claims,
      bypass: false,
      ...(!isNaN(parsedUserId) && parsedUserId !== 0 ? { userId: parsedUserId } : {}),
    };
    next();
  };
}

// ---- jwt mode ----

function createJwtMiddleware(config: JwtAuthConfig): EzAuthMiddleware {
  const { secret, issuer, audience } = config;
  const rolesClaim = config.rolesClaim ?? "roles";

  return (req: EzRequest, res: Response, next: NextFunction): void => {
    const authHeader = req.headers["authorization"];
    if (authHeader === undefined || !authHeader.startsWith("Bearer ")) {
      res.status(401).json({ error: "Bearer token required." });
      return;
    }

    const token = authHeader.slice(7);
    try {
      const payload = jwt.verify(token, secret, {
        ...(issuer !== undefined ? { issuer } : {}),
        ...(audience !== undefined ? { audience } : {}),
      }) as Record<string, unknown>;

      const rawRoles = extractClaim(payload, rolesClaim);
      const email = extractSingle(payload, "email");
      const sub = extractSingle(payload, "sub");
      const parsedUserId = sub !== undefined ? parseInt(sub, 10) : NaN;

      const claims: Record<string, string> = {};
      for (const [k, v] of Object.entries(payload)) {
        if (typeof v === "string") claims[k.toLowerCase()] = v;
      }

      req.ezIdentity = {
        ...(email !== undefined ? { email } : {}),
        isAdmin: false,
        roleIds: rawRoles.map((name) => hashRoleName(name)),
        claims,
        bypass: false,
        ...(!isNaN(parsedUserId) && parsedUserId !== 0 ? { userId: parsedUserId } : {}),
      };
      next();
    } catch (err) {
      if (err instanceof jwt.TokenExpiredError) {
        res.status(401).json({ error: "Token expired." });
      } else if (err instanceof jwt.JsonWebTokenError) {
        res.status(401).json({ error: "Invalid token." });
      } else {
        res.status(500).json({ error: "Authentication error." });
      }
    }
  };
}

// ---- helpers ----

function extractClaim(obj: Record<string, unknown>, key: string): string[] {
  const value = obj[key];
  if (Array.isArray(value)) return value.filter((v): v is string => typeof v === "string");
  if (typeof value === "string") return value.split(",").map((s) => s.trim()).filter((s) => s.length > 0);
  return [];
}

function extractSingle(obj: Record<string, unknown>, key: string): string | undefined {
  const v = obj[key];
  return typeof v === "string" ? v : undefined;
}

/**
 * Deterministic numeric ID from a role name for `RequestIdentity.roleIds`.
 * Must be consistent with how roles are stored in `RoleRuleSet.roleId`.
 */
export function hashRoleName(name: string): number {
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = (hash * 31 + name.charCodeAt(i)) >>> 0;
  }
  return hash === 0 ? 1 : hash;
}
