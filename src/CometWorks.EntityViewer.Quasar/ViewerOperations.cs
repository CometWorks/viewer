using System.Globalization;
using System.Text.Json;
using CometWorks.EntityViewer.Quasar.Replay;
using CometWorks.EntityViewer.Quasar.Streaming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;

namespace CometWorks.EntityViewer.Quasar;

public sealed partial class EntityViewerQuasarPlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public sealed record SceneRequest(string ServerId, [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] long EntityId, bool Voxels = false, bool Context = false);
    public sealed record ResolveRequest(string SessionId, AssetResolveRequest Asset);
    public sealed record FileRequest(string AssetToken);

    private void RegisterOperations(IServiceCollection services)
    {
        services.AddQuasarOperation<SceneRequest, object>(Id, "scene.read", [QuasarPolicyNames.CanView], async (caller, input, token) =>
        {
            try
            {
                var scene = await caller.Services.GetRequiredService<IQuasarCompanionChannel>().SendAsync<EntityViewerSceneRequest, JsonElement>(
                    input.ServerId, CompanionPluginId, GetEntitySceneOperation,
                    new() { EntityId = input.EntityId, IncludeVoxels = input.Voxels, IncludeContext = input.Context }, token);
                return await ViewerAssetSessionProtocol.AttachAsync(caller, scene, input.ServerId,
                    input.EntityId.ToString(CultureInfo.InvariantCulture), ViewerAssetSessionProtocol.SceneMods(scene), token);
            }
            catch (TimeoutException error) { throw new QuasarOperationException(504, error.Message); }
            catch (InvalidOperationException error) { throw new QuasarOperationException(400, error.Message); }
        });
        services.AddQuasarOperation<JsonElement, AssetStreamingStatusResponse>(Id, "assets.status", [QuasarPolicyNames.CanView], ReadAssetStatusAsync);
        services.AddQuasarOperation<JsonElement, AssetStreamingRootSettingsResponse>(Id, "assets.roots.read", [QuasarPolicyNames.CanManageSecurity], ReadAssetRootsAsync);
        services.AddQuasarOperation<AssetStreamingConsentRequest, AssetStreamingStatusResponse>(Id, "assets.consent.update", [QuasarPolicyNames.CanManageSecurity], async (caller, input, token) =>
        {
            if (input.StreamingEnabled && !input.ConsentAccepted) throw new QuasarOperationException(400, "Consent must be accepted before server asset streaming can be enabled.");
            await caller.EnsureAuthorizedAsync(token);
            var settings = await caller.Services.GetRequiredService<IEntityViewerStreamingSettingsStore>().UpdateAsync(current =>
            {
                current.StreamingEnabled = input.StreamingEnabled && input.ConsentAccepted;
                current.ConsentAccepted = input.ConsentAccepted;
                current.ConsentVersion = input.ConsentAccepted ? EntityViewerStreamingSettings.CurrentConsentVersion : "";
                current.ConsentAcceptedAtUtc = input.ConsentAccepted ? DateTimeOffset.UtcNow : null;
                current.ConsentAcceptedByUserId = input.ConsentAccepted ? UserId(caller.User) : "";
                return current;
            }, token);
            return BuildAssetStreamingStatus(settings, caller.Services.GetRequiredService<EntityViewerStreamingPaths>(), true);
        });
        services.AddQuasarOperation<AssetStreamingRootSettingsRequest, AssetStreamingRootSettingsResponse>(Id, "assets.roots.update", [QuasarPolicyNames.CanManageSecurity], async (caller, input, token) =>
        {
            var paths = caller.Services.GetRequiredService<EntityViewerStreamingPaths>();
            await caller.EnsureAuthorizedAsync(token);
            var settings = await caller.Services.GetRequiredService<IEntityViewerStreamingSettingsStore>().UpdateAsync(current =>
            {
                current.BaseGameSourceMode = EntityViewerStreamingSettings.NormalizeSourceMode(input.BaseGameSourceMode);
                current.BaseGameContentPath = current.UsesManagedSteamCmd ? "" : paths.ToStoredPath(input.BaseGameContentPath);
                current.DedicatedServerModsPath = paths.ToStoredPath(input.DedicatedServerModsPath);
                return current;
            }, token);
            return BuildRootSettingsResponse(settings, paths);
        });
        services.AddQuasarOperation<JsonElement, SteamCmdInstallerStatusResponse>(Id, "assets.installer.status", [QuasarPolicyNames.CanManageSecurity],
            (caller, input, token) => Task.FromResult(caller.Services.GetRequiredService<SteamCmdInstallerService>().GetStatus()));
        services.AddQuasarOperation<SteamCmdInstallRequest, SteamCmdInstallerStatusResponse>(Id, "assets.installer.start", [QuasarPolicyNames.CanManageSecurity], async (caller, input, token) =>
        {
            var settings = await caller.Services.GetRequiredService<IEntityViewerStreamingSettingsStore>().GetAsync(token);
            if (!settings.UsesManagedSteamCmd) throw new QuasarOperationException(409, "SteamCMD install is disabled while manual Content mode is selected.");
            await caller.EnsureAuthorizedAsync(token);
            return await caller.Services.GetRequiredService<SteamCmdInstallerService>().StartAsync(input, automatic: false, token);
        });
        services.AddQuasarOperation<SteamCmdInputRequest, SteamCmdInstallerStatusResponse>(Id, "assets.installer.input", [QuasarPolicyNames.CanManageSecurity], async (caller, input, token) =>
        { await caller.EnsureAuthorizedAsync(token); return await caller.Services.GetRequiredService<SteamCmdInstallerService>().SendInputAsync(input, token); });
        services.AddQuasarOperation<JsonElement, SteamCmdInstallerStatusResponse>(Id, "assets.installer.cancel", [QuasarPolicyNames.CanManageSecurity], async (caller, input, token) =>
        { await caller.EnsureAuthorizedAsync(token); return await caller.Services.GetRequiredService<SteamCmdInstallerService>().CancelAsync(); });
        services.AddQuasarOperation<ResolveRequest, AssetResolveResponse>(Id, "assets.resolve", [QuasarPolicyNames.CanView], async (caller, input, token) =>
        {
            ArgumentNullException.ThrowIfNull(input.Asset);
            var user = UserId(caller.User);
            var store = caller.Services.GetRequiredService<ViewerAssetSessionStore>();
            var session = store.TryGetSession(input.SessionId, user) ?? throw new QuasarOperationException(404, "Asset session not found or expired.");
            if (!store.TryBeginResolve(session, out var resolveLease)) throw new QuasarOperationException(429, "Asset resolve limit reached.");
            using var resolving = resolveLease;
            var asset = await caller.Services.GetRequiredService<ServerAssetResolver>().ResolveAsync(session, input.Asset, token);
            if (asset is null) return new() { Found = false, LogicalPath = input.Asset.LogicalPath, Message = "Asset not found." };
            await RequireStreamingAsync(caller, token);
            await caller.EnsureAuthorizedAsync(token);
            var access = store.CreateAssetToken(user, session.Id, asset) ?? throw new QuasarOperationException(404, "Asset session not found or expired.");
            return new() { Found = true, AssetToken = access.Id, CacheKey = asset.CacheKeyForUser(user), ContentHash = asset.ContentHash,
                ExpiresAtUtc = access.ExpiresAtUtc, LogicalPath = asset.LogicalPath, RootId = asset.RootId, RootKind = asset.RootKind,
                Size = asset.Size, LastModifiedUtc = asset.LastModifiedUtc, ContentType = asset.ContentType };
        });
        services.AddQuasarRead<FileRequest>(Id, "assets.file.read", [QuasarPolicyNames.CanView], async (caller, input, token) =>
        {
            var store = caller.Services.GetRequiredService<ViewerAssetSessionStore>();
            var user = UserId(caller.User);
            async Task Validate(CancellationToken cancellation)
            {
                await RequireStreamingAsync(caller, cancellation);
                if (store.TryGetAssetToken(input.AssetToken, user) is null) throw new QuasarOperationException(404, "Asset not found or expired.");
            }
            await Validate(token);
            var asset = store.TryGetAssetToken(input.AssetToken, user)!.Asset;
            return new(_ =>
            {
                var access = store.TryGetAssetToken(input.AssetToken, user) ?? throw new QuasarOperationException(404, "Asset token expired.");
                if (!store.TryBeginStream(access, out var streamLease)) throw new QuasarOperationException(429, "Asset stream limit reached.");
                try { return Task.FromResult(asset.OpenRead(streamLease)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { streamLease!.Dispose(); throw new QuasarOperationException(404, "Asset not found."); }
            }, asset.ContentType, Length: asset.Size, EnableRanges: !asset.IsArchiveEntry,
                LastModified: asset.LastModifiedUtc, EntityTag: "\"" + asset.CacheKeyForUser(user) + "\"", ValidateAsync: Validate);
        });
        ReplayEndpoints.Register(services, Id);
    }

    private static async Task RequireStreamingAsync(QuasarOperationContext caller, CancellationToken token)
    {
        var settings = await caller.Services.GetRequiredService<IEntityViewerStreamingSettingsStore>().GetAsync(token);
        if (!settings.StreamingEnabled || !settings.HasCurrentConsent) throw new QuasarOperationException(409, "Server asset streaming is not enabled.");
    }

    private static void MapOperations(IEndpointRouteBuilder endpoints, string prefix)
    {
        endpoints.MapQuasarSdkClient(prefix + "/_sdk/client.js");
        endpoints.MapQuasarOperation(prefix + "/api/entities/{serverId}/{entityId:long}/scene", ["GET"], CompanionPluginId, "scene.read", http =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new SceneRequest(http.Request.RouteValues["serverId"]!.ToString()!,
                long.Parse(http.Request.RouteValues["entityId"]!.ToString()!, CultureInfo.InvariantCulture),
                IsTrueLikeQueryFlag(http.Request.Query["voxels"]), IsTrueLikeQueryFlag(http.Request.Query["context"])))));
        foreach (var route in new (string Path, string Method, string Operation)[] {
            ("status", "GET", "assets.status"), ("settings/consent", "POST", "assets.consent.update"),
            ("settings/roots", "GET", "assets.roots.read"), ("settings/roots", "POST", "assets.roots.update"),
            ("installer/status", "GET", "assets.installer.status"), ("installer/start", "POST", "assets.installer.start"),
            ("installer/input", "POST", "assets.installer.input"), ("installer/cancel", "POST", "assets.installer.cancel") })
            endpoints.MapQuasarOperation(prefix + "/api/assets/" + route.Path, [route.Method], CompanionPluginId, route.Operation, route.Operation == "assets.installer.cancel"
                ? _ => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { })) : null);
        endpoints.MapQuasarOperation(prefix + "/api/assets/sessions/{sessionId}/resolve", ["POST"], CompanionPluginId, "assets.resolve", async http =>
            JsonSerializer.SerializeToElement(new ResolveRequest(http.Request.RouteValues["sessionId"]!.ToString()!,
                (await http.ReadQuasarRequestAsync()).Deserialize<AssetResolveRequest>(JsonOptions) ?? throw new JsonException())));
        endpoints.MapQuasarOperation(prefix + "/api/assets/files/{assetToken}", ["GET"], CompanionPluginId, "assets.file.read", http =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new FileRequest(http.Request.RouteValues["assetToken"]!.ToString()!))));
        ReplayEndpoints.Map(endpoints, prefix);
    }
}
