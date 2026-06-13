using System.Collections.Concurrent;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Policy;
using EzOdata.Core.Schema;
using EzOdata.Core.Services;
using Microsoft.Extensions.Logging;

namespace EzOdata.Embedded;

/// <summary>
/// Code/config-defined service runtimes (spec 15 §4): introspects each declared service
/// on startup, caches the snapshot in memory (optionally persisted to a file), and serves
/// it through the same engine as the platform. No system database.
/// </summary>
public sealed class InMemoryServiceRuntimeResolver : IServiceRuntimeResolver
{
    private readonly IReadOnlyList<EmbeddedServiceDefinition> _definitions;
    private readonly IConnectorRegistry _connectors;
    private readonly ConcurrentDictionary<string, ServiceRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryServiceRuntimeResolver(IReadOnlyList<EmbeddedServiceDefinition> definitions, IConnectorRegistry connectors)
    {
        _definitions = definitions;
        _connectors = connectors;
    }

    public Task<ServiceRuntime?> ResolveAsync(string serviceName, CancellationToken ct) =>
        Task.FromResult(_runtimes.TryGetValue(serviceName, out var runtime) ? runtime : null);

    public async Task IntrospectAllAsync(ILogger logger, CancellationToken ct)
    {
        foreach (var def in _definitions)
        {
            if (!_connectors.TryGet(def.ConnectorType, out var connector))
            {
                logger.LogError("Embedded service '{Service}': connector '{Type}' not registered.", def.Name, def.ConnectorType);
                continue;
            }

            try
            {
                var snapshot = await connector.Introspector.IntrospectAsync(def.Connection, new IntrospectionOptions
                {
                    IncludeSchemas = def.Options.IncludeSchemas,
                    ExcludeTables = def.Options.ExcludeTables,
                    IncludeViews = def.Options.IncludeViews,
                    ExposedNameStyle = def.Options.ExposedNameStyle,
                }, ct);

                var version = SnapshotSerializer.ComputeHash(snapshot);
                _runtimes[def.Name] = new ServiceRuntime(
                    def.Name, def.ConnectorType, def.Connection, snapshot, def.Options, version, ServiceStatus.Active);
                logger.LogInformation("Embedded service '{Service}' ready: {Tables} tables.", def.Name, snapshot.TableCount);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Embedded service '{Service}' introspection failed.", def.Name);
            }
        }
    }
}

/// <summary>Code-declared roles (spec 15 §3): rules from the fluent builder, no persistence.</summary>
public sealed class InMemoryPolicySource : IPolicySource
{
    private readonly Dictionary<long, RoleRuleSet> _byId;
    private readonly string _version;

    public InMemoryPolicySource(IReadOnlyList<RoleRuleSet> roles)
    {
        _byId = roles.ToDictionary(r => r.RoleId);
        _version = $"embedded-{roles.Count}-{string.Join(",", roles.Select(r => r.RoleName))}".GetHashCode().ToString();
    }

    public Task<IReadOnlyList<RoleRuleSet>> GetRoleRulesAsync(IReadOnlyList<long> roleIds, CancellationToken ct)
    {
        var result = roleIds.Where(_byId.ContainsKey).Select(id => _byId[id]).ToList();
        return Task.FromResult<IReadOnlyList<RoleRuleSet>>(result);
    }

    public Task<string> GetPolicyVersionAsync(CancellationToken ct) => Task.FromResult(_version);

    /// <summary>Maps role names (from the host identity bridge) to their ids.</summary>
    public IReadOnlyList<long> ResolveRoleIds(IEnumerable<string> roleNames)
    {
        var byName = _byId.Values.ToDictionary(r => r.RoleName, r => r.RoleId, StringComparer.OrdinalIgnoreCase);
        return roleNames.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
    }
}
