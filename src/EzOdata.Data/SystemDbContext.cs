using System.Text;
using EzOdata.Core.Time;
using EzOdata.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.Data;

/// <summary>
/// EF Core context for the platform's own metadata store (spec 03).
/// Providers: SQLite (dev/default) and PostgreSQL (prod) — schema kept provider-portable.
/// </summary>
public class SystemDbContext : DbContext
{
    private readonly ISystemClock _clock;

    public SystemDbContext(DbContextOptions<SystemDbContext> options, ISystemClock clock)
        : base(options)
    {
        _clock = clock;
    }

    public DbSet<ServiceEntity> Services => Set<ServiceEntity>();
    public DbSet<SchemaSnapshotEntity> SchemaSnapshots => Set<SchemaSnapshotEntity>();
    public DbSet<RoleEntity> Roles => Set<RoleEntity>();
    public DbSet<RoleServiceAccessEntity> RoleServiceAccess => Set<RoleServiceAccessEntity>();
    public DbSet<FieldPolicyEntity> FieldPolicies => Set<FieldPolicyEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<UserRoleEntity> UserRoles => Set<UserRoleEntity>();
    public DbSet<AppEntity> Apps => Set<AppEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<RateLimitPolicyEntity> RateLimitPolicies => Set<RateLimitPolicyEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<SystemSettingEntity> SystemSettings => Set<SystemSettingEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ServiceEntity>(e =>
        {
            e.ToTable("services");
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Label).HasMaxLength(128);
            e.Property(x => x.ConnectorType).HasMaxLength(32);
            e.Property(x => x.ConnectionFingerprint).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.Name).IsUnique().HasFilter("is_deleted = 0");
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<SchemaSnapshotEntity>(e =>
        {
            e.ToTable("schema_snapshots");
            e.Property(x => x.VersionHash).HasMaxLength(64);
            e.HasOne(x => x.Service).WithMany(s => s.Snapshots).HasForeignKey(x => x.ServiceId);
            e.HasIndex(x => x.ServiceId).IsUnique().HasFilter("is_current = 1");
        });

        b.Entity<RoleEntity>(e =>
        {
            e.ToTable("roles");
            e.Property(x => x.Name).HasMaxLength(64);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<RoleServiceAccessEntity>(e =>
        {
            e.ToTable("role_service_access");
            e.Property(x => x.ResourcePattern).HasMaxLength(256);
            e.Property(x => x.Effect).HasMaxLength(8);
            e.HasOne(x => x.Role).WithMany(r => r.Access).HasForeignKey(x => x.RoleId);
            e.HasIndex(x => new { x.RoleId, x.ServiceId });
        });

        b.Entity<FieldPolicyEntity>(e =>
        {
            e.ToTable("field_policies");
            e.Property(x => x.FieldPattern).HasMaxLength(128);
            e.Property(x => x.Action).HasMaxLength(16);
            e.Property(x => x.MaskValue).HasMaxLength(64);
            e.HasOne(x => x.Access).WithMany(a => a.FieldPolicies).HasForeignKey(x => x.RoleServiceAccessId);
        });

        b.Entity<UserEntity>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<UserRoleEntity>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.User).WithMany(u => u.Roles).HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
        });

        b.Entity<AppEntity>(e =>
        {
            e.ToTable("apps");
            e.Property(x => x.Name).HasMaxLength(64);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ApiKeyEntity>(e =>
        {
            e.ToTable("api_keys");
            e.Property(x => x.KeyPrefix).HasMaxLength(12);
            e.Property(x => x.KeyHash).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(64);
            e.HasOne(x => x.App).WithMany(a => a.Keys).HasForeignKey(x => x.AppId);
            e.HasIndex(x => x.KeyHash).IsUnique();
        });

        b.Entity<RateLimitPolicyEntity>(e =>
        {
            e.ToTable("rate_limit_policies");
            e.Property(x => x.ScopeType).HasMaxLength(16);
            e.HasIndex(x => new { x.ScopeType, x.ScopeId });
        });

        b.Entity<RefreshTokenEntity>(e =>
        {
            e.ToTable("refresh_tokens");
            e.Property(x => x.TokenHash).HasMaxLength(128);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
        });

        b.Entity<AuditEventEntity>(e =>
        {
            e.ToTable("audit_events");
            e.Property(x => x.RequestId).HasMaxLength(64);
            e.Property(x => x.Category).HasMaxLength(24);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.Outcome).HasMaxLength(8);
            e.Property(x => x.Resource).HasMaxLength(256);
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.AppId, x.OccurredAt });
            e.HasIndex(x => new { x.ServiceId, x.OccurredAt });
        });

        b.Entity<SystemSettingEntity>(e =>
        {
            e.ToTable("system_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
        });

        b.Entity<JobEntity>(e =>
        {
            e.ToTable("jobs");
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasIndex(x => new { x.Kind, x.Status });
        });

        ApplySnakeCaseColumns(b);
        ApplyConcurrencyTokens(b);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return await base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampTimestamps();
        return base.SaveChanges();
    }

    private void StampTimestamps()
    {
        var now = _clock.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            if (entry.Metadata.FindProperty("CreatedAt") is not null && entry.State == EntityState.Added)
            {
                entry.Property("CreatedAt").CurrentValue = now;
            }

            if (entry.Metadata.FindProperty("UpdatedAt") is not null)
            {
                entry.Property("UpdatedAt").CurrentValue = now;
            }

            if (entry.Metadata.FindProperty("RowVersion") is not null && entry.State == EntityState.Modified)
            {
                entry.Property("RowVersion").CurrentValue = (long)(entry.Property("RowVersion").OriginalValue ?? 0L) + 1;
            }
        }
    }

    private static void ApplySnakeCaseColumns(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static void ApplyConcurrencyTokens(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            var rowVersion = entity.FindProperty("RowVersion");
            if (rowVersion is not null)
            {
                rowVersion.IsConcurrencyToken = true;
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
