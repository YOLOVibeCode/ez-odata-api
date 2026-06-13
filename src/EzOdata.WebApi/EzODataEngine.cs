using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EzOdata.Connectors.Abstractions;
using EzOdata.Core.Policy;
using EzOdata.Embedded;
using EzOdata.OData;
using Microsoft.Extensions.Logging.Abstractions;

namespace EzOdata.WebApi;

/// <summary>
/// Composition root for .NET Framework 4.8 hosts (spec 15 EMB-9): builds the engine
/// from the same fluent configuration as the modern adapter — no DI container required.
/// Call <see cref="Create"/> once at app start (e.g. Global.asax Application_Start).
/// </summary>
public sealed class EzODataEngine
{
    private readonly InMemoryPolicySource _policySource;
    private readonly Func<ClaimsPrincipal, IReadOnlyList<string>>? _roleResolver;

    private EzODataEngine(
        ODataRequestHandler handler, InMemoryPolicySource policySource,
        Func<ClaimsPrincipal, IReadOnlyList<string>>? roleResolver)
    {
        Handler = handler;
        _policySource = policySource;
        _roleResolver = roleResolver;
    }

    public ODataRequestHandler Handler { get; }

    public static EzODataEngine Create(Action<EzOdataBuilder> configure)
    {
        var builder = new EzOdataBuilder();
        configure(builder);

        var registry = new ConnectorRegistry(builder.Connectors);
        var resolver = new InMemoryServiceRuntimeResolver(builder.Services, registry);
        var policySource = new InMemoryPolicySource(builder.Roles);
        var models = new EdmModelFactory();
        var rowFilterParser = new ODataRowFilterParser(models);

        byte[] skipKey;
        using (var sha = SHA256.Create())
        {
            skipKey = sha.ComputeHash(Encoding.UTF8.GetBytes("ez-net48-skiptoken-" + Environment.MachineName));
        }

        var handler = new ODataRequestHandler(
            resolver, registry, models, new SkipTokenCodec(skipKey),
            new PolicyEngine(), policySource, rowFilterParser);

        // Synchronous warm-up: classic ASP.NET has no hosted-service hook; Application_Start
        // is the natural call site and blocking there is conventional.
        resolver.IntrospectAllAsync(NullLogger.Instance, default).GetAwaiter().GetResult();

        return new EzODataEngine(handler, policySource, builder.RoleResolver);
    }

    /// <summary>Host ClaimsPrincipal → RequestIdentity (fail closed without a resolver).</summary>
    public RequestIdentity ResolveIdentity(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || _roleResolver is null)
        {
            return RequestIdentity.Anonymous;
        }

        var roleIds = _policySource.ResolveRoleIds(_roleResolver(principal));
        return new RequestIdentity
        {
            RoleIds = roleIds,
            Claims = principal.Claims
                .GroupBy(c => c.Type, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase),
        };
    }
}
