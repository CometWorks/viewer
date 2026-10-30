namespace CometWorks.EntityViewer.Quasar.Streaming;

public static class AssetStreamingLimits
{
    public const long MaxAssetBytes = 64L * 1024 * 1024;
    public const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    public const long MaxSessionResponseBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxArchiveEntries = 32768;
    public const int MaxModsPerSession = 1024;
    public const int MaxSessions = 1024;
    public const int MaxSessionsPerUser = 8;
    public const int MaxTokens = 32768;
    public const int MaxTokensPerUser = 4096;
    public const int MaxTokensPerSession = 2048;
    public const int MaxConcurrentResolves = 8;
    public const int MaxConcurrentResolvesPerUser = 4;
    public const int MaxConcurrentResolvesPerSession = 2;
    public const int MaxConcurrentStreamsPerUser = 8;
    public const int MaxConcurrentStreamsPerSession = 4;
    public const int MaxResolveRequestsPerMinute = 600;
    public const int MaxResolveRequestsPerUserPerMinute = 1200;
}
