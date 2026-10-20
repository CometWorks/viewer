using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CometWorks.EntityViewer.Magnetar.Model;
using CometWorks.EntityViewer.Replay;
using Sandbox.Game.Entities;
using VRage.Game.Voxels;
using VRage.Voxels;
using VRageMath;

namespace CometWorks.EntityViewer.Magnetar.Replay;

// Discovery and immutable request creation share the recorder's update budget. One worker
// reads at most ten small ranges/sec; unchanged samples are reused, never read each frame.
internal sealed class ReplayVoxelCapture : IDisposable
{
    private const int MaxChunks = 128;
    private readonly ReplayWriter _writer;
    private readonly Dictionary<long, Body> _bodies = new Dictionary<long, Body>(); // simulation thread
    private readonly List<Body> _bodyScan = new List<Body>();
    private readonly List<string> _expired = new List<string>(); // worker only
    private readonly BlockingCollection<Request> _requests = new BlockingCollection<Request>(64);
    private readonly ConcurrentQueue<Change> _changes = new ConcurrentQueue<Change>();
    private readonly Dictionary<string, Chunk> _chunks = new Dictionary<string, Chunk>(); // worker only
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private int _changeCount;
    private long _overflowAt;
    private long _nextCoverageWarning;
    private readonly Task _worker;
    private sealed class Body
    {
        public string Id;
        public MyVoxelBase Voxel;
        public IMyStorage Storage;
        public Action<Vector3I, Vector3I, MyStorageDataTypeFlags> Handler;
        public long Revision;
        public volatile bool Active = true;
    }
    private sealed class Request
    {
        public Body Body;
        public Vector3I Min, Max;
        public Vector3D Origin, Center;
        public Quaternion Rotation;
        public long At;
    }
    private sealed class Change { public Body Body; public Vector3I Min, Max; public long At; }
    private sealed class Chunk
    {
        public Body Body;
        public Vector3I Min, Max;
        public Vector3D Origin;
        public Quaternion Rotation;
        public int Lod;
        public ReplayVoxelPage Page;
        public long LastWanted, Checkpoint, Observed, RetryAt;
    }

    public ReplayVoxelCapture(ReplayWriter writer)
    {
        _writer = writer;
        _worker = Task.Run(Run);
    }
    public void Add(MyVoxelBase voxel)
    {
        if (_bodies.Count >= 64 || voxel == null || voxel.GetType().Name == "MyVoxelPhysics" || voxel.Storage == null || _bodies.ContainsKey(voxel.EntityId)) return;
        var body = new Body { Id = voxel.EntityId.ToString(), Voxel = voxel, Storage = voxel.Storage };
        body.Handler = (min, max, flags) =>
        {
            if (!body.Active) return;
            Interlocked.Increment(ref body.Revision);
            var at = Utc();
            if (Interlocked.Increment(ref _changeCount) > 128)
            {
                Interlocked.Decrement(ref _changeCount);
                Interlocked.CompareExchange(ref _overflowAt, at, 0);
                return;
            }
            _changes.Enqueue(new Change { Body = body, Min = min, Max = max, At = at });
        };
        body.Storage.RangeChanged += body.Handler;
        _bodies.Add(voxel.EntityId, body); _bodyScan.Add(body);
    }
    public void Observe(Vector3D position, Vector3D size)
    {
        // Covers the selected grid and surrounding context; large areas use coarser samples.
        var half = Math.Min(1024, Math.Max(500, size.AbsMax() + 125));
        var area = new BoundingBoxD(position - half, position + half);
        for (var i = _bodyScan.Count - 1; i >= 0; i--)
        {
            var body = _bodyScan[i];
            var voxel = body.Voxel;
            if (!body.Active) continue;
            if (voxel.Closed || voxel.MarkedForClose || !ReferenceEquals(voxel.Storage, body.Storage))
            {
                body.Active = false; body.Storage.RangeChanged -= body.Handler;
                _bodies.Remove(voxel.EntityId); _bodyScan.RemoveAt(i);
                if (!voxel.Closed && !voxel.MarkedForClose) Add(voxel);
                continue;
            }
            if (!voxel.PositionComp.WorldAABB.Intersects(area)) continue;
            var origin = voxel.PositionLeftBottomCorner;
            var matrix = MatrixD.CreateWorld(origin, voxel.Orientation.Forward, voxel.Orientation.Up);
            var inverse = MatrixD.Invert(matrix);
            var local = BoundingBoxD.CreateInvalid();
            for (var corner = 0; corner < 8; corner++) local.Include(Vector3D.Transform(area.GetCorner(corner), inverse));
            var min = Vector3I.Max(voxel.StorageMin, Vector3I.Floor(local.Min) - 1);
            var max = Vector3I.Min(voxel.StorageMax, Vector3I.Ceiling(local.Max) + 1);
            if (!_requests.TryAdd(new Request { Body = body, Min = min, Max = max, Origin = origin,
                Rotation = Quaternion.CreateFromRotationMatrix(matrix), Center = position, At = Utc() })) break;
        }
    }
    private async Task Run()
    {
        try
        {
            var lossVersion = _writer.LossVersion;
            while (!_stop.IsCancellationRequested)
            {
                var now = Utc();
                if (lossVersion != _writer.LossVersion)
                { lossVersion = _writer.LossVersion; foreach (var pair in _chunks) Invalidate(pair.Key, pair.Value, now); }
                DrainChanges();
                // Bound pending work and keep recent nearby context in preference to old areas.
                for (var i = 0; i < 4 && _requests.TryTake(out var request); i++) RequestChunks(request);
                _expired.Clear();
                KeyValuePair<string, Chunk> pending = default;
                foreach (var pair in _chunks)
                {
                    var chunk = pair.Value;
                    if (!chunk.Body.Active || now - chunk.LastWanted > 60000)
                    {
                        Invalidate(pair.Key, chunk, now); _expired.Add(pair.Key); continue;
                    }
                    if (chunk.Page != null && now - chunk.Checkpoint >= 10000) Publish(pair.Key, chunk, now);
                    if (chunk.Page == null && chunk.RetryAt <= now && (pending.Value == null || chunk.RetryAt < pending.Value.RetryAt)) pending = pair;
                }
                foreach (var key in _expired) _chunks.Remove(key);
                if (pending.Value != null) Read(pending.Key, pending.Value);
                await Task.Delay(100, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _writer.Add(new ReplayRecord { At = Utc(), Kind = "gap", Detail = "Terrain capture stopped: " + e.GetType().Name }); }
    }
    private void DrainChanges()
    {
        var overflow = Interlocked.Exchange(ref _overflowAt, 0);
        if (overflow != 0) foreach (var pair in _chunks) Invalidate(pair.Key, pair.Value, overflow);
        while (_changes.TryDequeue(out var change))
        {
            Interlocked.Decrement(ref _changeCount);
            foreach (var pair in _chunks)
            {
                var chunk = pair.Value;
                if (chunk.Body != change.Body) continue;
                var min = chunk.Min << chunk.Lod; var max = ((chunk.Max + 1) << chunk.Lod) - 1;
                if (min.X <= change.Max.X && max.X >= change.Min.X && min.Y <= change.Max.Y && max.Y >= change.Min.Y && min.Z <= change.Max.Z && max.Z >= change.Min.Z)
                    Invalidate(pair.Key, chunk, change.At);
            }
        }
    }
    private void RequestChunks(Request request)
    {
        if (!request.Body.Active || request.At < Utc() - 15000) return;
        var lod = 0;
        // At most 27 tiles per requested context, with shared border samples (33³).
        while (lod < 6 && Count(request.Min >> lod, request.Max >> lod) > 27) lod++;
        var lower = request.Min >> lod; var upper = request.Max >> lod;
        for (var z = lower.Z / 32 * 32; z <= upper.Z; z += 32)
        for (var y = lower.Y / 32 * 32; y <= upper.Y; y += 32)
        for (var x = lower.X / 32 * 32; x <= upper.X; x += 32)
        {
            var min = new Vector3I(x, y, z);
            var max = Vector3I.Min(min + 32, (request.Body.Storage.Size - 1) >> lod);
            if (max.X <= min.X || max.Y <= min.Y || max.Z <= min.Z) continue;
            var key = "voxel:" + request.Body.Id + "/" + lod + "/" + x + "," + y + "," + z;
            if (_chunks.TryGetValue(key, out var existing))
            {
                existing.LastWanted = request.At;
                if (existing.Origin != request.Origin || existing.Rotation != request.Rotation)
                { Invalidate(key, existing, request.At); existing.Origin = request.Origin; existing.Rotation = request.Rotation; }
                continue;
            }
            if (_chunks.Count >= MaxChunks)
            {
                if (request.At >= _nextCoverageWarning)
                {
                    _writer.Add(new ReplayRecord { At = request.At, Kind = "voxel-coverage", Type = "voxel", Position = V(request.Center),
                        Detail = "Terrain context incomplete: active chunk budget reached" });
                    _nextCoverageWarning = request.At + 10000;
                }
                continue;
            } // Coverage is explicit in the browser; never fabricate uncaptured terrain.
            _chunks[key] = new Chunk { Body = request.Body, Min = min, Max = max, Lod = lod,
                Origin = request.Origin, Rotation = request.Rotation, LastWanted = request.At };
        }
    }
    private static long Count(Vector3I min, Vector3I max) => (long)(max.X / 32 - min.X / 32 + 1) * (max.Y / 32 - min.Y / 32 + 1) * (max.Z / 32 - min.Z / 32 + 1);
    private void Read(string key, Chunk chunk)
    {
        chunk.RetryAt = Utc() + 2000;
        try
        {
            var revision = Interlocked.Read(ref chunk.Body.Revision);
            var data = new MyStorageData(); data.Resize(chunk.Max - chunk.Min + 1);
            using (var pin = chunk.Body.Storage.Pin())
            {
                if (!pin.Valid || !chunk.Body.Active) return;
                var flags = MyVoxelRequestFlags.ConsiderContent | MyVoxelRequestFlags.SurfaceMaterial;
                chunk.Body.Storage.ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, chunk.Lod, chunk.Min, chunk.Max, ref flags);
            }
            var observed = Utc(); // Never backdate a delayed read to the request or edit timestamp.
            if (revision != Interlocked.Read(ref chunk.Body.Revision)) return;
            var content = new byte[data.Size3D.Size]; var materials = new byte[content.Length];
            Array.Copy(data[MyStorageDataTypeEnum.Content], content, content.Length);
            Array.Copy(data[MyStorageDataTypeEnum.Material], materials, materials.Length);
            var palette = GridRenderSceneInspector.BuildVoxelMaterials(new List<ViewerVoxelDataChunk> { new ViewerVoxelDataChunk { Materials = materials } }, new List<string>());
            if (revision != Interlocked.Read(ref chunk.Body.Revision)) return;
            chunk.Page = new ReplayVoxelPage { Body = chunk.Body.Id, Origin = V(chunk.Origin),
                Rotation = new[] { (double)chunk.Rotation.X, chunk.Rotation.Y, chunk.Rotation.Z, chunk.Rotation.W },
                Min = C(chunk.Min), Size = C(data.Size3D), Lod = chunk.Lod, Content = content, Materials = materials, Palette = palette.ToArray() };
            chunk.Observed = observed;
            Publish(key, chunk, observed);
        }
        catch (Exception) { Invalidate(key, chunk, Utc()); } // Unavailable/closing storage is unknown, not empty terrain.
    }
    private void Publish(string key, Chunk chunk, long now)
    {
        _writer.Add(new ReplayRecord { At = now, Kind = "voxel-sample", Type = "voxel", Entity = key,
            Source = chunk.Body.Id, Terrain = chunk.Page, Value = chunk.Observed,
            Position = V(chunk.Origin + Vector3D.Transform((Vector3D)((chunk.Min + chunk.Max) << chunk.Lod) * .5, chunk.Rotation)), Detail = "Terrain observation; " + (1 << chunk.Lod) + " m samples" });
        chunk.Checkpoint = now;
    }
    private void Invalidate(string key, Chunk chunk, long at)
    {
        chunk.Page = null;
        _writer.Add(new ReplayRecord { At = at, Kind = "voxel-invalidated", Type = "voxel", Entity = key, Source = chunk.Body.Id,
            Detail = "Terrain changed or left recording coverage; unknown until sampled again" });
    }
    private static long Utc() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static double[] V(Vector3D value) => new[] { value.X, value.Y, value.Z };
    private static int[] C(Vector3I value) => new[] { value.X, value.Y, value.Z };
    public void Dispose()
    {
        foreach (var body in _bodies.Values) { body.Active = false; body.Storage.RangeChanged -= body.Handler; }
        _bodies.Clear(); _bodyScan.Clear(); _stop.Cancel(); // No simulation-thread wait for storage/disk locks.
    }
}
