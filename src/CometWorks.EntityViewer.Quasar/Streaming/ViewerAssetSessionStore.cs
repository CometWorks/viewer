using System.Security.Cryptography;

namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class ViewerAssetSessionStore
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);
    private readonly object _sync = new();
    private readonly Dictionary<string, ViewerAssetSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ViewerAssetToken> _tokens = new(StringComparer.Ordinal);

    public ViewerAssetSession CreateSession(string userId, AssetSessionRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new ViewerAssetSession
        {
            Id = CreateTokenId(),
            UserId = userId,
            AgentId = request.AgentId,
            EntityId = request.EntityId,
            Mods = request.Mods.ToList(),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(SessionLifetime),
        };

        lock (_sync)
        {
            CleanupExpiredLocked(now);
            _sessions[session.Id] = session;
        }

        return session;
    }

    public ViewerAssetSession? TryGetSession(string sessionId, string userId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            CleanupExpiredLocked(now);
            if (!_sessions.TryGetValue(sessionId, out var session))
                return null;

            if (!string.Equals(session.UserId, userId, StringComparison.Ordinal))
                return null;

            return session.ExpiresAtUtc > now ? session : null;
        }
    }

    public ViewerAssetToken CreateAssetToken(string userId, string sessionId, ResolvedServerAsset asset)
    {
        var now = DateTimeOffset.UtcNow;
        var token = new ViewerAssetToken
        {
            Id = CreateTokenId(),
            UserId = userId,
            SessionId = sessionId,
            Asset = asset,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(TokenLifetime),
        };

        lock (_sync)
        {
            CleanupExpiredLocked(now);
            _tokens[token.Id] = token;
        }

        return token;
    }

    public ViewerAssetToken? TryGetAssetToken(string tokenId, string userId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            CleanupExpiredLocked(now);
            if (!_tokens.TryGetValue(tokenId, out var token))
                return null;

            if (!string.Equals(token.UserId, userId, StringComparison.Ordinal))
                return null;

            return token.ExpiresAtUtc > now ? token : null;
        }
    }

    private void CleanupExpiredLocked(DateTimeOffset now)
    {
        foreach (var expired in _sessions.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToList())
            _sessions.Remove(expired);

        foreach (var expired in _tokens.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToList())
            _tokens.Remove(expired);
    }

    private static string CreateTokenId()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
    }
}

public sealed class ViewerAssetSession
{
    public string Id { get; init; } = string.Empty;

    public string UserId { get; init; } = string.Empty;

    public string AgentId { get; init; } = string.Empty;

    public string EntityId { get; init; } = string.Empty;

    public IReadOnlyList<AssetSessionModDto> Mods { get; init; } = [];

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset ExpiresAtUtc { get; init; }
}

public sealed class ViewerAssetToken
{
    public string Id { get; init; } = string.Empty;

    public string UserId { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public required ResolvedServerAsset Asset { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset ExpiresAtUtc { get; init; }
}
