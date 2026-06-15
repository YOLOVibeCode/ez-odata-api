using System.Security.Cryptography;
using System.Text;
using EzOdata.Admin;
using EzOdata.Admin.Auth;
using EzOdata.Admin.Controllers;
using EzOdata.Admin.Services;
using EzOdata.AspNetCore;
using EzOdata.Connectors.Abstractions;
using EzOdata.Connectors.PostgreSql;
using EzOdata.Core.Policy;
using EzOdata.Core.Security;
using EzOdata.Core.Time;
using EzOdata.Data;
using EzOdata.Data.Runtime;
using EzOdata.Data.Security;
using EzOdata.Host;
using EzOdata.Host.Middleware;
using EzOdata.OData;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration (fail fast; spec 12 §4) ----
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionPath).Get<JwtOptions>() ?? new JwtOptions();
if (jwtOptions.Error() is { } jwtError) throw new InvalidOperationException(jwtError);
var masterKey = HostConfig.LoadMasterKey(builder.Configuration);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionPath));
builder.Services.Configure<LockoutOptions>(builder.Configuration.GetSection(LockoutOptions.SectionPath));

// ---- System database (SQLite default; PostgreSQL planned for prod) ----
var systemDbProvider = builder.Configuration["SystemDatabase:Provider"] ?? "sqlite";
var systemDbConnection = builder.Configuration["SystemDatabase:ConnectionString"] ?? "Data Source=ezodata-system.db";
builder.Services.AddDbContext<SystemDbContext>(options =>
{
    switch (systemDbProvider.ToLowerInvariant())
    {
        case "sqlite":
            options.UseSqlite(systemDbConnection);
            break;
        case "postgres":
        case "postgresql":
            options.UseNpgsql(systemDbConnection);
            break;
        default:
            throw new InvalidOperationException(
                $"Unsupported SystemDatabase:Provider '{systemDbProvider}' (supported: sqlite, postgres).");
    }
});

// ---- Core services ----
builder.Services.AddSingleton<ISystemClock, SystemClock>();
builder.Services.AddSingleton<IPasswordHasher>(new Argon2PasswordHasher());
builder.Services.AddSingleton<ISecretProtector>(new AesGcmEnvelopeProtector(masterKey));
builder.Services.AddSingleton(new PasswordPolicy(CommonPasswords.List));
builder.Services.AddSingleton<SetupState>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ServiceConnectionCodec>();

// ---- Data plane: connectors, schema cache, OData engine ----
builder.Services.AddSingleton<IConnectorRegistry>(new ConnectorRegistry([
    PostgreSqlConnector.Create(),
    EzOdata.Connectors.MySql.MySqlConnectorFactory.Create(),
    EzOdata.Connectors.SqlServer.SqlServerConnector.Create(),
    EzOdata.Connectors.Sqlite.SqliteConnector.Create(),
]));
builder.Services.AddSingleton<EdmModelFactory>();
builder.Services.AddSingleton(new SkipTokenCodec(
    SHA256.HashData(Encoding.UTF8.GetBytes("ez-skiptoken:" + jwtOptions.SigningKey))));
builder.Services.AddSingleton<EfServiceRuntimeResolver>();
builder.Services.AddSingleton<IServiceRuntimeResolver>(sp => sp.GetRequiredService<EfServiceRuntimeResolver>());
builder.Services.AddSingleton<ODataRequestHandler>();
builder.Services.AddHostedService<IntrospectionWorker>();

// ---- Security: policy engine + sources (spec 08) ----
builder.Services.AddSingleton<PolicyEngine>();
builder.Services.AddSingleton<EfPolicySource>();
builder.Services.AddSingleton<IPolicySource>(sp => sp.GetRequiredService<EfPolicySource>());
builder.Services.AddSingleton<ODataRowFilterParser>();
builder.Services.AddSingleton<IEzIdentityFactory>(
    new DelegateIdentityFactory(context => IdentityBuilder.Build(context.User)));

// REST dialect engine, sharing the policy + connector pipeline (spec 06).
builder.Services.AddSingleton<EzOdata.Rest.RestRequestHandler>(sp =>
{
    var rowFilterParser = sp.GetRequiredService<ODataRowFilterParser>();
    return new EzOdata.Rest.RestRequestHandler(
        sp.GetRequiredService<IServiceRuntimeResolver>(),
        sp.GetRequiredService<IConnectorRegistry>(),
        sp.GetRequiredService<PolicyEngine>(),
        sp.GetRequiredService<IPolicySource>(),
        (service, schema, version, identity) => rowFilterParser.Bind(service, schema, version, identity));
});

// ---- Audit + rate limiting (spec 08 §6, §8) ----
builder.Services.AddSingleton<BufferedAuditPipeline>();
builder.Services.AddSingleton<EzOdata.Core.Audit.IAuditSink>(sp => sp.GetRequiredService<BufferedAuditPipeline>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BufferedAuditPipeline>());
builder.Services.AddSingleton<EzOdata.Core.RateLimiting.TokenBucketRateLimiter>();
builder.Services.AddSingleton<EfRateLimitPolicyProvider>();

// ---- Redis (optional, spec 12 §3 / NFR-5): multi-node rate limits + cache invalidation ----
var redisConnection = builder.Configuration["Redis:ConnectionString"];
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnection));
}

// ---- MCP server (spec 09) ----
var mcpOptions = builder.Configuration.GetSection("Mcp").Get<EzOdata.Mcp.McpOptions>() ?? new EzOdata.Mcp.McpOptions();
builder.Services.AddSingleton(mcpOptions);
builder.Services.AddSingleton<EzOdata.Mcp.McpServer>(sp =>
{
    var rowFilterParser = sp.GetRequiredService<ODataRowFilterParser>();
    return new EzOdata.Mcp.McpServer(
        sp.GetRequiredService<IServiceRuntimeResolver>(),
        sp.GetRequiredService<IConnectorRegistry>(),
        sp.GetRequiredService<PolicyEngine>(),
        sp.GetRequiredService<IPolicySource>(),
        (service, schema, version, identity) => rowFilterParser.Bind(service, schema, version, identity),
        mcpOptions,
        async ct =>
        {
            using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
            return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                db.Services.Where(s => s.Status == EzOdata.Core.Services.ServiceStatus.Active
                                       || s.Status == EzOdata.Core.Services.ServiceStatus.Refreshing)
                           .Select(s => s.Name), ct);
        });
});

// ---- Dev no-auth (Auth:DevNoAuth): bypass identity for all requests ----
// Hard guard: if the flag is set outside Development, refuse to start immediately.
var devNoAuth = builder.Configuration.GetValue<bool>("Auth:DevNoAuth");
if (devNoAuth && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        $"Auth:DevNoAuth is enabled but ASPNETCORE_ENVIRONMENT is '{builder.Environment.EnvironmentName}'. " +
        "This option is only permitted in the Development environment and must never be used in production.");
}

// ---- AuthN/AuthZ ----
var authBuilder = builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Resolved lazily so JwtTokenService owns the parameter construction in one place.
        var tokenService = new JwtTokenService(Options.Create(jwtOptions), new SystemClock());
        options.TokenValidationParameters = tokenService.ValidationParameters();
        options.MapInboundClaims = false;
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationOptions.Scheme, _ => { });

if (devNoAuth)
{
    authBuilder.AddScheme<DevBypassAuthenticationOptions, DevBypassAuthenticationHandler>(
        DevBypassAuthenticationOptions.Scheme, _ => { });
}

builder.Services.AddMemoryCache();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AdminPolicy.Name, AdminPolicy.Build());

    var dataSchemes = devNoAuth
        ? new[] { JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationOptions.Scheme, DevBypassAuthenticationOptions.Scheme }
        : new[] { JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationOptions.Scheme };

    // Data plane accepts JWT users, API keys, and (dev-only) the bypass scheme.
    options.AddPolicy("DataAccess", policy => policy
        .AddAuthenticationSchemes(dataSchemes)
        .RequireAuthenticatedUser());
    // MCP authenticates with the same schemes; endpoint enforces API-key requirement (spec 09 MCP-1).
    options.AddPolicy("McpAccess", policy => policy
        .AddAuthenticationSchemes(dataSchemes)
        .RequireAuthenticatedUser());
});

builder.Services.AddControllers().AddApplicationPart(typeof(SetupController).Assembly);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<SystemDbContext>("system-db");

// ---- Telemetry (spec 12 §6): Prometheus metrics + OTel traces ----
var prometheusEnabled = builder.Configuration.GetValue("Telemetry:PrometheusEnabled", true);
builder.Services.AddOpenTelemetry()
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation();
        if (prometheusEnabled) m.AddPrometheusExporter();
    })
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation();
        if (builder.Configuration["Telemetry:OtlpEndpoint"] is { Length: > 0 } otlp)
        {
            t.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
        }
    });

var app = builder.Build();

// Dev no-auth warning banner (logged after Build so the real logger is available).
if (devNoAuth)
{
    var devLog = app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
    devLog.LogWarning(
        "======================================================================\n" +
        "  EzOData dev no-auth is ACTIVE (Auth:DevNoAuth=true).\n" +
        "  All data-plane requests have full bypass access.\n" +
        "  This MUST NEVER be enabled in production.\n" +
        "======================================================================");
}

// ---- Migrate + master-key probe (spec 02 §11, 08 §9) ----
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SystemDbContext>();
    // SQLite ships with EF migrations; PostgreSQL bootstraps via EnsureCreated for v1
    // (provider-specific migration sets are a fast-follow).
    if (systemDbProvider.StartsWith("postgres", StringComparison.OrdinalIgnoreCase))
    {
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        await db.Database.MigrateAsync();
    }
}

await HostConfig.ValidateMasterKeyProbeAsync(app.Services, CancellationToken.None);

// ---- Pipeline (spec 02 §4 order) ----
app.UseMiddleware<ExceptionShieldMiddleware>();
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<SetupModeMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<DataPlaneMiddleware>();

app.MapHealthChecks("/healthz/ready");
app.MapGet("/healthz/live", () => Results.Ok(new { status = "ok" }));
if (prometheusEnabled) app.MapPrometheusScrapingEndpoint("/metrics");
app.MapControllers();

// Serve the admin SPA (built into wwwroot) with client-side routing fallback.
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
app.MapEzOData("/api/odata").RequireAuthorization("DataAccess");
app.MapEzODataRest("/api/rest").RequireAuthorization("DataAccess");
if (mcpOptions.Enabled)
{
    app.MapMcp();
}

// SPA fallback: non-API routes return index.html so the React router can take over.
app.MapFallback(context =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/api") || path.StartsWith("/system") || path.StartsWith("/mcp") || path.StartsWith("/healthz"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    var indexPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "index.html");
    if (File.Exists(indexPath))
    {
        context.Response.ContentType = "text/html";
        return context.Response.SendFileAsync(indexPath);
    }

    context.Response.StatusCode = StatusCodes.Status404NotFound;
    return Task.CompletedTask;
});

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
