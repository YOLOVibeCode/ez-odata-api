import { ErrorCodes, RowFilterError } from "../errors.js";
import { isMatch } from "../glob.js";
import { logical, type FilterNode } from "../query.js";
import type { RowFilterParser } from "./contracts.js";
import { denyDecision, FULL_ACCESS, type PolicyDecision } from "./decision.js";
import { Verb, type AccessRule, type RequestIdentity, type RoleRuleSet } from "./model.js";

interface RoleDecision {
  readonly allowed: boolean;
  readonly hadMatch: boolean;
  readonly verbDenied: boolean;
  readonly denied: Set<string>;
  readonly masked: Map<string, string>;
  readonly writeOnly: Set<string>;
  readonly rowFilter?: FilterNode;
  readonly hasUnfilteredAccess: boolean;
}

const NO_MATCH: RoleDecision = {
  allowed: false,
  hadMatch: false,
  verbDenied: false,
  denied: new Set(),
  masked: new Map(),
  writeOnly: new Set(),
  hasUnfilteredAccess: false,
};

const MATCHED_BUT_DENIED: RoleDecision = { ...NO_MATCH, hadMatch: true };

const VERB_DENIED: RoleDecision = { ...NO_MATCH, hadMatch: true, verbDenied: true };

/**
 * The single authorization algorithm (spec 08 §4-5) shared by OData, REST, and
 * MCP (port of src/EzOdata.Core/Policy/PolicyEngine.cs). Pure logic: rules and
 * parsing arrive from outside; nothing here touches storage.
 */
export class PolicyEngine {
  authorize(
    identity: RequestIdentity,
    roleRules: readonly RoleRuleSet[],
    serviceName: string,
    table: string,
    verb: Verb,
    tableColumns: readonly string[],
    rowFilterParser: RowFilterParser,
  ): PolicyDecision {
    // Dev-bypass identity short-circuits before any role evaluation.
    if (identity.bypass) {
      return FULL_ACCESS;
    }

    if (roleRules.some((r) => r.bypassDataRules)) {
      return FULL_ACCESS;
    }

    const allowing: RoleDecision[] = [];
    let sawMatchingRule = false;
    let sawVerbDenial = false;

    for (const role of roleRules) {
      const evaluation = evaluateRole(role, serviceName, table, verb, tableColumns, rowFilterParser);
      sawMatchingRule ||= evaluation.hadMatch;
      sawVerbDenial ||= evaluation.verbDenied;
      if (evaluation.allowed) {
        allowing.push(evaluation);
      }
    }

    if (allowing.length === 0) {
      if (sawMatchingRule) {
        return denyDecision(
          sawVerbDenial ? ErrorCodes.ForbiddenVerb : "Forbidden",
          sawVerbDenial
            ? `Verb ${verb} is not granted on '${table}'.`
            : `Access to '${table}' is denied.`,
        );
      }
      return denyDecision("NotFound", `Resource '${table}' not found.`, true);
    }

    return merge(allowing);
  }
}

function evaluateRole(
  role: RoleRuleSet,
  serviceName: string,
  table: string,
  verb: Verb,
  tableColumns: readonly string[],
  rowFilterParser: RowFilterParser,
): RoleDecision {
  const matching = role.rules.filter(
    (r) =>
      (r.serviceName === undefined || r.serviceName.toLowerCase() === serviceName.toLowerCase()) &&
      isMatch(table, r.resourcePattern),
  );

  if (matching.length === 0) {
    return NO_MATCH;
  }

  // Highest priority wins; tie => deny wins (spec 08 §4 step 3). Array.sort is stable.
  const top = [...matching].sort((a, b) => {
    if (b.priority !== a.priority) return b.priority - a.priority;
    return effectRank(a) - effectRank(b);
  })[0]!;

  if (top.effect === "deny") {
    return MATCHED_BUT_DENIED;
  }

  if ((top.verbs & verb) === 0) {
    return VERB_DENIED;
  }

  // Field policies union across ALL matching allow rules (most restrictive, §4 step 5),
  // with glob patterns expanded against the table's actual columns.
  const allowRules = matching.filter((r) => r.effect === "allow");
  const denied = new Set<string>();
  const masked = new Map<string, string>();
  const writeOnly = new Set<string>();

  for (const rule of allowRules) {
    for (const fieldRule of rule.fieldRules) {
      for (const column of tableColumns.filter((c) => isMatch(c, fieldRule.pattern))) {
        switch (fieldRule.action) {
          case "deny":
            denied.add(column);
            break;
          case "mask":
            masked.set(column, fieldRule.maskValue ?? "***");
            break;
          case "writeOnly":
            writeOnly.add(column);
            break;
        }
      }
    }
  }

  // Row filters AND within the role (§4 step 7); unparsable => fail closed (§5.4).
  let rowFilter: FilterNode | undefined;
  for (const rule of allowRules) {
    if (rule.rowFilter === undefined || rule.rowFilter.trim() === "") continue;

    let parsed: FilterNode;
    try {
      parsed = rowFilterParser(table, rule.rowFilter);
    } catch (err) {
      if (err instanceof RowFilterError) {
        return MATCHED_BUT_DENIED;
      }
      throw err;
    }

    rowFilter = rowFilter === undefined ? parsed : logical("and", [rowFilter, parsed]);
  }

  return {
    allowed: true,
    hadMatch: true,
    verbDenied: false,
    denied,
    masked,
    writeOnly,
    ...(rowFilter !== undefined ? { rowFilter } : {}),
    hasUnfilteredAccess: rowFilter === undefined,
  };
}

function merge(allowing: readonly RoleDecision[]): PolicyDecision {
  // Union of access across allowing roles (spec 08 §5.6): a field restriction only
  // survives if EVERY allowing role imposes it.
  const first = allowing[0]!;
  const mergedDenied = new Set(first.denied);
  const mergedWriteOnly = new Set(first.writeOnly);
  const mergedMasked = new Map(first.masked);

  for (const role of allowing.slice(1)) {
    const { denied, masked, writeOnly } = role;
    for (const field of [...mergedDenied]) {
      if (!denied.has(field) && !masked.has(field) && !writeOnly.has(field)) {
        mergedDenied.delete(field);
      }
    }
    for (const field of [...mergedWriteOnly]) {
      if (!writeOnly.has(field) && !denied.has(field)) {
        mergedWriteOnly.delete(field);
      }
    }
    for (const key of [...mergedMasked.keys()]) {
      if (!masked.has(key) && !denied.has(key)) {
        mergedMasked.delete(key);
      }
    }
  }

  // Denied beats masked when both survive.
  for (const field of mergedDenied) {
    mergedMasked.delete(field);
  }

  // Row filter: OR across allowing roles; any role with unfiltered access => no filter.
  let rowFilter: FilterNode | undefined;
  if (!allowing.some((r) => r.hasUnfilteredAccess)) {
    for (const role of allowing) {
      rowFilter = rowFilter === undefined ? role.rowFilter : logical("or", [rowFilter, role.rowFilter!]);
    }
  }

  return {
    allowed: true,
    hidden: false,
    bypass: false,
    deniedFields: mergedDenied,
    maskedFields: mergedMasked,
    writeOnlyFields: mergedWriteOnly,
    ...(rowFilter !== undefined ? { rowFilter } : {}),
  };
}

function effectRank(rule: AccessRule): number {
  return rule.effect === "deny" ? 0 : 1;
}
