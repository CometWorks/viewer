using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class SteamCmdHourlyUpdateService(
    IEntityViewerStreamingSettingsStore settingsStore,
    SteamCmdInstallerService installerService,
    ILogger<SteamCmdHourlyUpdateService> logger)
    : BackgroundService
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(UpdateInterval, stoppingToken).ConfigureAwait(false);
                await TryRunUpdateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Entity Viewer SteamCMD hourly update check failed.");
            }
        }
    }

    private async Task TryRunUpdateAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.StreamingEnabled || !settings.HasCurrentConsent)
            return;

        if (string.IsNullOrWhiteSpace(settings.SteamCmdLoginName))
            return;

        if (installerService.GetStatus().IsRunning)
            return;

        await installerService.StartAsync(
            new SteamCmdInstallRequest
            {
                LoginName = settings.SteamCmdLoginName,
                Validate = true,
            },
            automatic: true,
            cancellationToken).ConfigureAwait(false);
    }
}
