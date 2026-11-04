using CometWorks.EntityViewer.Quasar.Replay;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using CometWorks.EntityViewer.Quasar.Streaming;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using CometWorks.EntityViewer.Components;
using CometWorks.EntityViewer.Services;

namespace CometWorks.EntityViewer.Quasar;

public sealed partial class EntityViewerQuasarPlugin : IQuasarPlugin
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
        services.AddSingleton(provider => new ReplayHistoryService(context, provider.GetRequiredService<IQuasarCompanionChannel>(), provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ReplayHistoryService>>()));
        services.AddHostedService(provider => provider.GetRequiredService<ReplayHistoryService>());
        services.AddSingleton(new EntityViewerStreamingPaths(context));
        services.AddSingleton<IEntityViewerStreamingSettingsStore, FileEntityViewerStreamingSettingsStore>();
        services.AddSingleton<SteamCmdInstallerService>();
        services.AddSingleton<ViewerAssetSessionStore>();
        services.AddSingleton<ServerAssetResolver>();
        services.AddHostedService<SteamCmdHourlyUpdateService>();
        RegisterOperations(services);
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context)
    {
        foreach (var prefix in ViewerRoutePrefixes) MapOperations(endpoints, prefix);
    }

    public IEnumerable<Assembly> GetRazorAssemblies()
    {
        yield return typeof(EntityViewerColumnCell).Assembly;
    }

    public IEnumerable<QuasarNavItem> GetNavItems()
    {
        yield return new QuasarNavItem("Entity history", PluginRoutePrefix + "/index.html?view=history", ViewerIcons.History, QuasarNavZones.Tools, 100, QuasarPolicyNames.CanManageSecurity);
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

    private static async Task<AssetStreamingStatusResponse> ReadAssetStatusAsync(QuasarOperationContext caller,
        JsonElement request, CancellationToken token)
    {
        var settings = await caller.Services.GetRequiredService<IEntityViewerStreamingSettingsStore>().GetAsync(token);
        var paths = caller.Services.GetRequiredService<EntityViewerStreamingPaths>();
        var canManage = (await caller.Services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(caller.User, null, QuasarPolicyNames.CanManageSecurity)).Succeeded;
        return BuildAssetStreamingStatus(settings, paths, canManage);
    }

    private static async Task<AssetStreamingRootSettingsResponse> ReadAssetRootsAsync(QuasarOperationContext caller,
        JsonElement request, CancellationToken token)
    {
        var settings = await caller.Services.GetRequiredService<IEntityViewerStreamingSettingsStore>().GetAsync(token);
        return BuildRootSettingsResponse(settings, caller.Services.GetRequiredService<EntityViewerStreamingPaths>());
    }

    private static AssetStreamingStatusResponse BuildAssetStreamingStatus(
        EntityViewerStreamingSettings settings,
        EntityViewerStreamingPaths paths,
        bool canManageStreaming)
    {
        var consentAccepted = settings.HasCurrentConsent;
        var streamingEnabled = settings.StreamingEnabled && consentAccepted;
        var content = EntityViewerContentRoots.Select(settings, paths);
        var baseGameContentConfigured = content.IsUsable;
        var activeContentDirectory = content.ContentDirectory;
        var contentMessage = content.Message;

        return new AssetStreamingStatusResponse
        {
            Mode = streamingEnabled ? "server-pending" : "local",
            StreamingEnabled = streamingEnabled,
            ConsentAccepted = consentAccepted,
            ConsentRequired = !consentAccepted,
            ConsentVersion = EntityViewerStreamingSettings.CurrentConsentVersion,
            CanManageStreaming = canManageStreaming,
            FileStreamingReady = baseGameContentConfigured,
            BaseGameSourceMode = EntityViewerStreamingSettings.NormalizeSourceMode(settings.BaseGameSourceMode),
            BaseGameContentConfigured = baseGameContentConfigured,
            ManagedGameContentExists = content.ClientProbe.IsUsable,
            ManagedDedicatedServerContentExists = content.DedicatedServerProbe.IsUsable,
            ActiveBaseGameContentDirectory = canManageStreaming ? activeContentDirectory : "",
            ManagedContentSource = content.Source,
            BaseGameContentMessage = canManageStreaming ? contentMessage : baseGameContentConfigured ? "Space Engineers Content is ready." : "Space Engineers Content is not ready.",
            LastInstallStatus = string.IsNullOrWhiteSpace(settings.LastInstallStatus)
                ? "NotStarted"
                : settings.LastInstallStatus,
            Message = StatusMessage(streamingEnabled, consentAccepted, canManageStreaming, baseGameContentConfigured, canManageStreaming ? contentMessage : "Space Engineers Content is not ready."),
            ConsentAcceptedAtUtc = settings.ConsentAcceptedAtUtc,
        };
    }

    private static AssetStreamingRootSettingsResponse BuildRootSettingsResponse(
        EntityViewerStreamingSettings settings,
        EntityViewerStreamingPaths paths)
    {
        var content = EntityViewerContentRoots.Select(settings, paths);
        var baseGameContentConfigured = content.IsUsable;
        var activeContentDirectory = content.ContentDirectory;
        var contentMessage = content.Message;
        var configuredModsPath = paths.ResolveConfiguredPath(settings.DedicatedServerModsPath);

        return new AssetStreamingRootSettingsResponse
        {
            BaseGameSourceMode = EntityViewerStreamingSettings.NormalizeSourceMode(settings.BaseGameSourceMode),
            BaseGameContentPath = settings.BaseGameContentPath,
            DedicatedServerModsPath = settings.DedicatedServerModsPath,
            ManagedGameClientDirectory = paths.ManagedGameClientDirectory,
            ManagedGameContentDirectory = paths.ManagedGameContentDirectory,
            ManagedDedicatedServerContentDirectory = paths.ManagedDedicatedServerContentDirectory,
            ActiveBaseGameContentDirectory = activeContentDirectory,
            ManagedContentSource = content.Source,
            BaseGameContentConfigured = baseGameContentConfigured,
            ManagedGameContentExists = content.ClientProbe.IsUsable,
            ManagedDedicatedServerContentExists = content.DedicatedServerProbe.IsUsable,
            BaseGameContentMessage = contentMessage,
            DedicatedServerModsPathExists = Directory.Exists(configuredModsPath),
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
