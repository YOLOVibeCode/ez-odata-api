namespace EzOdata.Data.Entities;

/// <summary>spec 03 §2.3.</summary>
public class RoleEntity
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsAdmin { get; set; }
    public bool BypassDataRules { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long RowVersion { get; set; }

    public List<RoleServiceAccessEntity> Access { get; set; } = [];
}

/// <summary>spec 03 §2.4 — one row of the RBAC matrix.</summary>
public class RoleServiceAccessEntity
{
    public long Id { get; set; }
    public long RoleId { get; set; }
    public RoleEntity? Role { get; set; }

    /// <summary>NULL = wildcard grant across all services.</summary>
    public long? ServiceId { get; set; }

    public required string ResourcePattern { get; set; }

    /// <summary>Bitmask: 1=GET, 2=POST, 4=PUT, 8=PATCH, 16=DELETE.</summary>
    public int Verbs { get; set; }

    /// <summary>OData $filter expression AND-ed into every operation; may reference @identity.* claims.</summary>
    public string? RowFilter { get; set; }

    public int Priority { get; set; }

    /// <summary>"allow" | "deny".</summary>
    public required string Effect { get; set; }

    public List<FieldPolicyEntity> FieldPolicies { get; set; } = [];
}

/// <summary>spec 03 §2.5.</summary>
public class FieldPolicyEntity
{
    public long Id { get; set; }
    public long RoleServiceAccessId { get; set; }
    public RoleServiceAccessEntity? Access { get; set; }
    public required string FieldPattern { get; set; }

    /// <summary>"deny" | "mask" | "writeonly".</summary>
    public required string Action { get; set; }

    public string? MaskValue { get; set; }
}
