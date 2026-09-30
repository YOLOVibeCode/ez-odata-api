using System.Reflection;
using EzOdata.Embedded;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace EzOdata.AspNetCore;

/// <summary>Which ez-odata API a mapped prefix serves.</summary>
public enum EzODataApi
{
    /// <summary><c>MapEzOData</c>: OData v4.</summary>
    OData,

    /// <summary><c>MapEzODataRest</c>: the REST/JSON dialect.</summary>
    Rest,
}

/// <summary>Route prefixes registered by <c>MapEzOData</c> / <c>MapEzODataRest</c>, for discovery (e.g. Swagger UI).</summary>
public sealed class EzODataEndpointCatalog
{
    private readonly List<(string Prefix, EzODataApi Api)> _entries = [];

    /// <summary>Every mapped (prefix, API) pair, in mapping order.</summary>
    public IReadOnlyList<(string Prefix, EzODataApi Api)> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    internal void Add(string prefix, EzODataApi api)
    {
        lock (_entries)
        {
            if (!_entries.Contains((prefix, api))) _entries.Add((prefix, api));
        }
    }
}

/// <summary>Options for <see cref="EzODataSwaggerUIExtensions.UseEzODataSwaggerUI"/>.</summary>
public sealed class EzODataSwaggerUIOptions
{
    /// <summary>Where the page is served. Default <c>swagger</c> (→ <c>/swagger</c>).</summary>
    public string RoutePrefix { get; set; } = "swagger";

    /// <summary>Browser tab title.</summary>
    public string DocumentTitle { get; set; } = "ez-odata API";

    /// <summary>Services to list. Empty (default): every service declared with <c>AddService</c>.</summary>
    public IList<string> Services { get; } = new List<string>();

    /// <summary>
    /// Prefixes to use when none were recorded by <c>MapEzOData</c>/<c>MapEzODataRest</c> (for example when the host
    /// maps endpoints without <c>AddEzOData</c>). Default: <c>/api/odata</c> (OData).
    /// </summary>
    public IList<(string Prefix, EzODataApi Api)> FallbackPrefixes { get; } = new List<(string, EzODataApi)> { ("/api/odata", EzODataApi.OData) };

    /// <summary>Escape hatch for any other Swagger UI setting.</summary>
    public Action<SwaggerUIOptions>? ConfigureSwaggerUI { get; set; }
}

/// <summary>A ready-to-use, branded Swagger UI over the OpenAPI documents ez-odata generates.</summary>
public static class EzODataSwaggerUIExtensions
{
    /// <summary>
    /// Serves Swagger UI (default <c>/swagger</c>) listing every declared service on every mapped ez-odata API, with
    /// "Try it out" on by default, a table filter, request timings and ez-odata styling. The OpenAPI documents are
    /// served with the same authorization as the data, so the page shows what the caller's role allows.
    /// </summary>
    /// <remarks>Consider enabling it only in non-production environments, or behind your own authorization.</remarks>
    public static IApplicationBuilder UseEzODataSwaggerUI(this IApplicationBuilder app, Action<EzODataSwaggerUIOptions>? configure = null)
    {
        var options = new EzODataSwaggerUIOptions();
        configure?.Invoke(options);
        var prefix = options.RoutePrefix.Trim('/');
        var stylesheet = $"/{prefix}/ez-odata-swagger.css";
        // Versioned URL: cached per release, never stale after an upgrade.
        var version = typeof(EzODataSwaggerUIExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1";

        var css = ReadStylesheet();
        app.Map(stylesheet, branch => branch.Run(async context =>
        {
            context.Response.ContentType = "text/css; charset=utf-8";
            context.Response.Headers.CacheControl = "public, max-age=3600";
            await context.Response.WriteAsync(css);
        }));

        var services = app.ApplicationServices;
        return app.UseSwaggerUI(ui =>
        {
            ui.RoutePrefix = prefix;
            ui.DocumentTitle = options.DocumentTitle;
            ui.ConfigObject.Urls = Documents(services, options); // enumerated per request: mapping order doesn't matter
            ui.DocExpansion(DocExpansion.List);
            ui.DefaultModelsExpandDepth(-1);  // request/response schemas are shown on each operation
            ui.EnableFilter();                // search box: filter operations by table
            ui.EnableTryItOutByDefault();
            ui.DisplayRequestDuration();
            ui.EnableDeepLinking();
            ui.ShowCommonExtensions();
            ui.InjectStylesheet($"{stylesheet}?v={Uri.EscapeDataString(version)}");
            options.ConfigureSwaggerUI?.Invoke(ui);
        });
    }

    private static IEnumerable<UrlDescriptor> Documents(IServiceProvider services, EzODataSwaggerUIOptions options)
    {
        var catalog = services.GetService<EzODataEndpointCatalog>();
        var prefixes = catalog?.Entries is { Count: > 0 } recorded ? recorded : options.FallbackPrefixes.ToList();
        var names = options.Services.Count > 0
            ? options.Services.ToList()
            : services.GetService<InMemoryServiceRuntimeResolver>()?.ServiceNames ?? [];

        foreach (var name in names)
        {
            foreach (var (apiPrefix, api) in prefixes.OrderBy(p => p.Api))
            {
                yield return new UrlDescriptor
                {
                    Url = $"{apiPrefix}/{Uri.EscapeDataString(name)}/openapi.json",
                    Name = $"{name} · {(api == EzODataApi.OData ? "OData v4" : "REST")}",
                };
            }
        }
    }

    private static string ReadStylesheet()
    {
        using var stream = typeof(EzODataSwaggerUIExtensions).Assembly.GetManifestResourceStream("EzOdata.AspNetCore.ez-odata-swagger.css")
            ?? throw new InvalidOperationException("Embedded Swagger UI stylesheet is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
