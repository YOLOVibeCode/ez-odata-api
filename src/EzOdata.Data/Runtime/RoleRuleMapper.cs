using EzOdata.Core.Policy;
using EzOdata.Data.Entities;

namespace EzOdata.Data.Runtime;

/// <summary>Entity → policy-model mapping shared by EfPolicySource and the role simulator.</summary>
public static class RoleRuleMapper
{
    public static RoleRuleSet ToRuleSet(RoleEntity role, IReadOnlyDictionary<long, string> serviceNames) =>
        new(role.Id, role.Name, role.BypassDataRules,
            role.Access.Select(a => new AccessRule
            {
                Id = a.Id,
                ServiceName = a.ServiceId is { } sid && serviceNames.TryGetValue(sid, out var name) ? name : null,
                ResourcePattern = a.ResourcePattern,
                Verbs = (Verb)a.Verbs,
                Effect = a.Effect == "deny" ? RuleEffect.Deny : RuleEffect.Allow,
                Priority = a.Priority,
                RowFilter = a.RowFilter,
                FieldRules = a.FieldPolicies.Select(f => new FieldRule(
                    f.FieldPattern,
                    f.Action switch
                    {
                        "mask" => FieldAction.Mask,
                        "writeonly" => FieldAction.WriteOnly,
                        _ => FieldAction.Deny,
                    },
                    f.MaskValue)).ToList(),
            }).ToList());

    public static Verb ParseVerb(string verb) => verb.ToUpperInvariant() switch
    {
        "GET" => Verb.Get,
        "POST" => Verb.Post,
        "PUT" => Verb.Put,
        "PATCH" => Verb.Patch,
        "DELETE" => Verb.Delete,
        _ => Verb.None,
    };
}
