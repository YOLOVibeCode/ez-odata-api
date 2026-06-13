using EzOdata.Connectors.Abstractions;
using EzOdata.Embedded;
using EzOdata.Core.Policy;
using EzOdata.Core.Time;
using EzOdata.OData;
using EzOdata.Rest;
using Microsoft.Extensions.DependencyInjection;

namespace EzOdata.AspNetCore.Embedded;

/// <summary>
/// Host integration entry point (spec 15 §3): <c>AddEzOData</c> wires the engine into an
/// existing ASP.NET Core app with code-declared services and roles, no system database.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEzOData(this IServiceCollection services, Action<EzOdataBuilder> configure)
    {
        var builder = new EzOdataBuilder();
        configure(builder);

        services.AddSingleton<ISystemClock, SystemClock>();
        services.AddSingleton<IConnectorRegistry>(new ConnectorRegistry(builder.Connectors));
        services.AddSingleton<EdmModelFactory>();
        services.AddSingleton<PolicyEngine>();

        var policySource = new InMemoryPolicySource(builder.Roles);
        services.AddSingleton<IPolicySource>(policySource);
        services.AddSingleton(policySource);

        var resolver = new InMemoryServiceRuntimeResolver(builder.Services, null!);
        // resolver needs the connector registry; resolve lazily via factory instead.
        services.AddSingleton<InMemoryServiceRuntimeResolver>(sp =>
            new InMemoryServiceRuntimeResolver(builder.Services, sp.GetRequiredService<IConnectorRegistry>()));
        services.AddSingleton<IServiceRuntimeResolver>(sp => sp.GetRequiredService<InMemoryServiceRuntimeResolver>());
        services.AddHostedService<EmbeddedWarmUp>();
        _ = resolver; // discarded; the DI-built instance is authoritative

        services.AddSingleton<ODataRowFilterParser>();

        // Signing key for skiptokens: derive from a stable per-process secret (embedded mode
        // has no JWT config); host apps can override by registering their own SkipTokenCodec.
        services.AddSingleton(sp =>
        {
            var seed = System.Text.Encoding.UTF8.GetBytes("ez-embedded-skiptoken-" + Environment.MachineName);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return new SkipTokenCodec(sha.ComputeHash(seed));
        });

        services.AddSingleton<ODataRequestHandler>();
        services.AddSingleton(sp =>
        {
            var rowFilter = sp.GetRequiredService<ODataRowFilterParser>();
            return new RestRequestHandler(
                sp.GetRequiredService<IServiceRuntimeResolver>(),
                sp.GetRequiredService<IConnectorRegistry>(),
                sp.GetRequiredService<PolicyEngine>(),
                sp.GetRequiredService<IPolicySource>(),
                (service, schema, version, identity) => rowFilter.Bind(service, schema, version, identity));
        });

        // Host identity bridge (spec 15 §3.1): ClaimsPrincipal → RequestIdentity via role names.
        // Registered in DI (not static) so multiple hosts in one process stay isolated.
        var roleResolver = builder.RoleResolver;
        services.AddSingleton<IEzIdentityFactory>(new DelegateIdentityFactory(context =>
        {
            if (context.User.Identity?.IsAuthenticated != true || roleResolver is null)
            {
                return RequestIdentity.Anonymous; // fail closed
            }

            var roleNames = roleResolver(context.User);
            var roleIds = policySource.ResolveRoleIds(roleNames);
            var claims = context.User.Claims.ToDictionary(c => c.Type, c => c.Value, StringComparer.OrdinalIgnoreCase);
            return new RequestIdentity
            {
                UserId = null,
                RoleIds = roleIds,
                Claims = claims,
            };
        }));

        return services;
    }
}
