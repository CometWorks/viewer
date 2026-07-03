using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Security;
using CometWorks.EntityViewer.Components;
using CometWorks.EntityViewer.Services;

namespace CometWorks.EntityViewer.Quasar;

public sealed class EntityViewerQuasarPlugin : IQuasarPlugin
{
    private const string CompanionPluginId = "cometworks.entityviewer";
    private const string GetEntitySceneOperation = "get-entity-scene";

    public string Id => "cometworks.entityviewer";

    public string DisplayName => "Entity Viewer";

    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context)
    {
        services.AddEntityViewerUi();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context)
    {
        var sceneEndpoint = endpoints.MapGet(
            "/_quasar/plugins/cometworks.entityviewer/api/entities/{serverId}/{entityId:long}/scene",
            async (
                string serverId,
                long entityId,
                HttpContext httpContext,
                IQuasarCompanionChannel companionChannel,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var request = new EntityViewerSceneRequest
                    {
                        EntityId = entityId,
                        IncludeVoxels = IsTrueLikeQueryFlag(httpContext.Request.Query["voxels"]),
                        IncludeContext = IsTrueLikeQueryFlag(httpContext.Request.Query["context"]),
                    };
                    var scene = await companionChannel.SendAsync<EntityViewerSceneRequest, JsonElement>(
                        serverId,
                        CompanionPluginId,
                        GetEntitySceneOperation,
                        request,
                        cancellationToken);
                    return Results.Json(scene);
                }
                catch (TimeoutException exception)
                {
                    return Results.Problem(exception.Message, statusCode: StatusCodes.Status504GatewayTimeout);
                }
                catch (InvalidOperationException exception)
                {
                    return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
                }
            });

        sceneEndpoint.RequireAuthorization(QuasarPolicyNames.CanView);
    }

    public IEnumerable<Assembly> GetRazorAssemblies()
    {
        yield return typeof(EntityViewerColumnCell).Assembly;
    }

    public IEnumerable<QuasarNavItem> GetNavItems()
    {
        yield break;
    }

    public IEnumerable<QuasarExtensionContribution> GetExtensions()
    {
        yield return new QuasarExtensionContribution(
            QuasarExtensionTargets.EntityViewerColumnHeader,
            typeof(EntityViewerColumnHeader),
            QuasarPatchMode.Replace,
            100,
            Id,
            QuasarPolicyNames.CanView);

        yield return new QuasarExtensionContribution(
            QuasarExtensionTargets.EntityViewerColumnCell,
            typeof(EntityViewerColumnCell),
            QuasarPatchMode.Replace,
            100,
            Id,
            QuasarPolicyNames.CanView);
    }

    private static bool IsTrueLikeQueryFlag(Microsoft.Extensions.Primitives.StringValues values)
    {
        if (values.Count == 0)
            return false;

        var value = values[0];
        return string.IsNullOrEmpty(value) ||
               string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EntityViewerSceneRequest
    {
        public long EntityId { get; init; }

        public bool IncludeVoxels { get; init; }

        public bool IncludeContext { get; init; }
    }
}
