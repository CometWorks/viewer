using System.Security.Claims;
using System.Text.Json;
using CometWorks.EntityViewer.Replay;
using Microsoft.AspNetCore.Http;

namespace CometWorks.EntityViewer.Quasar.Streaming;

internal static class ViewerAssetSessionProtocol
{
    internal const string SessionHeader = "X-Entity-Viewer-Asset-Session";
    internal const string ExpiresHeader = "X-Entity-Viewer-Asset-Session-Expires";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void WriteHeaders(HttpResponse response, ViewerAssetSession session)
    {
        response.Headers[SessionHeader] = session.Id;
        response.Headers[ExpiresHeader] = session.ExpiresAtUtc.ToString("O");
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
