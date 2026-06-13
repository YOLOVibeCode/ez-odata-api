using System.Diagnostics;
using System.Text.Json;
using EzOdata.Admin.Auth;
using EzOdata.Core.Audit;
using EzOdata.Core.RateLimiting;
using EzOdata.Data.Runtime;

namespace EzOdata.Host.Middleware;

/// <summary>
/// Data-plane guards (spec 02 §4 steps 6 + 10): rate limiting before the protocol
/// handler, audit tap after it. Runs only on /api/* data routes.
/// </summary>
public sealed class DataPlaneMiddleware
{
    private readonly RequestDelegate _next;
    private readonly TokenBucketRateLimiter _limiter;
    private readonly EfRateLimitPolicyProvider _policies;
    private readonly IAuditSink _audit;

    public DataPlaneMiddleware(
        RequestDelegate next, TokenBucketRateLimiter limiter,
        EfRateLimitPolicyProvider policies, IAuditSink audit)
    {
        _next = next;
        _limiter = limiter;
        _policies = policies;
        _audit = audit;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var identity = IdentityBuilder.Build(context.User);

        // ---- rate limiting (spec 08 §6) ----
        if (identity != EzOdata.Core.Policy.RequestIdentity.Anonymous)
        {
            var applicable = await _policies.ResolveAsync(identity, context.RequestAborted);
            var result = _limiter.Check(applicable);
            if (!result.Allowed)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString();
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new { code = "RateLimited", message = "Rate limit exceeded. Retry later." },
                });
                Audit(context, identity, 429, 0);
                return;
            }

            if (result.Remaining != int.MaxValue)
            {
                context.Response.Headers["RateLimit-Remaining"] = result.Remaining.ToString();
            }
        }

        // ---- audit tap (spec 08 §8) ----
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            Audit(context, identity, context.Response.StatusCode, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    private void Audit(HttpContext context, EzOdata.Core.Policy.RequestIdentity identity, int status, int durationMs)
    {
        var isRead = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);

        _audit.Record(new AuditEvent
        {
            RequestId = context.Items[RequestIdMiddleware.ItemKey] as string ?? "unknown",
            Category = isRead ? "data.read" : "data.write",
            Action = context.Request.Method.ToLowerInvariant(),
            Outcome = status switch
            {
                < 400 => "ok",
                401 or 403 or 404 or 429 => "denied",
                _ => "error",
            },
            AppId = identity.AppId,
            UserId = identity.UserId,
            Resource = context.Request.Path.Value,
            DurationMs = durationMs,
            DetailJson = JsonSerializer.Serialize(new
            {
                status,
                query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null,
            }),
        });
    }
}
