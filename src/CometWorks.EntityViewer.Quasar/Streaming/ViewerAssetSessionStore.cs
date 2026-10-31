using System.Security.Cryptography;

namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class ViewerAssetSessionStore
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);
    private readonly object _sync = new();
    private readonly Dictionary<string, ViewerAssetSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ViewerAssetToken> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ViewerAssetUserLimits> _userLimits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sessionCountsByUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _tokenCountsByUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _tokenCountsBySession = new(StringComparer.Ordinal);
    private readonly Queue<string> _sessionOrder = new();
    private readonly Queue<string> _tokenOrder = new();
    private readonly Dictionary<string, Queue<string>> _sessionOrderByUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> _tokenOrderByUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> _tokenOrderBySession = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _resolveConcurrency = new(AssetStreamingLimits.MaxConcurrentResolves);
    private DateTimeOffset _nextCleanupUtc;

    public ViewerAssetSession CreateSession(
        string userId,
        string agentId,
        string entityId,
        IEnumerable<AssetSessionModDto> mods)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            CleanupExpiredLocked(now);
            EnsureSessionCapacityLocked(userId);
            var session = new ViewerAssetSession
            {
                Id = CreateTokenId(),
                UserId = userId,
                AgentId = Limit(agentId),
                EntityId = Limit(entityId),
                Mods = NormalizeMods(mods),
                CreatedAtUtc = now,
                ExpiresAtUtc = now.Add(SessionLifetime),
                UserLimits = GetUserLimitsLocked(userId),
            };
            _sessions[session.Id] = session;
            _sessionOrder.Enqueue(session.Id);
            GetQueue(_sessionOrderByUser, userId).Enqueue(session.Id);
            Increment(_sessionCountsByUser, userId);
            return session;
        }
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

    public ViewerAssetToken? CreateAssetToken(string userId, string sessionId, ResolvedServerAsset asset)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            CleanupExpiredLocked(now);
            if (!_sessions.TryGetValue(sessionId, out var session) || session.ExpiresAtUtc <= now ||
                !string.Equals(session.UserId, userId, StringComparison.Ordinal))
            {
                return null;
            }

            EnsureTokenCapacityLocked(userId, sessionId);
            var token = new ViewerAssetToken
            {
                Id = CreateTokenId(),
                UserId = userId,
                SessionId = sessionId,
                Session = session,
                Asset = asset,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.Add(TokenLifetime),
            };
            _tokens[token.Id] = token;
            _tokenOrder.Enqueue(token.Id);
            GetQueue(_tokenOrderByUser, userId).Enqueue(token.Id);
            GetQueue(_tokenOrderBySession, sessionId).Enqueue(token.Id);
            Increment(_tokenCountsByUser, userId);
            Increment(_tokenCountsBySession, sessionId);
            return token;
        }
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

            return token.ExpiresAtUtc > now &&
                   _sessions.TryGetValue(token.SessionId, out var session) &&
                   session.ExpiresAtUtc > now &&
                   string.Equals(session.UserId, userId, StringComparison.Ordinal)
                ? token : null;
        }
    }

    public bool TryBeginResolve(ViewerAssetSession session, out IDisposable? lease)
    {
        lease = null;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(session.Id, out var current) || !ReferenceEquals(current, session))
                return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (session.UserLimits is null || !session.TryConsumeResolve(now) ||
            !session.UserLimits.TryConsumeResolve(now) || !_resolveConcurrency.Wait(0))
        {
            return false;
        }

        if (!session.UserLimits.ResolveConcurrency.Wait(0))
        {
            _resolveConcurrency.Release();
            return false;
        }

        if (!session.ResolveConcurrency.Wait(0))
        {
            session.UserLimits.ResolveConcurrency.Release();
            _resolveConcurrency.Release();
            return false;
        }

        lease = new GateLease(session.ResolveConcurrency, session.UserLimits.ResolveConcurrency, _resolveConcurrency);
        return true;
    }

    public bool TryBeginStream(ViewerAssetToken token, out IDisposable? lease)
    {
        lease = null;
        lock (_sync)
        {
            if (!_tokens.TryGetValue(token.Id, out var current) || !ReferenceEquals(current, token))
                return false;
        }

        var session = token.Session;
        if (session is null || session.UserLimits is null || !session.UserLimits.StreamConcurrency.Wait(0))
            return false;

        if (!session.StreamConcurrency.Wait(0))
        {
            session.UserLimits.StreamConcurrency.Release();
            return false;
        }

        if (!session.TryReserveResponseBytes(token.Asset.Size))
        {
            session.StreamConcurrency.Release();
            session.UserLimits.StreamConcurrency.Release();
            return false;
        }

        lease = new GateLease(session.StreamConcurrency, session.UserLimits.StreamConcurrency);
        return true;
    }

    private void CleanupExpiredLocked(DateTimeOffset now)
    {
        if (now < _nextCleanupUtc)
            return;
        _nextCleanupUtc = now.AddMinutes(1);

        foreach (var expired in _sessions.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToList())
            RemoveSessionLocked(expired);

        foreach (var expired in _tokens.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToList())
            RemoveTokenLocked(expired);

        foreach (var userId in _userLimits.Keys.Where(userId => Count(_sessionCountsByUser, userId) == 0).ToList())
            _userLimits.Remove(userId);

        RebuildOrderQueuesLocked();
    }

    private void EnsureSessionCapacityLocked(string userId)
    {
        while (_sessions.Count >= AssetStreamingLimits.MaxSessions)
            RemoveNextSessionLocked(_sessionOrder);
        var userOrder = GetQueue(_sessionOrderByUser, userId);
        while (Count(_sessionCountsByUser, userId) >= AssetStreamingLimits.MaxSessionsPerUser)
            RemoveNextSessionLocked(userOrder);
    }

    private void EnsureTokenCapacityLocked(string userId, string sessionId)
    {
        while (_tokens.Count >= AssetStreamingLimits.MaxTokens)
            RemoveNextTokenLocked(_tokenOrder);
        var userOrder = GetQueue(_tokenOrderByUser, userId);
        while (Count(_tokenCountsByUser, userId) >= AssetStreamingLimits.MaxTokensPerUser)
            RemoveNextTokenLocked(userOrder);
        var sessionOrder = GetQueue(_tokenOrderBySession, sessionId);
        while (Count(_tokenCountsBySession, sessionId) >= AssetStreamingLimits.MaxTokensPerSession)
            RemoveNextTokenLocked(sessionOrder);
    }

    private void RemoveNextSessionLocked(Queue<string> order)
    {
        while (order.TryDequeue(out var id))
        {
            if (_sessions.ContainsKey(id))
            {
                RemoveSessionLocked(id);
                return;
            }
        }
    }

    private void RemoveNextTokenLocked(Queue<string> order)
    {
        while (order.TryDequeue(out var id))
        {
            if (_tokens.ContainsKey(id))
            {
                RemoveTokenLocked(id);
                return;
            }
        }
    }

    private void RemoveSessionLocked(string sessionId)
    {
        if (!_sessions.Remove(sessionId, out var session))
            return;
        Decrement(_sessionCountsByUser, session.UserId);
        if (_tokenOrderBySession.Remove(sessionId, out var tokens))
        {
            while (tokens.TryDequeue(out var tokenId))
                RemoveTokenLocked(tokenId);
        }
        _tokenCountsBySession.Remove(sessionId);
    }

    private void RemoveTokenLocked(string tokenId)
    {
        if (!_tokens.Remove(tokenId, out var token))
            return;
        Decrement(_tokenCountsByUser, token.UserId);
        Decrement(_tokenCountsBySession, token.SessionId);
    }

    private ViewerAssetUserLimits GetUserLimitsLocked(string userId)
    {
        if (_userLimits.TryGetValue(userId, out var limits))
            return limits;
        limits = new ViewerAssetUserLimits();
        _userLimits[userId] = limits;
        return limits;
    }

    private void RebuildOrderQueuesLocked()
    {
        _sessionOrder.Clear();
        _tokenOrder.Clear();
        _sessionOrderByUser.Clear();
        _tokenOrderByUser.Clear();
        _tokenOrderBySession.Clear();
        foreach (var session in _sessions.Values.OrderBy(session => session.CreatedAtUtc))
        {
            _sessionOrder.Enqueue(session.Id);
            GetQueue(_sessionOrderByUser, session.UserId).Enqueue(session.Id);
        }
        foreach (var token in _tokens.Values.OrderBy(token => token.CreatedAtUtc))
        {
            _tokenOrder.Enqueue(token.Id);
            GetQueue(_tokenOrderByUser, token.UserId).Enqueue(token.Id);
            GetQueue(_tokenOrderBySession, token.SessionId).Enqueue(token.Id);
        }
    }

    private static IReadOnlyList<AssetSessionModDto> NormalizeMods(IEnumerable<AssetSessionModDto>? mods)
    {
        var normalized = new List<AssetSessionModDto>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mod in mods ?? [])
        {
            if (normalized.Count >= AssetStreamingLimits.MaxModsPerSession)
                break;
            var rootId = Limit(mod.RootId);
            if (string.IsNullOrWhiteSpace(rootId) || !ids.Add(rootId))
                continue;
            normalized.Add(new AssetSessionModDto
            {
                RootId = rootId,
                Name = Limit(mod.Name),
                PublishedFileId = mod.PublishedFileId,
                PublishedServiceName = Limit(mod.PublishedServiceName),
                FriendlyName = Limit(mod.FriendlyName),
            });
        }
        return normalized;
    }

    private static string Limit(string? value)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length <= 256 ? value : string.Empty;
    }

    private static Queue<string> GetQueue(Dictionary<string, Queue<string>> queues, string key)
    {
        if (queues.TryGetValue(key, out var queue))
            return queue;
        queue = new Queue<string>();
        queues[key] = queue;
        return queue;
    }

    private static int Count(Dictionary<string, int> counts, string key) => counts.GetValueOrDefault(key);

    private static void Increment(Dictionary<string, int> counts, string key) => counts[key] = Count(counts, key) + 1;

    private static void Decrement(Dictionary<string, int> counts, string key)
    {
        var count = Count(counts, key) - 1;
        if (count <= 0)
            counts.Remove(key);
        else
            counts[key] = count;
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

    private sealed class GateLease(params SemaphoreSlim[] gates) : IDisposable
    {
        private SemaphoreSlim[]? _gates = gates;

        public void Dispose()
        {
            var gatesToRelease = Interlocked.Exchange(ref _gates, null);
            if (gatesToRelease is null)
                return;
            foreach (var gate in gatesToRelease)
                gate.Release();
        }
    }
}

public sealed class ViewerAssetSession
{
    private readonly object _limitSync = new();
    private DateTimeOffset _resolveWindowStartedUtc;
    private int _resolveCount;
    private long _responseBytes;

    public string Id { get; init; } = string.Empty;

    public string UserId { get; init; } = string.Empty;

    public string AgentId { get; init; } = string.Empty;

    public string EntityId { get; init; } = string.Empty;

    public IReadOnlyList<AssetSessionModDto> Mods { get; init; } = [];

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset ExpiresAtUtc { get; init; }

    internal ViewerAssetUserLimits? UserLimits { get; init; }

    internal SemaphoreSlim ResolveConcurrency { get; } = new(AssetStreamingLimits.MaxConcurrentResolvesPerSession);

    internal SemaphoreSlim StreamConcurrency { get; } = new(AssetStreamingLimits.MaxConcurrentStreamsPerSession);

    internal bool TryConsumeResolve(DateTimeOffset now)
    {
        lock (_limitSync)
        {
            if (_resolveWindowStartedUtc == default || now - _resolveWindowStartedUtc >= TimeSpan.FromMinutes(1))
            {
                _resolveWindowStartedUtc = now;
                _resolveCount = 0;
            }
            if (_resolveCount >= AssetStreamingLimits.MaxResolveRequestsPerMinute)
                return false;
            _resolveCount++;
            return true;
        }
    }

    internal bool TryReserveResponseBytes(long size)
    {
        if (size < 0 || size > AssetStreamingLimits.MaxAssetBytes)
            return false;
        lock (_limitSync)
        {
            if (_responseBytes > AssetStreamingLimits.MaxSessionResponseBytes - size)
                return false;
            _responseBytes += size;
            return true;
        }
    }
}

internal sealed class ViewerAssetUserLimits
{
    private readonly object _sync = new();
    private DateTimeOffset _resolveWindowStartedUtc;
    private int _resolveCount;

    public SemaphoreSlim ResolveConcurrency { get; } = new(AssetStreamingLimits.MaxConcurrentResolvesPerUser);

    public SemaphoreSlim StreamConcurrency { get; } = new(AssetStreamingLimits.MaxConcurrentStreamsPerUser);

    public bool TryConsumeResolve(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_resolveWindowStartedUtc == default || now - _resolveWindowStartedUtc >= TimeSpan.FromMinutes(1))
            {
                _resolveWindowStartedUtc = now;
                _resolveCount = 0;
            }
            if (_resolveCount >= AssetStreamingLimits.MaxResolveRequestsPerUserPerMinute)
                return false;
            _resolveCount++;
            return true;
        }
    }
}

public sealed class ViewerAssetToken
{
    public string Id { get; init; } = string.Empty;

    public string UserId { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    internal ViewerAssetSession? Session { get; init; }

    public required ResolvedServerAsset Asset { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset ExpiresAtUtc { get; init; }
}
