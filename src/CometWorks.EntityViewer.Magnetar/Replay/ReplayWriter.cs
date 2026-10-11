#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly BlockingCollection<Tuple<ReplayRecord, long>> _queue = new BlockingCollection<Tuple<ReplayRecord, long>>(4096);
    private readonly Task _worker;
    private readonly string _session;
    private readonly string _world;
    private readonly string _path;
    private long _dropped;
    private long _lossVersion;
    public long LossVersion => Interlocked.Read(ref _lossVersion);
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

    public void Add(ReplayRecord record)
    {
        if (_queue.IsAddingCompleted) return;
        try { if (!_queue.TryAdd(Tuple.Create(record, LossVersion))) { Interlocked.Increment(ref _dropped); Interlocked.Increment(ref _lossVersion); } }
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
            while (!_queue.IsCompleted)
            {
                var records = new List<ReplayRecord>();
                var captured = new List<Tuple<ReplayRecord, long>>();
                var until = DateTime.UtcNow.AddSeconds(5);
                while (captured.Count < 512 && DateTime.UtcNow < until && !_queue.IsCompleted)
                    if (_queue.TryTake(out var item, 100)) captured.Add(item);
                var version = LossVersion;
                if (version != layoutVersion) { _layouts.Clear(); layoutVersion = version; }
                var geometry = new Dictionary<string, byte[]>();
                foreach (var item in captured)
                {
                    var record = item.Item1;
                    if (item.Item2 != version && record.Kind.StartsWith("layout-", StringComparison.Ordinal)) continue;
                    if (_layouts.Apply(record)) continue;
                    var parts = _layouts.Attach(record);
                    var extra = parts.Where(p => !geometry.ContainsKey(p.Key)).Sum(p => p.Value.Length);
                    if (records.Count > 0 && (records.Count >= 128 || record.At - records[0].At > 60000 || geometry.Sum(p => p.Value.Length) + extra > 2 * 1024 * 1024))
                    { Write(records, geometry); records.Clear(); geometry.Clear(); }
                    foreach (var part in parts) geometry[part.Key] = part.Value;
                    records.Add(record);
                }
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped != 0) records.Add(new ReplayRecord { Kind = "gap", At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Value = dropped, Detail = "Recorder queue overflow" });
                if (records.Count == 0) { _store.Prune(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); continue; }
                Write(records, geometry);
            }
        }
        catch (Exception e) { _error = "Replay storage unavailable: " + e.Message; }
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
                var key = record.Entity + "/" + record.Source + "/" + record.Detail + "/" + record.At / 250;
                if (!damage.TryGetValue(key, out var prior)) { damage.Add(key, record); compact.Add(record); }
                else { prior.Value += record.Value; prior.Count += record.Count; }
            }
            var batch = new ReplayBatch { Session = _session, World = _world, Records = compact };
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(batch, Json));
            var id = ReplayChunkStore.NewId(records.Min(r => r.At), records.Count(r => r.Kind == "damage" || r.Kind == "physics"));
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
