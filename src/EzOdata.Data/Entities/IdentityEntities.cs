namespace EzOdata.Data.Entities;

/// <summary>spec 03 §2.6.</summary>
public class UserEntity
{
    public long Id { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public string? PasswordHash { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSystemAdmin { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long RowVersion { get; set; }

    public List<UserRoleEntity> Roles { get; set; } = [];
}

/// <summary>spec 03 §2.7 — composite PK (UserId, RoleId).</summary>
public class UserRoleEntity
{
    public long UserId { get; set; }
    public UserEntity? User { get; set; }
    public long RoleId { get; set; }
    public RoleEntity? Role { get; set; }
}

/// <summary>spec 03 §2.8 — a registered API consumer.</summary>
public class AppEntity
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public long RoleId { get; set; }
    public RoleEntity? Role { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>JSON array of allowed CORS origins.</summary>
    public string? AllowedOriginsJson { get; set; }

    public bool RequireUserSession { get; set; }
    public bool McpEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long RowVersion { get; set; }

    public List<ApiKeyEntity> Keys { get; set; } = [];
}

/// <summary>spec 03 §2.9 — full key shown exactly once; only hash stored.</summary>
public class ApiKeyEntity
{
    public long Id { get; set; }
    public long AppId { get; set; }
    public AppEntity? App { get; set; }
    public required string KeyPrefix { get; set; }
    public required string KeyHash { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>spec 03 §2.11 — single-use rotation with family revocation on reuse (spec 08 §3.4).</summary>
public class RefreshTokenEntity
{
    public Guid Id { get; set; }
    public long UserId { get; set; }
    public UserEntity? User { get; set; }
    public required string TokenHash { get; set; }
    public Guid FamilyId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedByIp { get; set; }
    public string? UserAgent { get; set; }
}
