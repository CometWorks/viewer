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
    private const string PluginRoutePrefix = "/_quasar/plugins/cometworks.entityviewer";
    private const string StaticWebAssetsRoutePrefix = "/_content/CometWorks.EntityViewer";
    private static readonly string[] ViewerRoutePrefixes = [PluginRoutePrefix, StaticWebAssetsRoutePrefix];

    public string Id => "cometworks.entityviewer";

    public string DisplayName => "Entity Viewer";

    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context)
    {
        services.AddEntityViewerUi();
        services.AddSingleton(new EntityViewerStreamingPaths(context));
        services.AddSingleton<IEntityViewerStreamingSettingsStore, FileEntityViewerStreamingSettingsStore>();
        services.AddSingleton<SteamCmdInstallerService>();
        services.AddSingleton<ViewerAssetSessionStore>();
        services.AddSingleton<ServerAssetResolver>();
        services.AddHostedService<SteamCmdHourlyUpdateService>();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context)
    {
        foreach (var routePrefix in ViewerRoutePrefixes)
        {
            var sceneEndpoint = MapSceneEndpoint(endpoints, routePrefix);
            sceneEndpoint.RequireAuthorization(QuasarPolicyNames.CanView);
        }

        foreach (var routePrefix in ViewerRoutePrefixes)
            MapAssetStreamingEndpoints(endpoints, routePrefix);
    }

    private static RouteHandlerBuilder MapSceneEndpoint(IEndpointRouteBuilder endpoints, string routePrefix)
    {
        return endpoints.MapGet(
            $"{routePrefix}/api/entities/{{serverId}}/{{entityId:long}}/scene",
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

    private static void MapAssetStreamingEndpoints(IEndpointRouteBuilder endpoints, string routePrefix)
    {
        var statusEndpoint = endpoints.MapGet(
            $"{routePrefix}/api/assets/status",
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
            $"{routePrefix}/api/assets/settings/consent",
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

        var rootSettingsGetEndpoint = endpoints.MapGet(
            $"{routePrefix}/api/assets/settings/roots",
            async (
                IEntityViewerStreamingSettingsStore settingsStore,
                EntityViewerStreamingPaths paths,
                CancellationToken cancellationToken) =>
            {
                var settings = await settingsStore.GetAsync(cancellationToken);
                return Results.Json(BuildRootSettingsResponse(settings, paths));
            });

        rootSettingsGetEndpoint.RequireAuthorization(QuasarPolicyNames.CanManageSecurity);

        var rootSettingsSaveEndpoint = endpoints.MapPost(
            $"{routePrefix}/api/assets/settings/roots",
            async (
                AssetStreamingRootSettingsRequest request,
                IEntityViewerStreamingSettingsStore settingsStore,
                EntityViewerStreamingPaths paths,
                CancellationToken cancellationToken) =>
            {
                var mode = NormalizeBaseGameSourceMode(request.BaseGameSourceMode);
                var settings = await settingsStore.UpdateAsync(current =>
                {
                    current.BaseGameSourceMode = mode;
                    current.BaseGameContentPath = NormalizeOptionalPath(request.BaseGameContentPath);
                    current.DedicatedServerModsPath = NormalizeOptionalPath(request.DedicatedServerModsPath);
                    return current;
                }, cancellationToken);
                return Results.Json(BuildRootSettingsResponse(settings, paths));
            });

        rootSettingsSaveEndpoint.RequireAuthorization(QuasarPolicyNames.CanManageSecurity);

        var installerStatusEndpoint = endpoints.MapGet(
            $"{routePrefix}/api/assets/installer/status",
            (SteamCmdInstallerService installerService) => Results.Json(installerService.GetStatus()));

        installerStatusEndpoint.RequireAuthorization(QuasarPolicyNames.CanManageSecurity);

        var installerStartEndpoint = endpoints.MapPost(
            $"{routePrefix}/api/assets/installer/start",
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
            $"{routePrefix}/api/assets/installer/input",
            async (
                SteamCmdInputRequest request,
                SteamCmdInstallerService installerService) =>
            {
                var status = await installerService.SendInputAsync(request);
                return Results.Json(status);
            });

        installerInputEndpoint.RequireAuthorization(QuasarPolicyNames.CanManageSecurity);

        var installerCancelEndpoint = endpoints.MapPost(
            $"{routePrefix}/api/assets/installer/cancel",
            async (SteamCmdInstallerService installerService) =>
            {
                var status = await installerService.CancelAsync();
                return Results.Json(status);
            });

        installerCancelEndpoint.RequireAuthorization(QuasarPolicyNames.CanManageSecurity);

        var sessionEndpoint = endpoints.MapPost(
            $"{routePrefix}/api/assets/sessions",
            async (
                AssetSessionRequest request,
                HttpContext httpContext,
                IEntityViewerStreamingSettingsStore settingsStore,
                ViewerAssetSessionStore sessionStore,
                CancellationToken cancellationToken) =>
            {
                var settings = await settingsStore.GetAsync(cancellationToken);
                if (!settings.StreamingEnabled || !settings.HasCurrentConsent)
                    return Results.Problem("Server asset streaming is not enabled.", statusCode: StatusCodes.Status409Conflict);

                var session = sessionStore.CreateSession(UserId(httpContext.User), request);
                return Results.Json(new AssetSessionResponse
                {
                    SessionId = session.Id,
                    ExpiresAtUtc = session.ExpiresAtUtc,
                });
            });

        sessionEndpoint.RequireAuthorization(QuasarPolicyNames.CanView);

        var resolveEndpoint = endpoints.MapPost(
            $"{routePrefix}/api/assets/sessions/{{sessionId}}/resolve",
            async (
                string sessionId,
                AssetResolveRequest request,
                HttpContext httpContext,
                ViewerAssetSessionStore sessionStore,
                ServerAssetResolver resolver,
                CancellationToken cancellationToken) =>
            {
                var userId = UserId(httpContext.User);
                var session = sessionStore.TryGetSession(sessionId, userId);
                if (session is null)
                    return Results.NotFound(new AssetResolveResponse { Found = false, Message = "Asset session not found or expired." });

                var asset = await resolver.ResolveAsync(session, request, cancellationToken);
                if (asset is null)
                    return Results.Json(new AssetResolveResponse { Found = false, LogicalPath = request.LogicalPath, Message = "Asset not found." });

                var token = sessionStore.CreateAssetToken(userId, session.Id, asset);
                return Results.Json(new AssetResolveResponse
                {
                    Found = true,
                    AssetToken = token.Id,
                    ExpiresAtUtc = token.ExpiresAtUtc,
                    LogicalPath = asset.LogicalPath,
                    RootId = asset.RootId,
                    RootKind = asset.RootKind,
                    Size = asset.Size,
                    LastModifiedUtc = asset.LastModifiedUtc,
                    ContentType = asset.ContentType,
                });
            });

        resolveEndpoint.RequireAuthorization(QuasarPolicyNames.CanView);

        var fileEndpoint = endpoints.MapGet(
            $"{routePrefix}/api/assets/files/{{assetToken}}",
            (string assetToken, HttpContext httpContext, ViewerAssetSessionStore sessionStore) =>
            {
                var token = sessionStore.TryGetAssetToken(assetToken, UserId(httpContext.User));
                if (token is null)
                    return Results.NotFound();

                try
                {
                    var asset = token.Asset;
                    var stream = asset.OpenRead();
                    return Results.File(
                        stream,
                        contentType: asset.ContentType,
                        lastModified: asset.LastModifiedUtc,
                        enableRangeProcessing: !asset.IsArchiveEntry);
                }
                catch (FileNotFoundException)
                {
                    return Results.NotFound();
                }
                catch (DirectoryNotFoundException)
                {
                    return Results.NotFound();
                }
            });

        fileEndpoint.RequireAuthorization(QuasarPolicyNames.CanView);
    }

    private static AssetStreamingStatusResponse BuildAssetStreamingStatus(
        EntityViewerStreamingSettings settings,
        EntityViewerStreamingPaths paths,
        bool canManageStreaming)
    {
        var consentAccepted = settings.HasCurrentConsent;
        var streamingEnabled = settings.StreamingEnabled && consentAccepted;
        var managedContent = EntityViewerContentRoots.SelectManaged(paths);
        var externalProbe = EntityViewerContentRoots.Probe(settings.BaseGameContentPath);
        var externalContentConfigured = !string.IsNullOrWhiteSpace(settings.BaseGameContentPath) &&
                                        externalProbe.IsUsable;
        var baseGameContentConfigured = string.Equals(settings.BaseGameSourceMode, "ExternalInstall", StringComparison.OrdinalIgnoreCase)
            ? externalContentConfigured
            : managedContent.IsUsable;
        var activeContentDirectory = string.Equals(settings.BaseGameSourceMode, "ExternalInstall", StringComparison.OrdinalIgnoreCase)
            ? settings.BaseGameContentPath
            : managedContent.ContentDirectory;
        var contentMessage = string.Equals(settings.BaseGameSourceMode, "ExternalInstall", StringComparison.OrdinalIgnoreCase)
            ? externalProbe.Message
            : managedContent.Message;

        return new AssetStreamingStatusResponse
        {
            Mode = streamingEnabled ? "server-pending" : "local",
            StreamingEnabled = streamingEnabled,
            ConsentAccepted = consentAccepted,
            ConsentRequired = !consentAccepted,
            ConsentVersion = EntityViewerStreamingSettings.CurrentConsentVersion,
            CanManageStreaming = canManageStreaming,
            FileStreamingReady = baseGameContentConfigured,
            BaseGameSourceMode = string.IsNullOrWhiteSpace(settings.BaseGameSourceMode)
                ? "ManagedSteamCmd"
                : settings.BaseGameSourceMode,
            BaseGameContentConfigured = baseGameContentConfigured,
            ManagedGameContentExists = managedContent.ClientProbe.IsUsable,
            ManagedDedicatedServerContentExists = managedContent.DedicatedServerProbe.IsUsable,
            ActiveBaseGameContentDirectory = activeContentDirectory,
            ManagedContentSource = managedContent.Source,
            BaseGameContentMessage = contentMessage,
            LastInstallStatus = string.IsNullOrWhiteSpace(settings.LastInstallStatus)
                ? "NotStarted"
                : settings.LastInstallStatus,
            Message = StatusMessage(streamingEnabled, consentAccepted, canManageStreaming, baseGameContentConfigured, contentMessage),
            ConsentAcceptedAtUtc = settings.ConsentAcceptedAtUtc,
        };
    }

    private static AssetStreamingRootSettingsResponse BuildRootSettingsResponse(
        EntityViewerStreamingSettings settings,
        EntityViewerStreamingPaths paths)
    {
        var mode = string.IsNullOrWhiteSpace(settings.BaseGameSourceMode)
            ? "ManagedSteamCmd"
            : settings.BaseGameSourceMode;
        var managedContent = EntityViewerContentRoots.SelectManaged(paths);
        var externalProbe = EntityViewerContentRoots.Probe(settings.BaseGameContentPath);
        var externalContentConfigured = !string.IsNullOrWhiteSpace(settings.BaseGameContentPath) &&
                                        externalProbe.IsUsable;
        var baseGameContentConfigured = mode.Equals("ExternalInstall", StringComparison.OrdinalIgnoreCase)
            ? externalContentConfigured
            : managedContent.IsUsable;
        var activeContentDirectory = mode.Equals("ExternalInstall", StringComparison.OrdinalIgnoreCase)
            ? settings.BaseGameContentPath
            : managedContent.ContentDirectory;
        var contentMessage = mode.Equals("ExternalInstall", StringComparison.OrdinalIgnoreCase)
            ? externalProbe.Message
            : managedContent.Message;

        return new AssetStreamingRootSettingsResponse
        {
            BaseGameSourceMode = mode,
            BaseGameContentPath = settings.BaseGameContentPath,
            DedicatedServerModsPath = settings.DedicatedServerModsPath,
            ManagedGameClientDirectory = paths.ManagedGameClientDirectory,
            ManagedGameContentDirectory = paths.ManagedGameContentDirectory,
            ManagedDedicatedServerContentDirectory = paths.ManagedDedicatedServerContentDirectory,
            ActiveBaseGameContentDirectory = activeContentDirectory,
            ManagedContentSource = managedContent.Source,
            BaseGameContentConfigured = baseGameContentConfigured,
            ManagedGameContentExists = managedContent.ClientProbe.IsUsable,
            ManagedDedicatedServerContentExists = managedContent.DedicatedServerProbe.IsUsable,
            BaseGameContentMessage = contentMessage,
            DedicatedServerModsPathExists = !string.IsNullOrWhiteSpace(settings.DedicatedServerModsPath) &&
                                             Directory.Exists(settings.DedicatedServerModsPath),
        };
    }

    private static string StatusMessage(
        bool streamingEnabled,
        bool consentAccepted,
        bool canManageStreaming,
        bool baseGameContentConfigured,
        string contentMessage)
    {
        if (streamingEnabled)
        {
            return baseGameContentConfigured
                ? "Server asset streaming is enabled."
                : $"Server asset streaming is enabled, but the Space Engineers Content folder is not ready. {contentMessage}";
        }

        if (!consentAccepted && canManageStreaming)
            return "Server asset streaming is disabled until the server owner accepts consent.";

        if (!consentAccepted)
            return "Server asset streaming is disabled by the server owner.";

        return "Server asset streaming is disabled. Local asset folders remain active.";
    }

    private static string NormalizeBaseGameSourceMode(string mode)
    {
        return string.Equals(mode, "ExternalInstall", StringComparison.OrdinalIgnoreCase)
            ? "ExternalInstall"
            : "ManagedSteamCmd";
    }

    private static string NormalizeOptionalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return Path.GetFullPath(path.Trim());
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
