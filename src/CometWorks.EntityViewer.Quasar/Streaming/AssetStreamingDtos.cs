namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class AssetSessionRequest
{
    public string AgentId { get; init; } = string.Empty;

    public string EntityId { get; init; } = string.Empty;

    public IReadOnlyList<AssetSessionModDto> Mods { get; init; } = [];
}

public sealed class AssetSessionModDto
{
    public string RootId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public ulong PublishedFileId { get; init; }

    public string PublishedServiceName { get; init; } = string.Empty;

    public string FriendlyName { get; init; } = string.Empty;
}

public sealed class AssetSessionResponse
{
    public string SessionId { get; init; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; init; }
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

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public string LogicalPath { get; init; } = string.Empty;

    public string RootId { get; init; } = string.Empty;

    public string RootKind { get; init; } = string.Empty;

    public long Size { get; init; }

    public DateTimeOffset? LastModifiedUtc { get; init; }

    public string ContentType { get; init; } = "application/octet-stream";

    public string Message { get; init; } = string.Empty;
}
