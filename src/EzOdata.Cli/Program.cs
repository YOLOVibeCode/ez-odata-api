using EzOdata.Core.Security;
using EzOdata.Core.Time;
using EzOdata.Data;
using EzOdata.Data.Entities;
using EzOdata.Data.Security;
using Microsoft.EntityFrameworkCore;

// ez-admin — operational CLI (spec 12 §7).
// Reads SystemDatabase + Encryption config from environment variables (EZODATA__... or the
// flat names used by the host), mirroring the server's configuration.

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0];
try
{
    return command switch
    {
        "migrate" => await MigrateAsync(),
        "create-admin" => await CreateAdminAsync(args),
        "validate-config" => ValidateConfig(),
        "rotate-master-key" => RotateMasterKey(args),
        "prune-audit" => await PruneAuditAsync(args),
        _ => Unknown(command),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static SystemDbContext OpenDb()
{
    var provider = Env("SystemDatabase__Provider", "SYSTEMDATABASE__PROVIDER") ?? "sqlite";
    var connection = Env("SystemDatabase__ConnectionString", "SYSTEMDATABASE__CONNECTIONSTRING")
                     ?? "Data Source=ezodata-system.db";

    var options = new DbContextOptionsBuilder<SystemDbContext>();
    if (provider.StartsWith("postgres", StringComparison.OrdinalIgnoreCase))
    {
        options.UseNpgsql(connection);
    }
    else
    {
        options.UseSqlite(connection);
    }

    return new SystemDbContext(options.Options, new SystemClock());
}

static async Task<int> MigrateAsync()
{
    using var db = OpenDb();
    var provider = Env("SystemDatabase__Provider", "SYSTEMDATABASE__PROVIDER") ?? "sqlite";
    if (provider.StartsWith("postgres", StringComparison.OrdinalIgnoreCase))
    {
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        await db.Database.MigrateAsync();
    }

    Console.WriteLine("System database is up to date.");
    return 0;
}

static async Task<int> CreateAdminAsync(string[] args)
{
    var email = Flag(args, "--email") ?? throw new InvalidOperationException("--email is required.");
    var password = Flag(args, "--password") ?? throw new InvalidOperationException("--password is required.");
    var policy = new PasswordPolicy();
    if (policy.Check(password) is { } error) throw new InvalidOperationException(error);

    using var db = OpenDb();
    var hasher = new Argon2PasswordHasher();
    email = email.Trim().ToLowerInvariant();

    var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
    if (existing is not null)
    {
        existing.PasswordHash = hasher.Hash(password);
        existing.IsSystemAdmin = true;
        existing.IsActive = true;
        existing.LockedUntil = null;
        existing.FailedLoginCount = 0;
        await db.SaveChangesAsync();
        Console.WriteLine($"Reset password and ensured admin for {email}.");
        return 0;
    }

    db.Users.Add(new UserEntity
    {
        Email = email,
        DisplayName = Flag(args, "--name") ?? email,
        PasswordHash = hasher.Hash(password),
        IsSystemAdmin = true,
        IsActive = true,
    });
    await db.SaveChangesAsync();
    Console.WriteLine($"Created system admin {email}.");
    return 0;
}

static int ValidateConfig()
{
    var provider = Env("SystemDatabase__Provider", "SYSTEMDATABASE__PROVIDER") ?? "sqlite";
    if (provider is not ("sqlite" or "postgres" or "postgresql"))
    {
        throw new InvalidOperationException($"Unsupported SystemDatabase:Provider '{provider}'.");
    }

    var key = Env("Encryption__MasterKey", "ENCRYPTION__MASTERKEY");
    if (key is null) throw new InvalidOperationException("Encryption:MasterKey is required.");
    if (Convert.FromBase64String(key).Length != 32)
    {
        throw new InvalidOperationException("Encryption master key must be 32 bytes (base64).");
    }

    Console.WriteLine("Configuration is valid.");
    return 0;
}

static int RotateMasterKey(string[] args)
{
    // Re-wraps DEKs (spec 08 §9). v1 prints guidance; full re-wrap runs in the host process
    // where the secret protector and DB are wired together.
    _ = Flag(args, "--new-key");
    Console.WriteLine("Master-key rotation: supply --new-key and restart the host with EZODATA__ENCRYPTION__MASTERKEY set to the new key.");
    Console.WriteLine("The host re-wraps each service DEK on first access and re-validates the probe value.");
    return 0;
}

static async Task<int> PruneAuditAsync(string[] args)
{
    var before = Flag(args, "--before") ?? throw new InvalidOperationException("--before YYYY-MM-DD is required.");
    var cutoff = DateTimeOffset.Parse(before);
    using var db = OpenDb();
    var deleted = await db.AuditEvents.Where(e => e.OccurredAt < cutoff).ExecuteDeleteAsync();
    Console.WriteLine($"Pruned {deleted} audit events before {cutoff:yyyy-MM-dd}.");
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        ez-admin — ez-odata-api operations CLI

        Commands:
          migrate                               Apply system DB schema
          create-admin --email E --password P [--name N]   Create/reset a system admin
          validate-config                       Validate startup configuration
          rotate-master-key --new-key K         Master key rotation guidance
          prune-audit --before YYYY-MM-DD       Delete old audit events

        Configuration via environment:
          SystemDatabase__Provider, SystemDatabase__ConnectionString, Encryption__MasterKey
        """);
}

static string? Env(params string[] names)
{
    foreach (var name in names)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value)) return value;
    }

    return null;
}

static string? Flag(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
