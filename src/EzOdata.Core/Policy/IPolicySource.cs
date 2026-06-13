namespace EzOdata.Core.Policy;

/// <summary>
/// Rule retrieval only (ISP, spec 02 §3.1): persistence lives elsewhere.
/// Implementations: EF-backed (platform) and code-declared (embedded, doc 15).
/// </summary>
public interface IPolicySource
{
    /// <summary>Rule sets for the given role ids; inactive roles are omitted.</summary>
    Task<IReadOnlyList<RoleRuleSet>> GetRoleRulesAsync(IReadOnlyList<long> roleIds, CancellationToken ct);

    /// <summary>
    /// Monotonic version of the policy configuration, used in trimmed-model cache keys
    /// (spec 05 §3.9). Changes whenever any role/rule mutates.
    /// </summary>
    Task<string> GetPolicyVersionAsync(CancellationToken ct);
}
