using Microsoft.AspNetCore.Mvc;

namespace EzOdata.Host.Middleware;

/// <summary>
/// Converts unhandled exceptions to RFC 9457 problem details; never leaks internals
/// (spec 02 §4, §9). OData routes get the OData error format when those land (Phase 1).
/// </summary>
public sealed class ExceptionShieldMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionShieldMiddleware> _logger;

    public ExceptionShieldMiddleware(RequestDelegate next, ILogger<ExceptionShieldMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to write.
        }
        catch (Exception ex)
        {
            var requestId = context.Items[RequestIdMiddleware.ItemKey] as string ?? "unknown";
            _logger.LogError(ex, "Unhandled exception for {Method} {Path} (request {RequestId})",
                context.Request.Method, context.Request.Path, requestId);

            if (context.Response.HasStarted) throw;

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Extensions = { ["requestId"] = requestId },
            });
        }
    }
}
