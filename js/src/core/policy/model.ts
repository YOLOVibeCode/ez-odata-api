/**
 * RBAC model (port of src/EzOdata.Core/Policy/PolicyModel.cs, spec 03 §2.4 / 08 §2).
 */

/** HTTP verb bitmask matching spec 03 §2.4. */
export const Verb = {
  None: 0,
  Get: 1,
  Post: 2,
  Put: 4,
  Patch: 8,
  Delete: 16,
  All: 1 | 2 | 4 | 8 | 16,
} as const;

/** A verb bitmask value (combine with bitwise OR). */
export type Verb = number;

export type RuleEffect = "allow" | "deny";

export type FieldAction = "deny" | "mask" | "writeOnly";

export interface FieldRule {
  readonly pattern: string;
  readonly action: FieldAction;
  readonly maskValue?: string;
}

/** One row of the RBAC matrix (spec 03 §2.4), resolved from storage. */
export interface AccessRule {
  readonly id?: number;
  /** undefined = wildcard across services. */
  readonly serviceName?: string;
  readonly resourcePattern: string;
  readonly verbs: Verb;
  readonly effect: RuleEffect;
  readonly priority: number;
  /** OData $filter expression with optional @identity.* claim references. */
  readonly rowFilter?: string;
  readonly fieldRules: readonly FieldRule[];
}

/** All rules of one active role. */
export interface RoleRuleSet {
  readonly roleId: number;
  readonly roleName: string;
  readonly bypassDataRules: boolean;
  readonly rules: readonly AccessRule[];
}

/** The authenticated principal of a request (spec 08 §2). */
export interface RequestIdentity {
  readonly appId?: number;
  readonly userId?: number;
  readonly email?: string;
  readonly isAdmin: boolean;
  readonly roleIds: readonly number[];
  /** Claims usable in row filters via @identity.* (spec 08 §5.4). Case-insensitive keys. */
  readonly claims: Readonly<Record<string, string>>;
  /**
   * Full-access bypass: PolicyEngine and SnapshotTrimmer short-circuit
   * immediately. Only ever set in development (gated by host config + a
   * non-production environment).
   */
  readonly bypass: boolean;
}

export const ANONYMOUS_IDENTITY: RequestIdentity = {
  isAdmin: false,
  roleIds: [],
  claims: {},
  bypass: false,
};

export const DEV_BYPASS_IDENTITY: RequestIdentity = {
  isAdmin: true,
  roleIds: [],
  claims: {},
  bypass: true,
};

/** Case-insensitive claim lookup (matches the C# OrdinalIgnoreCase dictionary). */
export function getClaim(identity: RequestIdentity, name: string): string | undefined {
  const lowered = name.toLowerCase();
  for (const [key, value] of Object.entries(identity.claims)) {
    if (key.toLowerCase() === lowered) return value;
  }
  return undefined;
}

/** Convenience builder for an access rule with sensible defaults. */
export function accessRule(rule: Partial<AccessRule> & { resourcePattern: string }): AccessRule {
  return {
    resourcePattern: rule.resourcePattern,
    verbs: rule.verbs ?? Verb.None,
    effect: rule.effect ?? "allow",
    priority: rule.priority ?? 0,
    fieldRules: rule.fieldRules ?? [],
    ...(rule.id !== undefined ? { id: rule.id } : {}),
    ...(rule.serviceName !== undefined ? { serviceName: rule.serviceName } : {}),
    ...(rule.rowFilter !== undefined ? { rowFilter: rule.rowFilter } : {}),
  };
}
