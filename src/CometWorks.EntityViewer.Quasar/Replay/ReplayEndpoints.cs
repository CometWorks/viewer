using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quasar.Plugin.Abstractions.Security;
using CometWorks.EntityViewer.Replay;
using CometWorks.EntityViewer.Quasar.Streaming;

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
        group.MapPut("/{serverId}/recording", async (string serverId, ReplayRecordingSettings settings, ReplayHistoryService history, CancellationToken token) => Results.Json(await history.SetRecording(serverId, settings, token)));
        group.MapGet("/{serverId}", async (string serverId, long from, long to, HttpContext httpContext,
            ReplayHistoryService history, IEntityViewerStreamingSettingsStore settingsStore,
            ViewerAssetSessionStore sessionStore, CancellationToken token) =>
        {
            var batches = await history.Query(serverId, from, to, token);
            var settings = await settingsStore.GetAsync(token);
            if (settings.StreamingEnabled && settings.HasCurrentConsent)
            {
                var session = sessionStore.CreateSession(
                    ViewerAssetSessionProtocol.UserId(httpContext.User),
                    history.AgentId(serverId),
                    string.Empty,
                    ViewerAssetSessionProtocol.ReplayMods(batches));
                ViewerAssetSessionProtocol.WriteHeaders(httpContext.Response, session);
            }
            return Results.Json(batches);
        });
    }
}
