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
    public Func<System.Security.Claims.ClaimsPrincipal, IReadOnlyList<string>>? RoleResolver { get; private set; }
    public List<ConnectorDescriptor> Connectors { get; } =
    [
        PostgreSqlConnector.Create(),
        MySqlConnectorFactory.Create(),
        SqlServerConnector.Create(),
        SqliteConnector.Create(),
    ];

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
    /// The single bridge from the host's ClaimsPrincipal to ez role names (spec 15 §3.1).
    /// If omitted, every request is denied (fail closed).
    /// </summary>
    public EzOdataBuilder ResolveRolesBy(Func<System.Security.Claims.ClaimsPrincipal, IReadOnlyList<string>> resolver)
    {
        RoleResolver = resolver;
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
