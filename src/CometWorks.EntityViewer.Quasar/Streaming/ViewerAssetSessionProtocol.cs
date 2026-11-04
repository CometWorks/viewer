using System.Security.Claims;
using System.Text.Json;
using CometWorks.EntityViewer.Replay;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;

namespace CometWorks.EntityViewer.Quasar.Streaming;

internal static class ViewerAssetSessionProtocol
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record Grant(string SessionId, DateTimeOffset ExpiresAtUtc);
    internal sealed record Response<T>(T Data, Grant? AssetSession);

    internal static async Task<Response<T>> AttachAsync<T>(QuasarOperationContext caller, T data,
        string agentId, string entityId, IEnumerable<AssetSessionModDto> mods, CancellationToken token)
    {
        var settings = await caller.Services.GetRequiredService<IEntityViewerStreamingSettingsStore>().GetAsync(token);
        if (!settings.StreamingEnabled || !settings.HasCurrentConsent) return new(data, null);
        await caller.EnsureAuthorizedAsync(token);
        var session = caller.Services.GetRequiredService<ViewerAssetSessionStore>().CreateSession(UserId(caller.User), agentId, entityId, mods);
        return new(data, new(session.Id, session.ExpiresAtUtc));
    }

    internal static string UserId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ??
        user.FindFirstValue("sub") ??
        user.Identity?.Name ??
        "unknown";

    internal static IReadOnlyList<AssetSessionModDto> SceneMods(JsonElement scene)
    {
        if (scene.ValueKind != JsonValueKind.Object ||
            (!scene.TryGetProperty("mods", out var mods) && !scene.TryGetProperty("Mods", out mods)) ||
            mods.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        try
        {
            return mods.Deserialize<List<AssetSessionModDto>>(Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<AssetSessionModDto> ReplayMods(IEnumerable<ReplayBatch> batches)
    {
        var names = batches
            .SelectMany(batch => batch.Records.Select(record => record.Mod)
                .Concat(batch.Geometry.Values.SelectMany(page => page.Select(block => block.Mod)))
                .Concat(batch.Voxels.Values.SelectMany(page => page.Palette.Select(material => material.Mod))))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(AssetStreamingLimits.MaxModsPerSession);

        return names.Select(name =>
        {
            var publishedName = name.EndsWith(".sbm", StringComparison.OrdinalIgnoreCase)
                ? name[..^".sbm".Length]
                : name;
            return new AssetSessionModDto
            {
                RootId = "mods-direct:" + name,
                Name = name,
                PublishedFileId = ulong.TryParse(publishedName, out var publishedId) ? publishedId : 0,
            };
        }).ToList();
    }
}
