namespace EzOdata.Host.Middleware;

/// <summary>Echoes/creates X-Request-Id and stores it for logging + audit correlation (spec 02 §4).</summary>
public sealed class RequestIdMiddleware
{
    public const string HeaderName = "X-Request-Id";
    public const string ItemKey = "ez:request-id";

    private readonly RequestDelegate _next;

    public RequestIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var requestId = !string.IsNullOrWhiteSpace(incoming) && incoming.Length <= 64
            ? incoming
            : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = requestId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        await _next(context);
    }
}
