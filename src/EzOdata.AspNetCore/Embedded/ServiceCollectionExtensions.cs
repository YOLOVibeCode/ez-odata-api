using System.Security.Claims;
using EzOdata.Connectors.Abstractions;
using EzOdata.Embedded;
using EzOdata.Core.Policy;
using EzOdata.Core.Time;
using EzOdata.OData;
using EzOdata.Rest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        // --- Dev no-auth: fail fast outside Development --------------------------------
        if (builder.DevNoAuth)
        {
            // Validation runs at configure time using IHostEnvironment registered in DI.
            services.AddSingleton<IHostedService>(sp =>
            {
                var env = sp.GetRequiredService<IHostEnvironment>();
                if (!env.IsDevelopment())
                {
                    throw new InvalidOperationException(
                        "EzOData: AllowAnonymousInDevelopment() is set but " +
                        $"ASPNETCORE_ENVIRONMENT is '{env.EnvironmentName}'. " +
                        "This option is only permitted in the Development environment. " +
                        "Remove it or set the environment to Development.");
                }

                var log = sp.GetRequiredService<ILogger<EzOdataBuilder>>();
                log.LogWarning(
                    "======================================================================\n" +
                    "  EzOData dev no-auth is ACTIVE: all requests get full bypass access.\n" +
                    "  This must never run in production.\n" +
                    "======================================================================");
                return new NoOpHostedService();
            });
        }

        services.AddSingleton<ISystemClock, SystemClock>();
        services.AddSingleton<IConnectorRegistry>(new ConnectorRegistry(builder.Connectors));
        services.AddSingleton<EdmModelFactory>();
        services.AddSingleton<PolicyEngine>();

        var policySource = new InMemoryPolicySource(builder.Roles);
        services.AddSingleton<IPolicySource>(policySource);
        services.AddSingleton(policySource);

        services.AddSingleton<InMemoryServiceRuntimeResolver>(sp =>
            new InMemoryServiceRuntimeResolver(builder.Services, sp.GetRequiredService<IConnectorRegistry>()));
        services.AddSingleton<IServiceRuntimeResolver>(sp => sp.GetRequiredService<InMemoryServiceRuntimeResolver>());
        services.AddHostedService<EmbeddedWarmUp>();

        services.AddSingleton<ODataRowFilterParser>();

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

        // --- Identity factory: claim mapping + dev bypass ------------------------------
        // Capture the role-resolution strategy chosen in the builder.
        var customResolver = builder.RoleResolver;
        var roleClaimType = builder.RoleClaimType;
        var roleClaimTransform = builder.RoleClaimTransform;
        var devNoAuth = builder.DevNoAuth;

        services.AddSingleton<IEzIdentityFactory>(new DelegateIdentityFactory(context =>
        {
            // Dev no-auth: unauthenticated requests get full bypass.
            if (devNoAuth && context.User.Identity?.IsAuthenticated != true)
            {
                // The env guard is enforced at startup via the hosted service above.
                return RequestIdentity.DevBypass;
            }

            if (context.User.Identity?.IsAuthenticated != true)
            {
                return RequestIdentity.Anonymous; // fail closed
            }

            // Resolve role names using whichever strategy was configured.
            IReadOnlyList<string> roleNames;
            if (customResolver is not null)
            {
                roleNames = customResolver(context.User);
            }
            else if (roleClaimType is not null)
            {
                roleNames = context.User.Claims
                    .Where(c => c.Type == roleClaimType)
                    .Select(c => roleClaimTransform is null ? c.Value : roleClaimTransform(c.Value))
                    .Where(n => n is not null)
                    .Select(n => n!)
                    .ToList();
            }
            else
            {
                roleNames = [];
            }

            var roleIds = policySource.ResolveRoleIds(roleNames);

            // Populate all claims so @identity.* row filters resolve.
            var claims = context.User.Claims
                .GroupBy(c => c.Type, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

            // Map sub/NameIdentifier to a numeric userId if present.
            long? userId = null;
            var sub = context.User.FindFirst("sub")?.Value ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (sub is not null && long.TryParse(sub, out var uid)) userId = uid;

            // Always expose sub and email in the claims dict for row filters.
            if (sub is not null) claims["sub"] = sub;
            var email = context.User.FindFirst("email")?.Value ?? context.User.FindFirst(ClaimTypes.Email)?.Value;
            if (email is not null) claims["email"] = email;

            return new RequestIdentity
            {
                UserId = userId,
                Email = email,
                RoleIds = roleIds,
                Claims = claims,
            };
        }));

        return services;
    }

    private sealed class NoOpHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
