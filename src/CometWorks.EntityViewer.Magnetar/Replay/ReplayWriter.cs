#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using CometWorks.EntityViewer.Replay;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace CometWorks.EntityViewer.Magnetar.Replay;

internal sealed class ReplayWriter : IDisposable
{
    internal static readonly JsonSerializerSettings Json = new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Ignore
    };
    private readonly BlockingCollection<Pending> _queue = new BlockingCollection<Pending>(4096);
    private struct Pending { public ReplayRecord Record; public long Version; public Action<ReplayRecord> Prepare; }
    private readonly Task _worker;
    private readonly string _session;
    private readonly string _world;
    private readonly string _path;
    private long _dropped;
    private long _lossVersion;
    public long LossVersion => Interlocked.Read(ref _lossVersion);
    private readonly ConditionalWeakTable<ReplayVoxelPage, Tuple<string, byte[]>> _terrain = new ConditionalWeakTable<ReplayVoxelPage, Tuple<string, byte[]>>();
    private readonly ReplayLayoutBuilder _layouts = new ReplayLayoutBuilder();
    private ReplayChunkStore _store;
    private string _error = "Recorder starting.";
    public Task Completion => _worker;

    public ReplayWriter(string path, string session, string world, ReplayWriter previous = null)
    {
        _path = path; _session = session; _world = world;
        _worker = Task.Run(async () =>
        {
            if (previous != null)
            {
                await previous.Completion.ConfigureAwait(false);
                // World reloads share the archive/index with any in-flight reader, after the old writer drains.
                if (previous._path == _path) _store = previous._store;
                previous = null;
            }
            Run();
        });
    }

    public void Add(ReplayRecord record, Action<ReplayRecord> prepare = null)
    {
        if (_queue.IsAddingCompleted) return;
        try { if (!_queue.TryAdd(new Pending { Record = record, Version = LossVersion, Prepare = prepare })) { Interlocked.Increment(ref _dropped); Interlocked.Increment(ref _lossVersion); } }
        catch (InvalidOperationException) { }
    }

    public ReplayPage Read(ReplayReadRequest request)
    {
        var store = _store;
        if (store == null) return new ReplayPage { Error = _error };
        var page = store.ReadAndAcknowledge(request, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        page.Error = _error;
        return page;
    }

    private void Run()
    {
        try
        {
            _store = _store ?? new ReplayChunkStore(_path, 64L * 1024 * 1024);
            _error = "";
            var layoutVersion = LossVersion;
            var records = new List<ReplayRecord>(128);
            var captured = new List<Pending>(512);
            var geometry = new Dictionary<string, byte[]>();
            while (!_queue.IsCompleted)
            {
                records.Clear(); captured.Clear(); geometry.Clear();
                long minAt = long.MaxValue, maxAt = long.MinValue;
                var geometryBytes = 0;
                var until = DateTime.UtcNow.AddSeconds(5);
                while (captured.Count < 512 && DateTime.UtcNow < until && !_queue.IsCompleted)
                    if (_queue.TryTake(out var item, 100)) captured.Add(item);
                var version = LossVersion;
                if (version != layoutVersion) { _layouts.Clear(); layoutVersion = version; }
                foreach (var item in captured)
                {
                    var record = item.Record;
                    if (item.Version != version && record.Kind.StartsWith("layout-", StringComparison.Ordinal)) continue;
                    try { item.Prepare?.Invoke(record); }
                    catch { /* Authored model metadata unavailable; recorded block topology remains a fallback. */ }
                    if (_layouts.Apply(record)) continue;
                    var parts = _layouts.Attach(record);
                    Tuple<string, byte[]> terrain = null;
                    if (record.Terrain != null)
                    {
                        terrain = _terrain.GetValue(record.Terrain, page => {
                            var data = ReplayChunkStore.Compress(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(page, Json)));
                            return Tuple.Create(ReplayChunkStore.GeometryId(data), data);
                        });
                        record.TerrainId = terrain.Item1;
                        record.Terrain = null;
                    }
                    var needsParts = record.LayoutId.Length == 0 || !geometry.ContainsKey(record.LayoutId);
                    var extra = 0;
                    if (needsParts) foreach (var part in parts) if (!geometry.ContainsKey(part.Key)) extra += part.Value.Length;
                    if (terrain != null && !geometry.ContainsKey(terrain.Item1)) extra += terrain.Item2.Length;
                    if (records.Count > 0 && (records.Count >= 128 || Math.Max(record.At, maxAt) - Math.Min(record.At, minAt) > 60000 || geometryBytes + extra > 2 * 1024 * 1024))
                    {
                        Write(records, geometry); records.Clear(); geometry.Clear();
                        minAt = long.MaxValue; maxAt = long.MinValue; geometryBytes = 0; needsParts = true;
                    }
                    if (needsParts) foreach (var part in parts) if (!geometry.ContainsKey(part.Key)) { geometry.Add(part.Key, part.Value); geometryBytes += part.Value.Length; }
                    if (terrain != null && !geometry.ContainsKey(terrain.Item1)) { geometry.Add(terrain.Item1, terrain.Item2); geometryBytes += terrain.Item2.Length; }
                    minAt = Math.Min(minAt, record.At); maxAt = Math.Max(maxAt, record.At);
                    records.Add(record);
                }
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped != 0)
                {
                    var gapAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (records.Count > 0 && (records.Count >= 128 || Math.Max(gapAt, maxAt) - Math.Min(gapAt, minAt) > 60000))
                    { Write(records, geometry); records.Clear(); geometry.Clear(); }
                    records.Add(new ReplayRecord { Kind = "gap", At = gapAt, Value = dropped, Detail = "Recorder queue overflow" });
                }
                if (records.Count == 0) { _store.Prune(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); continue; }
                Write(records, geometry);
            }
        }
        catch (Exception e) { _error = "Replay storage unavailable: " + e.Message; }
        finally { _layouts.Clear(); }
    }

    private void Write(List<ReplayRecord> records, Dictionary<string, byte[]> geometry)
    {
        try
        {
            // Coalesce only matching damage targets/sources/types inside a 250 ms window.
            var damage = new Dictionary<string, ReplayRecord>();
            var compact = new List<ReplayRecord>();
            foreach (var record in records)
            {
                if (record.Kind != "damage") { compact.Add(record); continue; }
                var key = record.IncidentId + "/" + record.Entity + "/" + record.Source + "/" + record.Detail + "/" + record.At / 250;
                if (!damage.TryGetValue(key, out var prior)) { damage.Add(key, record); compact.Add(record); }
                else { prior.Value += record.Value; prior.Count += record.Count; }
            }
            var batch = new ReplayBatch { Session = _session, World = _world, Records = compact };
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(batch, Json));
            var id = ReplayChunkStore.NewId(records.Min(r => r.At), records.Count(r => r.Kind == "damage" || r.Kind == "physics" || r.Kind == "death" || r.Kind == "explosion" || r.Kind == "split" || r.Kind == "merge"));
            _store.Put(new ReplayChunk { Id = id, Data = ReplayChunkStore.Compress(bytes), Geometry = geometry }, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _error = "";
        }
        catch (Exception e) { _error = "Replay write failed: " + e.Message; Interlocked.Add(ref _dropped, records.Count); }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        // Do not stall the simulation thread on disk I/O; the worker drains independently.
    }
}
