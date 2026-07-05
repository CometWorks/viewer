namespace CometWorks.EntityViewer.Quasar.Streaming;

public interface IEntityViewerStreamingSettingsStore
{
    Task<EntityViewerStreamingSettings> GetAsync(CancellationToken cancellationToken);

    Task<EntityViewerStreamingSettings> UpdateAsync(
        Func<EntityViewerStreamingSettings, EntityViewerStreamingSettings> update,
        CancellationToken cancellationToken);
}
