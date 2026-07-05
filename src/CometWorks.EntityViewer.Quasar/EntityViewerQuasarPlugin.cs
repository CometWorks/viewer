using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using CometWorks.EntityViewer.Quasar.Streaming;
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
        services.AddSingleton(new EntityViewerStreamingPaths(context));
        services.AddSingleton<IEntityViewerStreamingSettingsStore, FileEntityViewerStreamingSettingsStore>();
        services.AddSingleton<SteamCmdInstallerService>();
        services.AddHostedService<SteamCmdHourlyUpdateService>();
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

        MapAssetStreamingEndpoints(endpoints);
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

    private static void MapAssetStreamingEndpoints(IEndpointRouteBuilder endpoints)
    {
        var statusEndpoint = endpoints.MapGet(
            "/_quasar/plugins/cometworks.entityviewer/api/assets/status",
            async (
                HttpContext httpContext,
                IEntityViewerStreamingSettingsStore settingsStore,
                EntityViewerStreamingPaths paths,
                IAuthorizationService authorizationService,
                CancellationToken cancellationToken) =>
            {
                var settings = await settingsStore.GetAsync(cancellationToken);
                var canManage = (await authorizationService.AuthorizeAsync(
                    httpContext.User,
                    resource: null,
                    QuasarPolicyNames.CanManageSecurity)).Succeeded;
                return Results.Json(BuildAssetStreamingStatus(settings, paths, canManage));
            });

        statusEndpoint.RequireAuthorization(QuasarPolicyNames.CanView);

        var consentEndpoint = endpoints.MapPost(
            "/_quasar/plugins/cometworks.entityviewer/api/assets/settings/consent",
            async (
                AssetStreamingConsentRequest request,
                HttpContext httpContext,
                IEntityViewerStreamingSettingsStore settingsStore,
                EntityViewerStreamingPaths paths,
                CancellationToken cancellationToken) =>
            {
                if (request.StreamingEnabled && !request.ConsentAccepted)
                    return Results.BadRequest(new { error = "Consent must be accepted before server asset streaming can be enabled." });

                var userId = UserId(httpContext.User);
                var settings = await settingsStore.UpdateAsync(current =>
                {
                    current.StreamingEnabled = request.StreamingEnabled && request.ConsentAccepted;
                    if (request.ConsentAccepted)
                    {
                        current.ConsentAccepted = true;
                        current.ConsentVersion = EntityViewerStreamingSettings.CurrentConsentVersion;
                        current.ConsentAcceptedAtUtc = DateTimeOffset.UtcNow;
                        current.ConsentAcceptedByUserId = userId;
                    }
                    else
                    {
                        current.ConsentAccepted = false;
                        current.ConsentVersion = string.Empty;
                        current.ConsentAcceptedAtUtc = null;
                        current.ConsentAcceptedByUserId = string.Empty;
                        current.StreamingEnabled = false;
                    }

                    return current;
                }, cancellationToken);

                return Results.Json(BuildAssetStreamingStatus(settings, paths, canManageStreaming: true));
            });

        consentEndpoint.RequireAuthorization(QuasarPolicyNames.CanManageSecurity);

        var installerStatusEndpoint = endpoints.MapGet(
            "/_quasar/plugins/cometworks.entityviewer/api/assets/installer/status",
            (SteamCmdInstallerService installerService) => Results.Json(installerService.GetStatus()));

        installerStatusEndpoint.RequireAuthorization(QuasarPolicyNames.CanManageSecurity);

        var installerStartEndpoint = endpoints.MapPost(
            "/_quasar/plugins/cometworks.entityviewer/api/assets/installer/start",
            async (
                SteamCmdInstallRequest request,
                SteamCmdInstallerService installerService,
                CancellationToken cancellationToken) =>
            {
                var status = await installerService.StartAsync(request, automatic: false, cancellationToken);
                return Results.Json(status);
            });

        installerStartEndpoint.RequireAuthorization(QuasarPolicyNames.CanManageSecurity);

        var installerInputEndpoint = endpoints.MapPost(
            "/_quasar/plugins/cometworks.entityviewer/api/assets/installer/input",
            async (
                SteamCmdInputRequest request,
                SteamCmdInstallerService installerService) =>
            {
                var status = await installerService.SendInputAsync(request);
                return Results.Json(status);
            });

        installerInputEndpoint.RequireAuthorization(QuasarPolicyNames.CanManageSecurity);

        var installerCancelEndpoint = endpoints.MapPost(
            "/_quasar/plugins/cometworks.entityviewer/api/assets/installer/cancel",
            async (SteamCmdInstallerService installerService) =>
            {
                var status = await installerService.CancelAsync();
                return Results.Json(status);
            });

        installerCancelEndpoint.RequireAuthorization(QuasarPolicyNames.CanManageSecurity);
    }

    private static AssetStreamingStatusResponse BuildAssetStreamingStatus(
        EntityViewerStreamingSettings settings,
        EntityViewerStreamingPaths paths,
        bool canManageStreaming)
    {
        var consentAccepted = settings.HasCurrentConsent;
        var streamingEnabled = settings.StreamingEnabled && consentAccepted;
        var managedContentExists = LooksLikeContentFolder(paths.ManagedGameContentDirectory);
        var externalContentConfigured = !string.IsNullOrWhiteSpace(settings.BaseGameContentPath) &&
                                        LooksLikeContentFolder(settings.BaseGameContentPath);
        var baseGameContentConfigured = string.Equals(settings.BaseGameSourceMode, "ExternalInstall", StringComparison.OrdinalIgnoreCase)
            ? externalContentConfigured
            : managedContentExists;

        return new AssetStreamingStatusResponse
        {
            Mode = streamingEnabled ? "server-pending" : "local",
            StreamingEnabled = streamingEnabled,
            ConsentAccepted = consentAccepted,
            ConsentRequired = !consentAccepted,
            ConsentVersion = EntityViewerStreamingSettings.CurrentConsentVersion,
            CanManageStreaming = canManageStreaming,
            FileStreamingReady = false,
            BaseGameSourceMode = string.IsNullOrWhiteSpace(settings.BaseGameSourceMode)
                ? "ManagedSteamCmd"
                : settings.BaseGameSourceMode,
            BaseGameContentConfigured = baseGameContentConfigured,
            ManagedGameContentExists = managedContentExists,
            LastInstallStatus = string.IsNullOrWhiteSpace(settings.LastInstallStatus)
                ? "NotStarted"
                : settings.LastInstallStatus,
            Message = StatusMessage(streamingEnabled, consentAccepted, canManageStreaming),
            ConsentAcceptedAtUtc = settings.ConsentAcceptedAtUtc,
        };
    }

    private static string StatusMessage(bool streamingEnabled, bool consentAccepted, bool canManageStreaming)
    {
        if (streamingEnabled)
            return "Server asset streaming setup is enabled. Local folders remain active until file streaming endpoints are ready.";

        if (!consentAccepted && canManageStreaming)
            return "Server asset streaming is disabled until the server owner accepts consent.";

        if (!consentAccepted)
            return "Server asset streaming is disabled by the server owner.";

        return "Server asset streaming is disabled. Local asset folders remain active.";
    }

    private static bool LooksLikeContentFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        return Directory.Exists(Path.Combine(path, "Data")) &&
               Directory.Exists(Path.Combine(path, "Models")) &&
               Directory.Exists(Path.Combine(path, "Textures"));
    }

    private static string UserId(ClaimsPrincipal user)
    {
        return user.FindFirstValue(ClaimTypes.NameIdentifier) ??
               user.FindFirstValue("sub") ??
               user.Identity?.Name ??
               "unknown";
    }

    private sealed class EntityViewerSceneRequest
    {
        public long EntityId { get; init; }

        public bool IncludeVoxels { get; init; }

        public bool IncludeContext { get; init; }
    }
}
