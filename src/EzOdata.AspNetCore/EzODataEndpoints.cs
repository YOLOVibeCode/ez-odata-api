using EzOdata.Core.Policy;
using EzOdata.Core.Protocol;
using EzOdata.OData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EzOdata.AspNetCore;

/// <summary>Host-supplied ClaimsPrincipal → RequestIdentity bridge (spec 15 EMB-4 seam).
/// Registered in DI so multiple hosts in one process never share identity logic.</summary>
public interface IEzIdentityFactory
{
    RequestIdentity Create(HttpContext context);
}

public sealed class DelegateIdentityFactory : IEzIdentityFactory
{
    private readonly Func<HttpContext, RequestIdentity> _factory;

    public DelegateIdentityFactory(Func<HttpContext, RequestIdentity> factory) => _factory = factory;

    public RequestIdentity Create(HttpContext context) => _factory(context);
}

/// <summary>
/// ASP.NET Core host adapter (spec 02 §5.2): mechanical translation between
/// HttpContext and the engine contract. No protocol logic lives here.
/// </summary>
public static class EzODataEndpoints
{
    internal static RequestIdentity ResolveIdentity(HttpContext context) =>
        context.RequestServices.GetService<IEzIdentityFactory>()?.Create(context)
        ?? RequestIdentity.Anonymous; // fail closed when no factory is registered

    public static IEndpointConventionBuilder MapEzOData(this IEndpointRouteBuilder endpoints, string prefix = "/api/odata")
    {
        var trimmed = "/" + prefix.Trim('/');

        return endpoints.Map(trimmed + "/{service}/{**odataPath}", HandleAsync);
    }

    public static IEndpointConventionBuilder MapEzODataRest(this IEndpointRouteBuilder endpoints, string prefix = "/api/rest")
    {
        var trimmed = "/" + prefix.Trim('/');
        // openapi.json served by the OData handler's docs generator (REST dialect)
        endpoints.Map(trimmed + "/{service}/openapi.json", async context =>
        {
            if (context.User.Identity?.IsAuthenticated != true) { await WriteUnauthorizedAsync(context); return; }
            var handler = context.RequestServices.GetRequiredService<OData.ODataRequestHandler>();
            var service = (string)context.Request.RouteValues["service"]!;
            var response = await handler.HandleOpenApiAsync(
                service, BuildRequest(context, "openapi.json"), Docs.ApiDialect.Rest, context.RequestAborted);
            await WriteResponseAsync(context, response);
        });
        return endpoints.Map(trimmed + "/{service}/{**restPath}", HandleRestAsync);
    }

    private static async Task HandleRestAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await WriteUnauthorizedAsync(context);
            return;
        }

        var handler = context.RequestServices.GetRequiredService<Rest.RestRequestHandler>();
        var service = (string)context.Request.RouteValues["service"]!;
        var restPath = context.Request.RouteValues["restPath"] as string ?? "";

        var engineRequest = BuildRequest(context, restPath);
        var response = await handler.HandleAsync(service, engineRequest, context.RequestAborted);
        await WriteResponseAsync(context, response);
    }

    private static EngineRequest BuildRequest(HttpContext context, string path)
    {
        var headers = context.Request.Headers.ToDictionary(
            h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var serviceRoot = new Uri($"{context.Request.Scheme}://{context.Request.Host}/");

        return new EngineRequest
        {
            Method = context.Request.Method,
            Path = path,
            QueryString = context.Request.QueryString.HasValue ? context.Request.QueryString.Value!.TrimStart('?') : "",
            ServiceRoot = serviceRoot,
            Headers = headers,
            Body = context.Request.Body,
            Identity = ResolveIdentity(context),
        };
    }

    private static async Task WriteResponseAsync(HttpContext context, EngineResponse response)
    {
        context.Response.StatusCode = response.StatusCode;
        foreach (var header in response.Headers) context.Response.Headers[header.Key] = header.Value;
        if (response.Body is { Length: > 0 })
        {
            context.Response.ContentType = response.ContentType ?? "application/json";
            await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
        }
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new
        {
            error = new { code = "Unauthorized", message = "Authenticate with a JWT bearer token or X-API-Key." },
        });
    }

    private static async Task HandleAsync(HttpContext context)
    {
        // Data plane requires authentication (spec 08 §2): anonymous → 401 + challenge.
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = "Unauthorized", message = "Authenticate with a JWT bearer token or X-API-Key." },
            });
            return;
        }

        var handler = context.RequestServices.GetRequiredService<ODataRequestHandler>();

        var service = (string)context.Request.RouteValues["service"]!;
        var odataPath = context.Request.RouteValues["odataPath"] as string ?? "";

        // External service root, proxy-aware via UseForwardedHeaders upstream.
        var pathBase = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "";
        var routePattern = context.Request.Path.Value ?? "";
        var rootPath = routePattern.Substring(0, routePattern.Length - odataPath.Length).TrimEnd('/');
        var serviceRoot = new Uri($"{context.Request.Scheme}://{context.Request.Host}{pathBase}{rootPath}/");

        var headers = context.Request.Headers.ToDictionary(
            h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var engineRequest = new EngineRequest
        {
            Method = context.Request.Method,
            Path = odataPath,
            QueryString = context.Request.QueryString.HasValue
                ? context.Request.QueryString.Value!.TrimStart('?')
                : "",
            ServiceRoot = serviceRoot,
            Headers = headers,
            Body = context.Request.Body,
            Identity = ResolveIdentity(context),
        };

        var response = await handler.HandleAsync(service, engineRequest, context.RequestAborted);

        context.Response.StatusCode = response.StatusCode;
        foreach (var header in response.Headers)
        {
            context.Response.Headers[header.Key] = header.Value;
        }

        if (response.Body is { Length: > 0 })
        {
            context.Response.ContentType = response.ContentType ?? "application/json";
            await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
        }
    }
}
