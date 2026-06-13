using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using EzOdata.Admin.Auth;
using EzOdata.Core.Audit;
using EzOdata.Mcp;

namespace EzOdata.Host;

/// <summary>
/// Hosts the MCP server at /mcp (spec 09): JSON-RPC over HTTP POST, authenticated by
/// API key, gated by the app's mcp_enabled flag, every call audited (category=mcp).
/// </summary>
public static class McpEndpoint
{
    public static void MapMcp(this WebApplication app)
    {
        app.MapGet("/mcp/health", async (HttpContext context, McpServer server) =>
        {
            // Reports tool availability for the presented key (spec 09 §7)
            if (context.User.Identity?.IsAuthenticated != true)
            {
                return Results.Json(new { ok = false, reason = "unauthenticated" }, statusCode: 401);
            }

            var identity = IdentityBuilder.Build(context.User);
            var listRequest = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 0, ["method"] = "tools/list" };
            var response = await server.HandleAsync(listRequest, identity, mcpAllowedForApp: true, context.RequestAborted);
            var count = (response?["result"]?["tools"] as JsonArray)?.Count ?? 0;
            return Results.Json(new { ok = true, toolsAvailableForKey = count });
        }).RequireAuthorization("McpAccess");

        app.MapPost("/mcp", McpPostAsync).RequireAuthorization("McpAccess");
        return;

        static async Task<IResult> McpPostAsync(HttpContext context, McpServer server, IAuditSink audit)
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                return Results.Json(
                    new { jsonrpc = "2.0", error = new { code = -32001, message = "Authentication required (X-API-Key)." } },
                    statusCode: 401);
            }

            // MCP requires an API key (app), not just a user JWT (spec 09 MCP-1).
            var appIdClaim = context.User.FindFirst(ApiKeyAuthenticationHandler.AppIdClaim);
            var mcpEnabled = context.User.FindFirst(ApiKeyAuthenticationHandler.McpEnabledClaim)?.Value == "true";
            if (appIdClaim is null)
            {
                return Results.Json(
                    new { jsonrpc = "2.0", error = new { code = -32001, message = "MCP requires an API key." } },
                    statusCode: 403);
            }

            JsonNode? request;
            try
            {
                request = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.Json(new { jsonrpc = "2.0", error = new { code = -32700, message = "Parse error." } });
            }

            if (request is null)
            {
                return Results.Json(new { jsonrpc = "2.0", error = new { code = -32600, message = "Invalid request." } });
            }

            var identity = IdentityBuilder.Build(context.User);
            var response = await server.HandleAsync(request, identity, mcpEnabled, context.RequestAborted);

            audit.Record(new AuditEvent
            {
                RequestId = context.Items["ez:request-id"] as string ?? "unknown",
                Category = "mcp",
                Action = request["method"]?.GetValue<string>() ?? "unknown",
                Outcome = response?["error"] is not null ? "error" : "ok",
                AppId = identity.AppId,
                UserId = identity.UserId,
                Resource = request["params"]?["name"]?.GetValue<string>(),
            });

            return response is null ? Results.NoContent() : Results.Json(response);
        }
    }
}
