namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class AssetSessionModDto
{
    public string RootId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public ulong PublishedFileId { get; init; }

    public string PublishedServiceName { get; init; } = string.Empty;

    public string FriendlyName { get; init; } = string.Empty;
}

public sealed class AssetResolveRequest
{
    public string LogicalPath { get; init; } = string.Empty;

    public string RootId { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;
}

public sealed class AssetResolveResponse
{
    public bool Found { get; init; }

    public string AssetToken { get; init; } = string.Empty;

    public string CacheKey { get; init; } = string.Empty;

    public string ContentHash { get; init; } = string.Empty;

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public string LogicalPath { get; init; } = string.Empty;

    public string RootId { get; init; } = string.Empty;

    public string RootKind { get; init; } = string.Empty;

    public long Size { get; init; }

    public DateTimeOffset? LastModifiedUtc { get; init; }

    public string ContentType { get; init; } = "application/octet-stream";

    public string Message { get; init; } = string.Empty;
}
