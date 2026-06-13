using EzOdata.Embedded;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EzOdata.AspNetCore.Embedded;

/// <summary>Runs embedded introspection once at startup (spec 15 EMB-7).</summary>
public sealed class EmbeddedWarmUp : IHostedService
{
    private readonly InMemoryServiceRuntimeResolver _resolver;
    private readonly ILogger<EmbeddedWarmUp> _logger;

    public EmbeddedWarmUp(InMemoryServiceRuntimeResolver resolver, ILogger<EmbeddedWarmUp> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct) => _resolver.IntrospectAllAsync(_logger, ct);

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
