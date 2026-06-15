using EzOdata.Connectors.Abstractions;
using EzOdata.Connectors.MySql;
using EzOdata.Connectors.PostgreSql;
using EzOdata.Connectors.Sqlite;
using EzOdata.Connectors.SqlServer;
using EzOdata.Core.Policy;
using EzOdata.Core.Services;

namespace EzOdata.Embedded;

/// <summary>
/// Fluent configuration for embedding ez-odata-api in an existing ASP.NET Core app
/// (spec 15 §3): code-declared services and roles, no system database, no admin UI.
/// </summary>
public sealed class EzOdataBuilder
{
    public List<EmbeddedServiceDefinition> Services { get; } = [];
    public List<RoleRuleSet> Roles { get; } = [];
    public List<ConnectorDescriptor> Connectors { get; } =
    [
        PostgreSqlConnector.Create(),
        MySqlConnectorFactory.Create(),
        SqlServerConnector.Create(),
        SqliteConnector.Create(),
    ];

    // Role-resolution strategy — exactly one of these will be set.
    public Func<System.Security.Claims.ClaimsPrincipal, IReadOnlyList<string>>? RoleResolver { get; private set; }

    // Claim-based role mapping (UseHostRoles / MapRolesFromClaim)
    public string? RoleClaimType { get; private set; }
    public Func<string, string?>? RoleClaimTransform { get; private set; }

    // Dev no-auth
    public bool DevNoAuth { get; private set; }

    public EzOdataBuilder AddService(string name, Action<EmbeddedServiceBuilder> configure)
    {
        var sb = new EmbeddedServiceBuilder(name);
        configure(sb);
        Services.Add(sb.Build());
        return this;
    }

    /// <summary>Declare a role and its access rules in code (spec 15 §3).</summary>
    public EzOdataBuilder AddRole(string name, Action<EmbeddedRoleBuilder> configure)
    {
        var rb = new EmbeddedRoleBuilder(name);
        configure(rb);
        Roles.Add(rb.Build());
        return this;
    }

    /// <summary>
    /// The escape-hatch bridge: fully custom ClaimsPrincipal → ez role names.
    /// Use <see cref="UseHostRoles"/> or <see cref="MapRolesFromClaim"/> for the common cases.
    /// </summary>
    public EzOdataBuilder ResolveRolesBy(Func<System.Security.Claims.ClaimsPrincipal, IReadOnlyList<string>> resolver)
    {
        RoleResolver = resolver;
        RoleClaimType = null;
        return this;
    }

    /// <summary>
    /// Map the host's standard role claims (<see cref="System.Security.Claims.ClaimTypes.Role"/>)
    /// directly to ez role names. The role names must match roles declared via <see cref="AddRole"/>.
    /// Also flows <c>sub</c>/<c>email</c> and all claims so @identity.* row filters resolve.
    /// </summary>
    public EzOdataBuilder UseHostRoles()
    {
        RoleClaimType = System.Security.Claims.ClaimTypes.Role;
        RoleClaimTransform = null;
        RoleResolver = null;
        return this;
    }

    /// <summary>
    /// Map a custom claim to ez role names, with an optional transform function
    /// (e.g. prefix stripping or lowercasing). Also flows sub/email and all claims.
    /// </summary>
    public EzOdataBuilder MapRolesFromClaim(string claimType, Func<string, string?>? transform = null)
    {
        RoleClaimType = claimType;
        RoleClaimTransform = transform;
        RoleResolver = null;
        return this;
    }

    /// <summary>
    /// Allow anonymous (unauthenticated) requests to pass through as a full-access bypass
    /// identity — ONLY when <c>ASPNETCORE_ENVIRONMENT=Development</c>.
    /// The app will refuse to start with this flag set in any other environment.
    /// Never use in production.
    /// </summary>
    public EzOdataBuilder AllowAnonymousInDevelopment()
    {
        DevNoAuth = true;
        return this;
    }
}

public sealed class EmbeddedServiceBuilder
{
    private readonly string _name;
    private string _connectorType = ConnectorTypes.PostgreSql;
    private ConnectionSpec _connection = new();
    private ServiceOptions _options = new();

    public EmbeddedServiceBuilder(string name) => _name = name;

    public EmbeddedServiceBuilder UsePostgreSql(ConnectionSpec connection) { _connectorType = ConnectorTypes.PostgreSql; _connection = connection; return this; }
    public EmbeddedServiceBuilder UseMySql(ConnectionSpec connection) { _connectorType = ConnectorTypes.MySql; _connection = connection; return this; }
    public EmbeddedServiceBuilder UseSqlServer(ConnectionSpec connection) { _connectorType = ConnectorTypes.SqlServer; _connection = connection; return this; }
    public EmbeddedServiceBuilder UseSqlite(string filePath) { _connectorType = ConnectorTypes.Sqlite; _connection = new ConnectionSpec { FilePath = filePath }; return this; }

    public EmbeddedServiceBuilder Options(Action<ServiceOptionsBuilder> configure)
    {
        var ob = new ServiceOptionsBuilder(_options);
        configure(ob);
        _options = ob.Build();
        return this;
    }

    internal EmbeddedServiceDefinition Build() => new(_name, _connectorType, _connection, _options);
}

public sealed class ServiceOptionsBuilder
{
    private ServiceOptions _options;
    public ServiceOptionsBuilder(ServiceOptions seed) => _options = seed;

    public ServiceOptionsBuilder ReadOnly(bool value = true) { _options = _options with { ReadOnly = value }; return this; }
    public ServiceOptionsBuilder DefaultPageSize(int n) { _options = _options with { DefaultPageSize = n }; return this; }
    public ServiceOptionsBuilder MaxPageSize(int n) { _options = _options with { MaxPageSize = n }; return this; }
    public ServiceOptionsBuilder IncludeSchemas(params string[] schemas) { _options = _options with { IncludeSchemas = schemas }; return this; }
    public ServiceOptionsBuilder ExcludeTables(params string[] globs) { _options = _options with { ExcludeTables = globs }; return this; }

    internal ServiceOptions Build() => _options;
}

public sealed class EmbeddedRoleBuilder
{
    private readonly string _name;
    private readonly List<AccessRule> _rules = [];
    private bool _bypass;

    public EmbeddedRoleBuilder(string name) => _name = name;

    public EmbeddedRoleBuilder BypassDataRules() { _bypass = true; return this; }

    public EmbeddedRoleBuilder Allow(string? serviceName, string resourcePattern, Verb verbs, string? rowFilter = null,
        IEnumerable<FieldRule>? fieldRules = null)
    {
        _rules.Add(new AccessRule
        {
            ServiceName = serviceName,
            ResourcePattern = resourcePattern,
            Verbs = verbs,
            Effect = RuleEffect.Allow,
            RowFilter = rowFilter,
            FieldRules = fieldRules?.ToList() ?? [],
        });
        return this;
    }

    public EmbeddedRoleBuilder Deny(string? serviceName, string resourcePattern, int priority = 100)
    {
        _rules.Add(new AccessRule { ServiceName = serviceName, ResourcePattern = resourcePattern, Effect = RuleEffect.Deny, Priority = priority });
        return this;
    }

    internal RoleRuleSet Build() => new(_name.GetHashCode(), _name, _bypass, _rules);
}

public sealed record EmbeddedServiceDefinition(
    string Name, string ConnectorType, ConnectionSpec Connection, ServiceOptions Options);
