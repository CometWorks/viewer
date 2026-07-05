namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class SteamCmdInstallRequest
{
    public string LoginName { get; init; } = string.Empty;

    public bool Validate { get; init; } = true;
}

public sealed class SteamCmdInputRequest
{
    public string Input { get; init; } = string.Empty;
}

public sealed class SteamCmdInstallerStatusResponse
{
    public string State { get; init; } = "Idle";

    public bool IsRunning { get; init; }

    public string Message { get; init; } = string.Empty;

    public string SteamCmdPath { get; init; } = string.Empty;

    public string InstallDirectory { get; init; } = string.Empty;

    public string ContentDirectory { get; init; } = string.Empty;

    public bool ContentReady { get; init; }

    public string ContentDiagnostics { get; init; } = string.Empty;

    public string LoginName { get; init; } = string.Empty;

    public bool Validate { get; init; } = true;

    public int? ExitCode { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public int LastSequence { get; init; }

    public IReadOnlyList<SteamCmdInstallerLogEntry> Log { get; init; } = [];
}

public sealed class SteamCmdInstallerLogEntry
{
    public int Sequence { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    public string Stream { get; init; } = "info";

    public string Message { get; init; } = string.Empty;
}
