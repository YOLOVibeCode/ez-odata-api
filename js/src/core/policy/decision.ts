import type { FilterNode } from "../query.js";

/**
 * Outcome of authorization for one (identity, service, table, verb) - spec 08 §4
 * (port of src/EzOdata.Core/Policy/PolicyDecision.cs).
 */
export interface PolicyDecision {
  readonly allowed: boolean;
  /** True => respond 404, not 403: the resource is invisible to this identity (spec 08 §5.1). */
  readonly hidden: boolean;
  readonly denialCode?: string;
  readonly denialMessage?: string;
  /** True => superuser data access; no rewriting applied (audited with bypass flag). */
  readonly bypass: boolean;
  /** Fields that must never be readable, filterable, sortable, or writable. */
  readonly deniedFields: ReadonlySet<string>;
  /** Field -> mask literal; returned masked, not filterable/sortable/writable. */
  readonly maskedFields: ReadonlyMap<string, string>;
  /** Accepted on writes, never returned/filterable. */
  readonly writeOnlyFields: ReadonlySet<string>;
  /** Combined row filter (already claim-substituted and parsed), AND-ed into every operation. */
  readonly rowFilter?: FilterNode;
}

export function denyDecision(code: string, message: string, hidden = false): PolicyDecision {
  return {
    allowed: false,
    hidden,
    denialCode: code,
    denialMessage: message,
    bypass: false,
    deniedFields: new Set(),
    maskedFields: new Map(),
    writeOnlyFields: new Set(),
  };
}

export const FULL_ACCESS: PolicyDecision = {
  allowed: true,
  hidden: false,
  bypass: true,
  deniedFields: new Set(),
  maskedFields: new Map(),
  writeOnlyFields: new Set(),
};
