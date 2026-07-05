namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class AssetStreamingConsentRequest
{
    public bool ConsentAccepted { get; init; }

    public bool StreamingEnabled { get; init; }
}

public sealed class AssetStreamingStatusResponse
{
    public string Mode { get; init; } = "local";

    public bool StreamingEnabled { get; init; }

    public bool ConsentAccepted { get; init; }

    public bool ConsentRequired { get; init; }

    public string ConsentVersion { get; init; } = EntityViewerStreamingSettings.CurrentConsentVersion;

    public bool CanManageStreaming { get; init; }

    public bool FileStreamingReady { get; init; }

    public string BaseGameSourceMode { get; init; } = "ManagedSteamCmd";

    public bool BaseGameContentConfigured { get; init; }

    public bool ManagedGameContentExists { get; init; }

    public bool ManagedDedicatedServerContentExists { get; init; }

    public string ActiveBaseGameContentDirectory { get; init; } = string.Empty;

    public string ManagedContentSource { get; init; } = "ManagedSteamCmd";

    public string BaseGameContentMessage { get; init; } = string.Empty;

    public string LastInstallStatus { get; init; } = "NotStarted";

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset? ConsentAcceptedAtUtc { get; init; }
}

public sealed class AssetStreamingRootSettingsRequest
{
    public string BaseGameSourceMode { get; init; } = "ManagedSteamCmd";

    public string BaseGameContentPath { get; init; } = string.Empty;

    public string DedicatedServerModsPath { get; init; } = string.Empty;
}

public sealed class AssetStreamingRootSettingsResponse
{
    public string BaseGameSourceMode { get; init; } = "ManagedSteamCmd";

    public string BaseGameContentPath { get; init; } = string.Empty;

    public string DedicatedServerModsPath { get; init; } = string.Empty;

    public string ManagedGameClientDirectory { get; init; } = string.Empty;

    public string ManagedGameContentDirectory { get; init; } = string.Empty;

    public string ManagedDedicatedServerContentDirectory { get; init; } = string.Empty;

    public string ActiveBaseGameContentDirectory { get; init; } = string.Empty;

    public string ManagedContentSource { get; init; } = "ManagedSteamCmd";

    public bool BaseGameContentConfigured { get; init; }

    public bool ManagedGameContentExists { get; init; }

    public bool ManagedDedicatedServerContentExists { get; init; }

    public string BaseGameContentMessage { get; init; } = string.Empty;

    public bool DedicatedServerModsPathExists { get; init; }
}
