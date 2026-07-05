namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class EntityViewerStreamingSettings
{
    public const string CurrentConsentVersion = "server-asset-streaming-v1";

    public bool StreamingEnabled { get; set; }

    public bool ConsentAccepted { get; set; }

    public string ConsentVersion { get; set; } = string.Empty;

    public string ConsentAcceptedByUserId { get; set; } = string.Empty;

    public DateTimeOffset? ConsentAcceptedAtUtc { get; set; }

    public string BaseGameSourceMode { get; set; } = "ManagedSteamCmd";

    public string BaseGameContentPath { get; set; } = string.Empty;

    public string DedicatedServerModsPath { get; set; } = string.Empty;

    public string SteamCmdLoginName { get; set; } = string.Empty;

    public string LastInstallStatus { get; set; } = "NotStarted";

    public DateTimeOffset? LastInstallStartedAtUtc { get; set; }

    public DateTimeOffset? LastInstallCompletedAtUtc { get; set; }

    public int? LastInstallExitCode { get; set; }

    public string LastInstallMessage { get; set; } = string.Empty;

    public bool HasCurrentConsent =>
        ConsentAccepted &&
        string.Equals(ConsentVersion, CurrentConsentVersion, StringComparison.Ordinal);
}
