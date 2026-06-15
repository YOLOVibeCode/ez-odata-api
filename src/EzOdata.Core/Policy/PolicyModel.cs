namespace EzOdata.Core.Policy;

/// <summary>HTTP verb bitmask matching spec 03 §2.4.</summary>
[Flags]
public enum Verb
{
    None = 0,
    Get = 1,
    Post = 2,
    Put = 4,
    Patch = 8,
    Delete = 16,
    All = Get | Post | Put | Patch | Delete,
}

public enum RuleEffect { Allow, Deny }

public enum FieldAction { Deny, Mask, WriteOnly }

/// <summary>One row of the RBAC matrix (spec 03 §2.4), resolved from storage.</summary>
public sealed record AccessRule
{
    public long Id { get; init; }

    /// <summary>null = wildcard across services.</summary>
    public string? ServiceName { get; init; }

    public required string ResourcePattern { get; init; }
    public Verb Verbs { get; init; }
    public RuleEffect Effect { get; init; } = RuleEffect.Allow;
    public int Priority { get; init; }

    /// <summary>OData $filter expression with optional @identity.* claim references.</summary>
    public string? RowFilter { get; init; }

    public IReadOnlyList<FieldRule> FieldRules { get; init; } = [];
}

public sealed record FieldRule(string Pattern, FieldAction Action, string? MaskValue);

/// <summary>All rules of one active role.</summary>
public sealed record RoleRuleSet(long RoleId, string RoleName, bool BypassDataRules, IReadOnlyList<AccessRule> Rules);

/// <summary>The authenticated principal of a request (spec 08 §2).</summary>
public sealed record RequestIdentity
{
    public long? AppId { get; init; }
    public long? UserId { get; init; }
    public string? Email { get; init; }
    public bool IsAdmin { get; init; }
    public IReadOnlyList<long> RoleIds { get; init; } = [];

    /// <summary>Claims usable in row filters via @identity.* (spec 08 §5.4).</summary>
    public IReadOnlyDictionary<string, string> Claims { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Full-access bypass: PolicyEngine and SnapshotTrimmer short-circuit immediately.
    /// Only ever set in development (gated by AllowAnonymousInDevelopment /
    /// Auth:DevNoAuth + ASPNETCORE_ENVIRONMENT=Development).
    /// </summary>
    public bool Bypass { get; init; }

    public static readonly RequestIdentity Anonymous = new();

    public static readonly RequestIdentity DevBypass = new() { Bypass = true, IsAdmin = true };
}
