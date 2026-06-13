using EzOdata.Core.Query;

namespace EzOdata.Core.Policy;

/// <summary>
/// Parses a role's row-filter expression (OData $filter grammar) to IR for one table.
/// Bound by the caller with schema + identity; throws <see cref="RowFilterException"/>
/// on unparsable filters or missing identity claims — the engine fails closed
/// (spec 08 §5.4).
/// </summary>
public delegate FilterNode RowFilterParser(string table, string rowFilter);

public sealed class RowFilterException : Exception
{
    public RowFilterException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The single authorization algorithm (spec 08 §4–5) shared by OData, REST, and MCP.
/// Pure logic: rules and parsing arrive from outside; nothing here touches storage.
/// </summary>
public sealed class PolicyEngine
{
    public PolicyDecision Authorize(
        RequestIdentity identity,
        IReadOnlyList<RoleRuleSet> roleRules,
        string serviceName,
        string table,
        Verb verb,
        IReadOnlyCollection<string> tableColumns,
        RowFilterParser rowFilterParser)
    {
        if (roleRules.Any(r => r.BypassDataRules))
        {
            return PolicyDecision.FullAccess;
        }

        // Evaluate each role independently; the identity may act under the union (spec 08 §5.6).
        var allowing = new List<RoleDecision>();
        var sawMatchingRule = false;
        var sawVerbDenial = false;

        foreach (var role in roleRules)
        {
            var evaluation = EvaluateRole(role, serviceName, table, verb, tableColumns, rowFilterParser);
            sawMatchingRule |= evaluation.HadMatch;
            sawVerbDenial |= evaluation.VerbDenied;
            if (evaluation.Allowed)
            {
                allowing.Add(evaluation);
            }
        }

        if (allowing.Count == 0)
        {
            // No rule even matches the resource for any role ⇒ the table is invisible (404).
            return sawMatchingRule
                ? PolicyDecision.Deny(
                    sawVerbDenial ? ErrorCodes.ForbiddenVerb : "Forbidden",
                    sawVerbDenial
                        ? $"Verb {verb} is not granted on '{table}'."
                        : $"Access to '{table}' is denied.")
                : PolicyDecision.Deny("NotFound", $"Resource '{table}' not found.", hidden: true);
        }

        return Merge(allowing);
    }

    private static RoleDecision EvaluateRole(
        RoleRuleSet role, string serviceName, string table, Verb verb,
        IReadOnlyCollection<string> tableColumns, RowFilterParser rowFilterParser)
    {
        var matching = role.Rules
            .Where(r => r.ServiceName is null
                        || string.Equals(r.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase))
            .Where(r => Text.Glob.IsMatch(table, r.ResourcePattern))
            .ToList();

        if (matching.Count == 0)
        {
            return RoleDecision.NoMatch;
        }

        // Highest priority wins; tie → deny wins (spec 08 §4 step 3).
        var top = matching
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Effect == RuleEffect.Deny ? 0 : 1)
            .First();

        if (top.Effect == RuleEffect.Deny)
        {
            return RoleDecision.MatchedButDenied;
        }

        if ((top.Verbs & verb) == 0)
        {
            return RoleDecision.VerbDeniedResult;
        }

        // Field policies union across ALL matching allow rules (most restrictive, §4 step 5),
        // with glob patterns expanded against the table's actual columns.
        var allowRules = matching.Where(r => r.Effect == RuleEffect.Allow).ToList();
        var denied = new HashSet<string>(StringComparer.Ordinal);
        var masked = new Dictionary<string, string>(StringComparer.Ordinal);
        var writeOnly = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in allowRules)
        {
            foreach (var fieldRule in rule.FieldRules)
            {
                foreach (var column in tableColumns.Where(c => Text.Glob.IsMatch(c, fieldRule.Pattern)))
                {
                    switch (fieldRule.Action)
                    {
                        case FieldAction.Deny: denied.Add(column); break;
                        case FieldAction.Mask: masked[column] = fieldRule.MaskValue ?? "***"; break;
                        case FieldAction.WriteOnly: writeOnly.Add(column); break;
                    }
                }
            }
        }

        // Row filters AND within the role (§4 step 7); unparsable ⇒ fail closed (§5.4).
        FilterNode? rowFilter = null;
        foreach (var rule in allowRules)
        {
            if (string.IsNullOrWhiteSpace(rule.RowFilter)) continue;

            FilterNode parsed;
            try
            {
                parsed = rowFilterParser(table, rule.RowFilter!);
            }
            catch (RowFilterException)
            {
                return RoleDecision.MatchedButDenied;
            }

            rowFilter = rowFilter is null
                ? parsed
                : new LogicalNode(LogicalOp.And, [rowFilter, parsed]);
        }

        return new RoleDecision(
            Allowed: true, HadMatch: true, VerbDenied: false,
            Denied: denied, Masked: masked, WriteOnly: writeOnly,
            RowFilter: rowFilter, HasUnfilteredAccess: rowFilter is null);
    }

    private static PolicyDecision Merge(List<RoleDecision> allowing)
    {
        // Union of access across allowing roles (spec 08 §5.6): a field restriction only
        // survives if EVERY allowing role imposes it.
        var first = allowing[0];
        var mergedDenied = new HashSet<string>(first.Denied, StringComparer.Ordinal);
        var mergedWriteOnly = new HashSet<string>(first.WriteOnly, StringComparer.Ordinal);
        var mergedMasked = new Dictionary<string, string>(first.Masked, StringComparer.Ordinal);

        foreach (var role in allowing.Skip(1))
        {
            var (denied, masked, writeOnly) = (role.Denied, role.Masked, role.WriteOnly);
            // A field stays restricted only when restricted in this role too (any-role access wins).
            mergedDenied.RemoveWhere(f => !denied.Contains(f) && !masked.ContainsKey(f) && !writeOnly.Contains(f));
            mergedWriteOnly.RemoveWhere(f => !writeOnly.Contains(f) && !denied.Contains(f));
            foreach (var key in mergedMasked.Keys.ToList())
            {
                if (!masked.ContainsKey(key) && !denied.Contains(key))
                {
                    mergedMasked.Remove(key);
                }
            }
        }

        // Denied beats masked when both survive
        foreach (var field in mergedDenied)
        {
            mergedMasked.Remove(field);
        }

        // Row filter: OR across allowing roles; any role with unfiltered access ⇒ no filter.
        FilterNode? rowFilter = null;
        if (!allowing.Any(r => r.HasUnfilteredAccess))
        {
            foreach (var role in allowing)
            {
                rowFilter = rowFilter is null
                    ? role.RowFilter
                    : new LogicalNode(LogicalOp.Or, [rowFilter, role.RowFilter!]);
            }
        }

        return new PolicyDecision
        {
            Allowed = true,
            DeniedFields = mergedDenied,
            MaskedFields = mergedMasked,
            WriteOnlyFields = mergedWriteOnly,
            RowFilter = rowFilter,
        };
    }

    private sealed record RoleDecision(
        bool Allowed, bool HadMatch, bool VerbDenied,
        HashSet<string> Denied, Dictionary<string, string> Masked, HashSet<string> WriteOnly,
        FilterNode? RowFilter, bool HasUnfilteredAccess)
    {
        public static readonly RoleDecision NoMatch = new(false, false, false, [], [], [], null, false);
        public static readonly RoleDecision MatchedButDenied = new(false, true, false, [], [], [], null, false);
        public static readonly RoleDecision VerbDeniedResult = new(false, true, true, [], [], [], null, false);
    }
}
