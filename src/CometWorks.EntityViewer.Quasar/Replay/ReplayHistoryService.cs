using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CometWorks.EntityViewer.Replay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Companion;

namespace CometWorks.EntityViewer.Quasar.Replay;

public sealed class ReplayHistoryService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly IQuasarCompanionChannel _channel;
    private readonly ILogger<ReplayHistoryService> _logger;
    private readonly ConcurrentDictionary<string, ServerHistory> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _queries = new(2);

    public ReplayHistoryService(QuasarPluginContext context, IQuasarCompanionChannel channel, ILogger<ReplayHistoryService> logger)
    {
        _root = Path.Combine(context.InstallDirectory, "Data", "EntityViewerReplay");
        _channel = channel; _logger = logger;
    }

    public object[] Servers() => _servers.Values.OrderBy(s => s.Info.DisplayName).Select(s => (object)new
    {
        s.Info.ServerId, s.Info.AgentId, s.Info.DisplayName, s.Error, s.LostChunks, s.LastCollectedAt, s.RecordingEnabled,
        retentionHours = 24
    }).ToArray();

    public string Resolve(string id) => _servers.Values.FirstOrDefault(s => s.Info.ServerId.Equals(id, StringComparison.OrdinalIgnoreCase)
        || s.Info.AgentId.Equals(id, StringComparison.OrdinalIgnoreCase))?.Info.ServerId ?? id;

    public object Overview(string serverId)
    {
        var server = Find(serverId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new { server.Info, server.Error, server.LostChunks, server.LastCollectedAt, server.RecordingEnabled, segments = server.Store.List(now).Select(id => new { at = ReplayChunkStore.Timestamp(id), events = int.Parse(id.Split('-')[1]) }) };
    }

    public async Task<object> SetRecording(string serverId, ReplayRecordingSettings settings, CancellationToken token)
    {
        var server = Find(serverId);
        await server.Gate.WaitAsync(token);
        try
        {
            // Persist before sending: a failed stop must never resume collection on restart.
            var path = Path.Combine(server.Directory, "recording.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(settings, Json), token);
            File.Move(path + ".tmp", path, true);
            server.RecordingEnabled = settings.Enabled;
            server.Draining = !settings.Enabled;
            server.RetryAt = default;
            await CollectLocked(server, token);
            return new { server.RecordingEnabled, server.Error };
        }
        finally { server.Gate.Release(); }
    }

    public async Task<List<ReplayBatch>> Query(string serverId, long from, long to, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (from < now - ReplayChunkStore.RetentionMs || to <= from || to - from > 15 * 60 * 1000 || to > now + 60000)
            throw new ArgumentException("Select up to 15 minutes within the last 24 hours.");
        var server = Find(serverId);
        await _queries.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                var batches = new List<ReplayBatch>();
                var count = 0;
                var expandedBytes = 0;
                var seenGeometry = new HashSet<string>();
                var geometryCache = new Dictionary<string, ReplayBlock[]>();
                var voxelCache = new Dictionary<string, ReplayVoxelPage>();
                var partsCache = new Dictionary<string, ReplayModelParts>();
                foreach (var chunk in server.Store.ReadRange(from, to, now))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = Decode(chunk, out var expandedSize, geometryCache, voxelCache, partsCache);
                    expandedBytes += expandedSize;
                    foreach (var id in batch.Geometry.Keys.ToArray())
                        if (!seenGeometry.Add(id)) batch.Geometry.Remove(id);
                    foreach (var id in batch.Voxels.Keys.ToArray())
                        if (!seenGeometry.Add(id)) batch.Voxels.Remove(id);
                    foreach (var id in batch.ModelParts.Keys.ToArray())
                        if (!seenGeometry.Add(id)) batch.ModelParts.Remove(id);
                    if (expandedBytes > 32 * 1024 * 1024) throw new InvalidDataException("Too much replay data. Select a shorter time range.");
                    batch.Records.RemoveAll(r => r.At < Math.Max(now - ReplayChunkStore.RetentionMs, from - 35000) || r.At > to);
                    count += batch.Records.Count;
                    if (count > 40000) throw new InvalidDataException("Too much replay data. Select a shorter time range.");
                    batches.Add(batch);
                }
                return batches;
            }, cancellationToken);
        }
        finally { _queries.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_root);
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            try
            {
                var info = JsonSerializer.Deserialize<QuasarCompanionServer>(await File.ReadAllTextAsync(Path.Combine(directory, "server.json"), stoppingToken), Json);
                if (info != null) _servers[info.ServerId] = Open(info);
            }
            catch (Exception e) when (e is IOException or JsonException) { _logger.LogWarning(e, "Could not open replay archive {Directory}", directory); }
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            var connected = _channel.GetConnectedServers().DistinctBy(s => s.ServerId, StringComparer.OrdinalIgnoreCase);
            // Bound collection concurrency: one stalled server must not stop other recordings.
            await Parallel.ForEachAsync(connected, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken }, Collect);
            foreach (var server in _servers.Values)
            {
                try { server.Store.Prune(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
                catch (IOException e) { server.Error = e.Message; }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async ValueTask Collect(QuasarCompanionServer info, CancellationToken token)
    {
        try
        {
            var server = _servers.GetOrAdd(info.ServerId, _ => Open(info));
            await server.Gate.WaitAsync(token);
            try
            {
                server.Info = info;
                if ((!server.RecordingEnabled && !server.Draining) || server.RetryAt > DateTimeOffset.UtcNow) return;
                await CollectLocked(server, token);
            }
            finally { server.Gate.Release(); }
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            _logger.LogWarning(e, "Could not open replay archive for {Server}", info.ServerId);
        }
    }

    private async Task CollectLocked(ServerHistory server, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var page = await _channel.SendAsync<ReplayReadRequest, ReplayPage>(server.Info.AgentId, "cometworks.entityviewer", "read-replay", new ReplayReadRequest { Record = server.RecordingEnabled, Acknowledge = server.Acknowledge }, timeout.Token);
            server.Acknowledge = [];
            if (page.Chunks.Count > 8 || page.Chunks.Sum(c => (long)c.Data.Length + c.Geometry.Sum(p => (long)p.Value.Length)) > ReplayChunkStore.MaxTransferBytes)
                throw new InvalidDataException("Replay response exceeds collection budget.");
            foreach (var chunk in page.Chunks)
            {
                var decoded = Decode(chunk);
                if (decoded.Records.Any(r => r.LayoutId.Length > 0 && r.Layout == null) || decoded.Records.Where(r => r.Layout != null).SelectMany(r => r.Layout!).Any(id => !decoded.Geometry.ContainsKey(id)))
                    throw new InvalidDataException("Missing replay geometry dependency.");
                server.Store.Put(chunk, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                server.Acknowledge.Add(chunk.Id);
            }
            server.LostChunks = page.LostChunks + server.Store.LostChunks;
            server.Error = page.Error;
            server.LastCollectedAt = DateTimeOffset.UtcNow;
            server.Draining = !server.RecordingEnabled && (page.Draining || server.Acknowledge.Count > 0);
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            server.Error = e.Message; server.RetryAt = DateTimeOffset.UtcNow.AddSeconds(10);
            _logger.LogDebug(e, "Replay collection unavailable for {Server}", server.Info.ServerId);
        }
    }

    private ServerHistory Open(QuasarCompanionServer info)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(info.ServerId.ToLowerInvariant())));
        var directory = Path.Combine(_root, hash);
        var store = new ReplayChunkStore(directory, 256L * 1024 * 1024);
        File.WriteAllText(Path.Combine(directory, "server.json"), JsonSerializer.Serialize(info, Json));
        var enabled = false;
        var settingsPath = Path.Combine(directory, "recording.json");
        try { if (File.Exists(settingsPath)) enabled = JsonSerializer.Deserialize<ReplayRecordingSettings>(File.ReadAllText(settingsPath), Json)?.Enabled == true; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { _logger.LogWarning(e, "Recording settings unreadable; recording disabled for {Server}", info.ServerId); }
        return new ServerHistory(info, store, directory) { RecordingEnabled = enabled };
    }

    private ServerHistory Find(string id) => _servers.TryGetValue(Resolve(id), out var server) ? server : throw new KeyNotFoundException("No replay history for this server.");

    internal static ReplayBatch Decode(ReplayChunk chunk) => Decode(chunk, out _);

    private static ReplayBatch Decode(ReplayChunk chunk, out int expandedSize, Dictionary<string, ReplayBlock[]>? geometryCache = null, Dictionary<string, ReplayVoxelPage>? voxelCache = null, Dictionary<string, ReplayModelParts>? partsCache = null)
    {
        if (!ReplayChunkStore.ValidId(chunk.Id)) throw new InvalidDataException("Invalid replay segment id.");
        var expanded = ReplayChunkStore.Expand(chunk.Data);
        expandedSize = expanded.Length;
        var batch = JsonSerializer.Deserialize<ReplayBatch>(expanded, Json) ?? throw new InvalidDataException("Empty replay segment.");
        if (batch.Version != 1 || !Guid.TryParseExact(batch.Session, "N", out _) || batch.Records == null || batch.Records.Count > 1024 || batch.Geometry == null || batch.Voxels == null || batch.ModelParts == null)
            throw new InvalidDataException("Unsupported replay segment.");
        if (chunk.Geometry == null || chunk.Geometry.Count > 32768 || chunk.Geometry.Sum(p => (long)p.Value.Length) + chunk.Data.Length > ReplayChunkStore.MaxTransferBytes)
            throw new InvalidDataException("Replay geometry exceeds collection budget.");
        // The content hash and compressed/expanded limits are checked before any block reaches the browser.
        batch.Geometry.Clear(); batch.Voxels.Clear(); batch.ModelParts.Clear();
        var layouts = new Dictionary<string, ReplayLayoutPage>();
        foreach (var part in chunk.Geometry)
        {
            if (geometryCache != null && geometryCache.TryGetValue(part.Key, out var cached)) { batch.Geometry[part.Key] = cached; continue; }
            if (voxelCache != null && voxelCache.TryGetValue(part.Key, out var voxelCached)) { batch.Voxels[part.Key] = voxelCached; continue; }
            if (partsCache != null && partsCache.TryGetValue(part.Key, out var partsCached)) { batch.ModelParts[part.Key] = partsCached; continue; }
            if (!ReplayChunkStore.ValidGeometryId(part.Key) || part.Value.Length > ReplayChunkStore.MaxGeometryBytes || ReplayChunkStore.GeometryId(part.Value) != part.Key)
                throw new InvalidDataException("Invalid replay geometry hash.");
            var data = ReplayChunkStore.Expand(part.Value);
            if (data.Length > ReplayChunkStore.MaxGeometryBytes) throw new InvalidDataException("Replay geometry page too large.");
            expandedSize += data.Length;
            if (expandedSize > 32 * 1024 * 1024) throw new InvalidDataException("Replay geometry expansion budget exceeded.");
            if (data.Length > 0 && data[0] == (byte)'{')
            {
                using var document = JsonDocument.Parse(data);
                if (document.RootElement.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "voxel")
                {
                    var voxel = JsonSerializer.Deserialize<ReplayVoxelPage>(data, Json);
                    if (!ValidVoxel(voxel)) throw new InvalidDataException("Invalid replay terrain page.");
                    batch.Voxels[part.Key] = voxel!;
                    if (voxelCache != null) voxelCache[part.Key] = voxel!;
                    continue;
                }
                if (document.RootElement.TryGetProperty("kind", out kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "models")
                {
                    var models = JsonSerializer.Deserialize<ReplayModelParts>(data, Json);
                    if (models?.Parts == null || models.Parts.Length is < 1 or > 32 || models.Parts.Any(p => p == null || !ValidAssetPath(p.Model) || p.Matrix == null || !ValidVector(p.Matrix, 16)))
                        throw new InvalidDataException("Invalid replay model parts.");
                    batch.ModelParts[part.Key] = models;
                    if (partsCache != null) partsCache[part.Key] = models;
                    continue;
                }
                var layout = JsonSerializer.Deserialize<ReplayLayoutPage>(data, Json);
                if (layout?.Pages == null || layout.Pages.Length > 256 || layout.Pages.Distinct().Count() != layout.Pages.Length || layout.Pages.Any(id => !ReplayChunkStore.ValidGeometryId(id)))
                    throw new InvalidDataException("Invalid layout manifest.");
                layouts[part.Key] = layout;
                continue;
            }
            var blocks = JsonSerializer.Deserialize<ReplayBlock[]>(data, Json) ?? throw new InvalidDataException("Empty geometry page.");
            if (blocks.Length > ReplayLayoutPage.MaxBlocksPerGrid || blocks.Any(b => !ValidBlock(b))) throw new InvalidDataException("Invalid replay block.");
            batch.Geometry[part.Key] = blocks;
            if (geometryCache != null) geometryCache[part.Key] = blocks;
        }
        var start = ReplayChunkStore.Timestamp(chunk.Id);
        foreach (var record in batch.Records)
        {
            if (record != null && !string.IsNullOrEmpty(record.LayoutId))
                record.Layout = layouts.TryGetValue(record.LayoutId, out var layout) ? layout.Pages : null;
            if (record == null || record.At < start || record.At > start + 60000 || record.Entity == null || record.Name == null || record.Detail == null || record.Source == null || record.Kind == null || record.Type == null
                || record.Entity.Length > 128 || record.Name.Length > 256 || record.Detail.Length > 1024 || record.Source.Length > 128 || record.Kind.Length > 32 || record.Type.Length > 32
                || record.Character == null || record.Character.Length > 128 || record.Weapon == null || record.Weapon.Length > 128
                || record.Related == null || record.Related.Length > 128 || record.Actions is < 0 or > 127 || record.Movement is < 0 or > 65535 || !ValidVector(record.View, 2)
                || record.Cell != null && !ValidCell(record.Cell)
                || !double.IsFinite(record.Value) || !double.IsFinite(record.Elapsed) || !ValidVector(record.Position, 3) || !ValidVector(record.Rotation, 4)
                || !ValidVector(record.Sun, 3) || !double.IsFinite(record.SunIntensity) || record.SunIntensity < 0 || record.SunIntensity > 100
                || !double.IsFinite(record.ModelScale) || record.ModelScale <= 0 || record.ModelScale > 100
                || record.Terrain != null || record.TerrainId == null
                || (record.Kind == "voxel-sample" ? !batch.Voxels.ContainsKey(record.TerrainId) || record.Value < 0 || record.Value > record.At : record.TerrainId.Length != 0)
                || !ValidVector(record.Gravity, 3) || !ValidVector(record.Color, 3) || !ValidAssetPath(record.Model)
                || record.Mod == null || record.Mod.Length > 256 || record.Mod.IndexOfAny(['/', '\\', ':']) >= 0 || record.Mod == ".."
                || !ValidVector(record.Origin, 3) || !double.IsFinite(record.GridSize) || record.GridSize < 0 || record.GridSize > 100
                || record.LayoutId == null || record.LayoutId.Length != 0 && !ReplayChunkStore.ValidGeometryId(record.LayoutId)
                || record.Block != null || record.Layout != null && (record.Kind != "sample" || record.Layout.Length > 256 || record.Layout.Any(id => !ReplayChunkStore.ValidGeometryId(id)))
                || !ValidVector(record.Size, 3) || !ValidVector(record.Velocity, 3) || !ValidVector(record.AngularVelocity, 3))
                throw new InvalidDataException("Invalid replay record.");
        }
        if (batch.Geometry.Values.SelectMany(p => p).Any(b => b.PartsId.Length > 0 && (!batch.ModelParts.TryGetValue(b.PartsId, out var parts) || b.Patterns?.Length != parts.Parts.Length * 4)))
            throw new InvalidDataException("Missing replay model parts dependency.");
        return batch;
    }
    private static bool ValidVoxel(ReplayVoxelPage? v) => v != null && v.Kind == "voxel" && v.Body != null && v.Body.Length <= 128
        && v.Origin != null && ValidVector(v.Origin, 3) && v.Rotation != null && ValidVector(v.Rotation, 4)
        && v.Rotation.Sum(x => x * x) is > .99 and < 1.01
        && v.Lod is >= 0 and <= 6 && ValidCell(v.Min) && v.Min.All(x => x >= 0)
        && v.Size != null && v.Size.Length == 3 && v.Size.All(x => x is >= 2 and <= 33)
        && v.Content != null && v.Materials != null && v.Content.Length == v.Size[0] * v.Size[1] * v.Size[2] && v.Materials.Length == v.Content.Length
        && v.Palette != null && v.Palette.Length <= 256 && v.Palette.All(m => m != null && m.Index is >= 0 and <= 255
            && m.Mod != null && m.Mod.Length <= 256 && m.Mod.IndexOfAny(['/', '\\', ':']) < 0 && m.Mod != ".."
            && m.SubtypeId != null && m.SubtypeId.Length <= 256 && m.MaterialTypeName != null && m.MaterialTypeName.Length <= 256
            && float.IsFinite(m.TilingScale) && m.TilingScale >= 0 && m.TilingScale <= 10000
            && new[] { m.ColorMetalXZnY, m.ColorMetalY, m.NormalGlossXZnY, m.NormalGlossY, m.ExtXZnY, m.ExtY }.All(ValidAssetPath));

    private static bool ValidBlock(ReplayBlock b) => b != null && ValidCell(b.Cell) && ValidCell(b.Min) && ValidCell(b.Max)
        && b.Min.Zip(b.Max).All(p => p.First <= p.Second) && b.Definition != null && b.Definition.Length <= 256
        && ValidAssetPath(b.Model) && b.Parts == null && b.PartsId != null && (b.PartsId.Length == 0 || ReplayChunkStore.ValidGeometryId(b.PartsId))
        && (b.Patterns == null || b.Patterns.Length <= 128)
        && b.Mod != null && b.Mod.Length <= 256 && b.Topology != null && b.Topology.Length <= 64
        && b.Translation != null && ValidVector(b.Translation, 3) && b.Rotation != null && ValidVector(b.Rotation, 4)
        && b.Color != null && ValidVector(b.Color, 3) && double.IsFinite(b.Integrity) && b.Integrity >= 0 && b.Integrity <= 1
        && double.IsFinite(b.Build) && b.Build >= 0 && b.Build <= 1;
    private static bool ValidAssetPath(string? path) => path != null && path.Length <= 1024
        && !Path.IsPathRooted(path) && !path.Contains(':') && !path.StartsWith('\\') && !path.Split('/', '\\').Contains("..");
    private static bool ValidCell(int[]? cell) => cell != null && cell.Length == 3 && cell.All(v => v >= -1000000 && v <= 1000000);
    private static bool ValidVector(double[]? vector, int length) => vector == null || vector.Length == length && vector.All(double.IsFinite);

    private sealed class ServerHistory(QuasarCompanionServer info, ReplayChunkStore store, string directory)
    {
        public readonly string Directory = directory;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public bool RecordingEnabled;
        public bool Draining;
        public QuasarCompanionServer Info = info;
        public readonly ReplayChunkStore Store = store;
        public List<string> Acknowledge = [];
        public string Error = "";
        public long LostChunks;
        public DateTimeOffset? LastCollectedAt;
        public DateTimeOffset RetryAt;
    }
}
