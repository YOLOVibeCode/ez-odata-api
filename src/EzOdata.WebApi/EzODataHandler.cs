using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EzOdata.Core.Policy;
using EzOdata.Core.Protocol;
using EzOdata.OData;

namespace EzOdata.WebApi;

/// <summary>
/// Classic ASP.NET host adapter (spec 15 EMB-9): an <see cref="HttpMessageHandler"/>
/// translating Web API 2 / OWIN messages to the host-agnostic engine contract — the
/// same engine binaries that power the modern hosts, running on .NET Framework 4.8.
///
/// Wire-up (Web API 2):
/// <code>
/// var engine = EzODataEngine.Create(ez => { ez.AddService(...); ez.AddRole(...); ez.ResolveRolesBy(...); });
/// config.Routes.MapHttpRoute(
///     name: "ezodata",
///     routeTemplate: "api/odata/{service}/{*odataPath}",
///     defaults: null,
///     constraints: null,
///     handler: new EzODataHandler(engine));
/// </code>
/// </summary>
public sealed class EzODataHandler : HttpMessageHandler
{
    private readonly EzODataEngine _engine;

    public EzODataHandler(EzODataEngine engine) => _engine = engine;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Route values: {service} and optional {*odataPath} from the Web API route.
        var routeValues = request.GetRouteData();
        if (!routeValues.TryGetValue("service", out var serviceValue) || serviceValue is not string service)
        {
            return Plain(HttpStatusCode.NotFound, "Route must contain a {service} segment.");
        }

        routeValues.TryGetValue("odataPath", out var pathValue);

        var identity = _engine.ResolveIdentity(GetPrincipal(request));

        var engineRequest = new EngineRequest
        {
            Method = request.Method.Method,
            Path = (pathValue as string ?? string.Empty).TrimStart('/'),
            QueryString = request.RequestUri?.Query.TrimStart('?') ?? string.Empty,
            ServiceRoot = BuildServiceRoot(request, service, pathValue as string),
            Headers = request.Headers
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
            Body = request.Content is null ? null : await request.Content.ReadAsStreamAsync().ConfigureAwait(false),
            Identity = identity,
        };

        var response = await _engine.Handler.HandleAsync(service, engineRequest, ct).ConfigureAwait(false);

        var result = new HttpResponseMessage((HttpStatusCode)response.StatusCode);
        if (response.Body is { Length: > 0 })
        {
            result.Content = new ByteArrayContent(response.Body);
            if (response.ContentType is { } contentType)
            {
                result.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
        }

        foreach (var header in response.Headers)
        {
            result.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return result;
    }

    private static ClaimsPrincipal GetPrincipal(HttpRequestMessage request)
    {
        // Web API 2 stores the principal in request context properties; OWIN in the owin env.
        if (request.Properties.TryGetValue("MS_RequestContext", out var contextObj)
            && contextObj?.GetType().GetProperty("Principal")?.GetValue(contextObj) is ClaimsPrincipal fromContext)
        {
            return fromContext;
        }

        return Thread.CurrentPrincipal as ClaimsPrincipal ?? new ClaimsPrincipal(new ClaimsIdentity());
    }

    private static Uri BuildServiceRoot(HttpRequestMessage request, string service, string? odataPath)
    {
        var uri = request.RequestUri ?? new Uri("http://localhost/");
        var absolute = uri.GetLeftPart(UriPartial.Path);
        var trim = odataPath ?? string.Empty;
        if (trim.Length > 0 && absolute.EndsWith(trim, StringComparison.Ordinal))
        {
            absolute = absolute.Substring(0, absolute.Length - trim.Length);
        }

        if (!absolute.EndsWith("/", StringComparison.Ordinal)) absolute += "/";
        return new Uri(absolute);
    }

    private static HttpResponseMessage Plain(HttpStatusCode status, string message) =>
        new(status) { Content = new StringContent(message) };
}

internal static class RouteDataExtensions
{
    /// <summary>Route values without a compile-time Web API dependency (reflection over MS_HttpRouteData).</summary>
    public static IDictionary<string, object?> GetRouteData(this HttpRequestMessage request)
    {
        if (request.Properties.TryGetValue("MS_HttpRouteData", out var routeData) && routeData is not null)
        {
            var valuesProperty = routeData.GetType().GetProperty("Values");
            if (valuesProperty?.GetValue(routeData) is IDictionary<string, object?> values)
            {
                return values;
            }
        }

        // Fallback: parse /{prefix}/{service}/{rest} positionally from the URI.
        var segments = (request.RequestUri?.AbsolutePath ?? "/")
            .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var odataIndex = Array.FindIndex(segments, s => s.Equals("odata", StringComparison.OrdinalIgnoreCase));
        if (odataIndex >= 0 && odataIndex + 1 < segments.Length)
        {
            result["service"] = segments[odataIndex + 1];
            result["odataPath"] = string.Join("/", segments.Skip(odataIndex + 2));
        }

        return result;
    }
}
