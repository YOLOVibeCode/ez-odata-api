using EzOdata.Core.Policy;
using EzOdata.Core.Query;
using Xunit;

namespace EzOdata.UnitTests.Policy;

/// <summary>Normative tests for spec 08 §4–5 evaluation semantics.</summary>
public class PolicyEngineTests
{
    private static readonly string[] CustomerColumns = ["id", "name", "email", "country", "ssn"];

    private static readonly PolicyEngine Engine = new();

    /// <summary>Hand-rolled parser fake (spec 13 §0.3): claims resolved like the real one.</summary>
    private static RowFilterParser ParserFor(RequestIdentity identity) => (table, rowFilter) =>
    {
        if (rowFilter.Contains("@identity.", StringComparison.Ordinal))
        {
            var claim = rowFilter.Split("@identity.")[1].Trim();
            if (!identity.Claims.TryGetValue(claim, out var value))
            {
                throw new RowFilterException($"Claim '{claim}' not present.");
            }

            return new ComparisonNode(new FieldRef("owner_id"), ComparisonOp.Eq, new ConstantValue(value));
        }

        return new ComparisonNode(new FieldRef("country"), ComparisonOp.Eq, new ConstantValue(rowFilter));
    };

    private static RoleRuleSet Role(string name, params AccessRule[] rules) =>
        new(RoleId: name.GetHashCode(), RoleName: name, BypassDataRules: false, Rules: rules);

    private static PolicyDecision Authorize(Verb verb, params RoleRuleSet[] roles) =>
        Engine.Authorize(RequestIdentity.Anonymous, roles, "sales", "customers", verb, CustomerColumns,
            ParserFor(RequestIdentity.Anonymous));

    // ---- §5.1 deny by default ----

    [Fact]
    public void No_matching_rule_hides_the_resource_as_404()
    {
        var decision = Authorize(Verb.Get, Role("r", new AccessRule { ResourcePattern = "orders", Verbs = Verb.All }));

        Assert.False(decision.Allowed);
        Assert.True(decision.Hidden);
    }

    [Fact]
    public void No_roles_at_all_hides_everything()
    {
        var decision = Authorize(Verb.Get);
        Assert.False(decision.Allowed);
        Assert.True(decision.Hidden);
    }

    // ---- §4 verb check ----

    [Fact]
    public void Matching_rule_without_verb_is_403_not_404()
    {
        var decision = Authorize(Verb.Delete, Role("ro", new AccessRule { ResourcePattern = "*", Verbs = Verb.Get }));

        Assert.False(decision.Allowed);
        Assert.False(decision.Hidden);
        Assert.Equal("Forbidden.Verb", decision.DenialCode);
    }

    // ---- §5.2 priority & effect ----

    [Fact]
    public void Higher_priority_deny_carves_exception_out_of_broad_allow()
    {
        var role = Role("r",
            new AccessRule { ResourcePattern = "*", Verbs = Verb.All, Priority = 0 },
            new AccessRule { ResourcePattern = "customers", Effect = RuleEffect.Deny, Priority = 100 });

        var decision = Authorize(Verb.Get, role);
        Assert.False(decision.Allowed);
        Assert.False(decision.Hidden); // matched, so 403 not 404
    }

    [Fact]
    public void Equal_priority_tie_deny_wins()
    {
        var role = Role("r",
            new AccessRule { ResourcePattern = "customers", Verbs = Verb.All, Priority = 5 },
            new AccessRule { ResourcePattern = "customer?", Effect = RuleEffect.Deny, Priority = 5 });

        Assert.False(Authorize(Verb.Get, role).Allowed);
    }

    [Fact]
    public void Higher_priority_allow_overrides_deny()
    {
        var role = Role("r",
            new AccessRule { ResourcePattern = "*", Effect = RuleEffect.Deny, Priority = 0 },
            new AccessRule { ResourcePattern = "customers", Verbs = Verb.Get, Priority = 10 });

        Assert.True(Authorize(Verb.Get, role).Allowed);
    }

    // ---- §5.3 wildcards ----

    [Theory]
    [InlineData("*")]
    [InlineData("cust*")]
    [InlineData("CUSTOMERS")]
    public void Resource_patterns_glob_case_insensitively(string pattern)
    {
        Assert.True(Authorize(Verb.Get, Role("r", new AccessRule { ResourcePattern = pattern, Verbs = Verb.Get })).Allowed);
    }

    // ---- field policies ----

    [Fact]
    public void Field_policies_expand_globs_and_union_across_rules()
    {
        var role = Role("r",
            new AccessRule
            {
                ResourcePattern = "*", Verbs = Verb.Get,
                FieldRules = [new FieldRule("ssn", FieldAction.Deny, null)],
            },
            new AccessRule
            {
                ResourcePattern = "customers", Verbs = Verb.Get, Priority = 1,
                FieldRules = [new FieldRule("e*", FieldAction.Mask, "***@***")],
            });

        var decision = Authorize(Verb.Get, role);

        Assert.True(decision.Allowed);
        Assert.Contains("ssn", decision.DeniedFields);
        Assert.Equal("***@***", decision.MaskedFields["email"]);
        Assert.DoesNotContain("name", decision.DeniedFields);
    }

    // ---- §5.4 row filters ----

    [Fact]
    public void Row_filters_and_within_role()
    {
        var role = Role("r",
            new AccessRule { ResourcePattern = "*", Verbs = Verb.Get, RowFilter = "US" },
            new AccessRule { ResourcePattern = "customers", Verbs = Verb.Get, RowFilter = "DE" });

        var decision = Authorize(Verb.Get, role);
        var and = Assert.IsType<LogicalNode>(decision.RowFilter);
        Assert.Equal(LogicalOp.And, and.Op);
    }

    [Fact]
    public void Missing_identity_claim_fails_closed()
    {
        var role = Role("r", new AccessRule
        {
            ResourcePattern = "*", Verbs = Verb.Get, RowFilter = "owner eq @identity.userId",
        });

        // Identity has no claims → role denies (spec 08 §5.4)
        var decision = Authorize(Verb.Get, role);
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Present_identity_claim_resolves_into_filter()
    {
        var identity = new RequestIdentity
        {
            Claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["userId"] = "42" },
        };
        var role = Role("r", new AccessRule
        {
            ResourcePattern = "*", Verbs = Verb.Get, RowFilter = "owner eq @identity.userId",
        });

        var decision = Engine.Authorize(identity, [role], "sales", "customers", Verb.Get, CustomerColumns,
            ParserFor(identity));

        Assert.True(decision.Allowed);
        var cmp = Assert.IsType<ComparisonNode>(decision.RowFilter);
        Assert.Equal("42", cmp.Value.Value);
    }

    // ---- §5.6 multiple roles ----

    [Fact]
    public void Any_allowing_role_grants_access()
    {
        var denying = Role("a", new AccessRule { ResourcePattern = "customers", Effect = RuleEffect.Deny });
        var allowing = Role("b", new AccessRule { ResourcePattern = "customers", Verbs = Verb.Get });

        Assert.True(Authorize(Verb.Get, denying, allowing).Allowed);
    }

    [Fact]
    public void Field_restriction_survives_only_if_every_allowing_role_imposes_it()
    {
        var restrictive = Role("a", new AccessRule
        {
            ResourcePattern = "*", Verbs = Verb.Get,
            FieldRules = [new FieldRule("ssn", FieldAction.Deny, null)],
        });
        var permissive = Role("b", new AccessRule { ResourcePattern = "*", Verbs = Verb.Get });

        var merged = Authorize(Verb.Get, restrictive, permissive);
        Assert.Empty(merged.DeniedFields); // more roles = more access

        var bothRestrict = Authorize(Verb.Get, restrictive, Role("c", new AccessRule
        {
            ResourcePattern = "*", Verbs = Verb.Get,
            FieldRules = [new FieldRule("ssn", FieldAction.Deny, null)],
        }));
        Assert.Contains("ssn", bothRestrict.DeniedFields);
    }

    [Fact]
    public void Row_filters_or_across_roles_and_unfiltered_role_wins()
    {
        var filteredA = Role("a", new AccessRule { ResourcePattern = "*", Verbs = Verb.Get, RowFilter = "US" });
        var filteredB = Role("b", new AccessRule { ResourcePattern = "*", Verbs = Verb.Get, RowFilter = "DE" });
        var unfiltered = Role("c", new AccessRule { ResourcePattern = "*", Verbs = Verb.Get });

        var or = Authorize(Verb.Get, filteredA, filteredB);
        var node = Assert.IsType<LogicalNode>(or.RowFilter);
        Assert.Equal(LogicalOp.Or, node.Op);

        Assert.Null(Authorize(Verb.Get, filteredA, unfiltered).RowFilter);
    }

    // ---- §5.7 bypass ----

    [Fact]
    public void Bypass_role_short_circuits_with_flag()
    {
        var bypass = new RoleRuleSet(1, "admin", BypassDataRules: true, Rules: []);
        var decision = Authorize(Verb.Delete, bypass);

        Assert.True(decision.Allowed);
        Assert.True(decision.Bypass);
    }
}
