import type { FilterNode } from "../query.js";
import type { RequestIdentity, RoleRuleSet } from "./model.js";

/**
 * Segregated role interfaces for the policy layer (ISP, spec 02 §3.1). Each is a
 * single-responsibility seam so read paths never depend on write-side concerns.
 */

/**
 * Parses a role's row-filter expression (OData $filter grammar) to IR for one
 * table. Throws {@link RowFilterError} on unparsable filters or missing identity
 * claims - the engine fails closed (spec 08 §5.4).
 */
export type RowFilterParser = (table: string, rowFilter: string) => FilterNode;

/** Read-only retrieval of the rule sets that apply to an identity (port of IPolicySource). */
export interface PolicySource {
  resolveRules(identity: RequestIdentity, serviceName: string): Promise<readonly RoleRuleSet[]>;
}
