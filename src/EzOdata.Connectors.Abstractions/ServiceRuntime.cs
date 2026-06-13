using EzOdata.Core.Schema;
using EzOdata.Core.Services;

namespace EzOdata.Connectors.Abstractions;

/// <summary>
/// Everything the protocol engines need to serve one service: resolved connection,
/// current schema snapshot, and options. Produced by the schema cache manager.
/// </summary>
public sealed record ServiceRuntime(
    string Name,
    string ConnectorType,
    ConnectionSpec Connection,
    SchemaSnapshot Schema,
    ServiceOptions Options,
    string SchemaVersion,
    ServiceStatus Status);

/// <summary>Resolves a service by URL slug; null = unknown service (404 upstream).</summary>
public interface IServiceRuntimeResolver
{
    Task<ServiceRuntime?> ResolveAsync(string serviceName, CancellationToken ct);
}
