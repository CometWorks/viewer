using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using CometWorks.EntityViewer.Replay;

namespace CometWorks.EntityViewer.Quasar.Replay;

internal static class ReplayEndpoints
{
    internal sealed record Request(string ServerId, long From = 0, long To = 0, ReplayRecordingSettings? Settings = null);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Register(IServiceCollection services, string pluginId)
    {
        Add("replay.servers", (history, input, token) => Task.FromResult<object>(history.Servers()));
        Add("replay.overview", (history, input, token) => Task.FromResult<object>(history.Overview(input.ServerId)));
        Add("replay.query", async (history, input, token) => await history.Query(input.ServerId, input.From, input.To, token));
        Add("replay.recording.update", async (history, input, token) => await history.SetRecording(input.ServerId,
            input.Settings ?? throw new ArgumentException("Recording settings are required."), token), write: true);

        void Add(string operation, Func<ReplayHistoryService, Request, CancellationToken, Task<object>> handler, bool write = false) =>
            services.AddQuasarOperation<Request, object>(pluginId, operation, [QuasarPolicyNames.CanManageSecurity], async (caller, input, token) =>
            {
                try
                {
                    if (write) await caller.EnsureAuthorizedAsync(token);
                    return await handler(caller.Services.GetRequiredService<ReplayHistoryService>(), input, token);
                }
                catch (KeyNotFoundException error) { throw new QuasarOperationException(404, error.Message); }
                catch (Exception error) when (error is ArgumentException or InvalidDataException)
                { throw new QuasarOperationException(400, error.Message); }
            });
    }

    public static void Map(IEndpointRouteBuilder endpoints, string prefix)
    {
        const string plugin = "cometworks.entityviewer";
        var api = prefix + "/api/replay";
        endpoints.MapQuasarOperation(api + "/servers", ["GET"], plugin, "replay.servers");
        endpoints.MapQuasarOperation(api + "/{serverId}/overview", ["GET"], plugin, "replay.overview", http =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new Request(Server(http)))));
        endpoints.MapQuasarOperation(api + "/{serverId}/recording", ["PUT"], plugin, "replay.recording.update", async http =>
            JsonSerializer.SerializeToElement(new Request(Server(http), Settings:
                (await http.ReadQuasarRequestAsync()).Deserialize<ReplayRecordingSettings>(JsonOptions))));
        endpoints.MapQuasarOperation(api + "/{serverId}", ["GET"], plugin, "replay.query", http =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new Request(Server(http),
                long.Parse(http.Request.Query["from"].ToString(), CultureInfo.InvariantCulture),
                long.Parse(http.Request.Query["to"].ToString(), CultureInfo.InvariantCulture)))));
    }

    private static string Server(HttpContext http) => http.Request.RouteValues["serverId"]!.ToString()!;
}
