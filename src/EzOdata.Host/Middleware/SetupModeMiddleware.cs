using EzOdata.Admin;
using EzOdata.Data;
using Microsoft.AspNetCore.Mvc;

namespace EzOdata.Host.Middleware;

/// <summary>
/// While no admin user exists, only /system/setup and health endpoints respond;
/// everything else returns 503 with a setup hint (spec 03 §4).
/// </summary>
public sealed class SetupModeMiddleware
{
    private readonly RequestDelegate _next;

    public SetupModeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, SetupState setupState, SystemDbContext db)
    {
        var path = context.Request.Path;

        // Setup endpoint, health, and the admin SPA's own assets must always load so the
        // setup wizard can render; only other API surfaces are gated (spec 03 §4).
        var isApi = path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp")
                    || (path.StartsWithSegments("/system") && !path.StartsWithSegments("/system/setup"));

        if (!isApi || path.StartsWithSegments("/healthz"))
        {
            await _next(context);
            return;
        }

        if (!await setupState.IsCompleteAsync(db, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Instance not set up.",
                Detail = "Complete first-run setup via POST /system/setup.",
            });
            return;
        }

        await _next(context);
    }
}
