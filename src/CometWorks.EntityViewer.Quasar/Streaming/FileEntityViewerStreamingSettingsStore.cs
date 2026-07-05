using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class FileEntityViewerStreamingSettingsStore(
    EntityViewerStreamingPaths paths,
    ILogger<FileEntityViewerStreamingSettingsStore> logger)
    : IEntityViewerStreamingSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<EntityViewerStreamingSettings> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EntityViewerStreamingSettings> UpdateAsync(
        Func<EntityViewerStreamingSettings, EntityViewerStreamingSettings> update,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var next = update(current) ?? new EntityViewerStreamingSettings();
            await WriteAsync(next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<EntityViewerStreamingSettings> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(paths.SettingsPath))
                return new EntityViewerStreamingSettings();

            await using var stream = File.OpenRead(paths.SettingsPath);
            return await JsonSerializer.DeserializeAsync<EntityViewerStreamingSettings>(
                       stream,
                       JsonOptions,
                       cancellationToken).ConfigureAwait(false)
                   ?? new EntityViewerStreamingSettings();
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Could not parse Entity Viewer streaming settings at {SettingsPath}.", paths.SettingsPath);
            return new EntityViewerStreamingSettings();
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not read Entity Viewer streaming settings at {SettingsPath}.", paths.SettingsPath);
            return new EntityViewerStreamingSettings();
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Could not access Entity Viewer streaming settings at {SettingsPath}.", paths.SettingsPath);
            return new EntityViewerStreamingSettings();
        }
    }

    private async Task WriteAsync(EntityViewerStreamingSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.PluginToolsDirectory);
        var tempPath = $"{paths.SettingsPath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, paths.SettingsPath, overwrite: true);
    }
}
