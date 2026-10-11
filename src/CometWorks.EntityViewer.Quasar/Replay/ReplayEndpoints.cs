using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quasar.Plugin.Abstractions.Security;

namespace CometWorks.EntityViewer.Quasar.Replay;

internal static class ReplayEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints, string prefix)
    {
        // Historical player positions are restricted to administrators, including direct URLs.
        var group = endpoints.MapGroup(prefix + "/api/replay").RequireAuthorization(QuasarPolicyNames.CanManageSecurity);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            try { return await next(context); }
            catch (KeyNotFoundException e) { return Results.NotFound(new { error = e.Message }); }
            catch (Exception e) when (e is ArgumentException or InvalidDataException) { return Results.BadRequest(new { error = e.Message }); }
        });
        group.MapGet("/servers", (ReplayHistoryService history) => Results.Json(history.Servers()));
        group.MapGet("/{serverId}/overview", (string serverId, ReplayHistoryService history) => Results.Json(history.Overview(serverId)));
        group.MapGet("/{serverId}", async (string serverId, long from, long to, ReplayHistoryService history, CancellationToken token) => Results.Json(await history.Query(serverId, from, to, token)));
    }
}
