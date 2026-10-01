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
    private Snapshot? _snapshot;

    public async Task<EntityViewerStreamingSettings> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null && snapshot.Stamp is not null && snapshot.Stamp == SettingsStamp())
            return snapshot.Settings.Copy();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false)).Copy();
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
            var current = (await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false)).Copy();
            var next = update(current) ?? new EntityViewerStreamingSettings();
            await WriteAsync(next, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, new Snapshot(SettingsStamp(), next.Copy()));
            return next.Copy();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<EntityViewerStreamingSettings> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var stamp = SettingsStamp();
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null && stamp is not null && snapshot.Stamp == stamp)
            return snapshot.Settings;

        var settings = await ReadAsync(cancellationToken).ConfigureAwait(false);
        // Use the pre-read stamp so a concurrent external replacement triggers another read.
        Volatile.Write(ref _snapshot, new Snapshot(stamp, settings));
        return settings;
    }

    private (DateTime Modified, long Size)? SettingsStamp()
    {
        try
        {
            var info = new FileInfo(paths.SettingsPath);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fail closed through ReadAsync; never reuse enabled settings when metadata cannot be read.
            return null;
        }
    }

    private sealed record Snapshot((DateTime Modified, long Size)? Stamp, EntityViewerStreamingSettings Settings);

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
