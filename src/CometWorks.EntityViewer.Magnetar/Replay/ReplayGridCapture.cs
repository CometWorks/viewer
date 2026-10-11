using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CometWorks.EntityViewer.Replay;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRageMath;

namespace CometWorks.EntityViewer.Magnetar.Replay;

// Only small immutable block observations cross to the worker. No full-grid object builder or mesh capture.
internal sealed class ReplayGridCapture : IDisposable
{
    private readonly MyCubeGrid _grid;
    private readonly ReplayWriter _writer;
    private readonly string _id;
    private readonly ConcurrentQueue<Vector3I> _dirty = new ConcurrentQueue<Vector3I>();
    private Dictionary<Vector3I, ReplayBlock> _known = new Dictionary<Vector3I, ReplayBlock>();
    private HashSet<MySlimBlock>.Enumerator _iterator;
    private bool _scanning;
    private bool _ready;
    private int _pending;
    private int _topology;
    private int _scanTopology;
    private int _overflow;
    private long _lossVersion = -1;
    private double _nextScan;
    private double _retryUntil;
    private volatile bool _disposed;
    private readonly ReplayBlockBudget _budget;
    public bool Ready => _ready && _pending == 0 && _lossVersion == _writer.LossVersion;

    public ReplayGridCapture(MyCubeGrid grid, ReplayWriter writer, ReplayBlockBudget budget)
    {
        _budget = budget; _grid = grid; _writer = writer; _id = grid.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        grid.OnBlockAdded += TopologyChanged;
        grid.OnBlockRemoved += TopologyChanged;
        grid.OnBlockIntegrityChanged += Changed;
        grid.OnGridMerge += Rebased;
    }
    private void Rebased(MyCubeGrid first, MyCubeGrid second) { Interlocked.Increment(ref _topology); Interlocked.Exchange(ref _overflow, 1); }
    private void TopologyChanged(MySlimBlock block) { Interlocked.Increment(ref _topology); Changed(block); }
    private void Changed(MySlimBlock block)
    {
        if (_disposed || _grid.BlocksCount > ReplayLayoutPage.MaxBlocksPerGrid) return;
        if (Interlocked.Increment(ref _pending) > 1024) { Interlocked.Decrement(ref _pending); Interlocked.Exchange(ref _overflow, 1); return; }
        if (Interlocked.Increment(ref _budget.Pending) > 4096)
        { Interlocked.Decrement(ref _budget.Pending); Interlocked.Decrement(ref _pending); Interlocked.Exchange(ref _overflow, 1); return; }
        _dirty.Enqueue(block.Position);
    }
    public bool Update(double now, long started, long budget)
    {
        if (now < _retryUntil) return false;
        try { return UpdateCore(now, started, budget); }
        catch (Exception)
        {
            Invalidate(now); _retryUntil = now + 30000; _nextScan = _retryUntil;
            return true; // This grid becomes bounds-only; a bad block must not stop other capture.
        }
    }
    private bool UpdateCore(double now, long started, long budget)
    {
        if (_grid.BlocksCount > ReplayLayoutPage.MaxBlocksPerGrid) { DiscardPending(); return Invalidate(now); }
        var changed = false;
        if (_lossVersion != _writer.LossVersion || Interlocked.Exchange(ref _overflow, 0) != 0)
        {
            _lossVersion = _writer.LossVersion;
            changed = Invalidate(now); _nextScan = 0;
        }
        if (now >= _nextScan && !_scanning)
        {
            if (!_ready) { Emit("layout-reset", now); if (_known.Count > 0) { _budget.Count -= _known.Count; _known = new Dictionary<Vector3I, ReplayBlock>(); } }
            _iterator = _grid.GetBlocks().GetEnumerator(); _scanTopology = _topology; _scanning = true;
        }
        for (var work = 0; work < 4 && Stopwatch.GetTimestamp() - started < budget; work++)
        {
            if (_dirty.TryDequeue(out var cell))
            {
                Interlocked.Decrement(ref _pending); Interlocked.Decrement(ref _budget.Pending);
                var block = _grid.GetCubeBlock(cell);
                if (block != null)
                {
                    if (block.Position != cell && _known.Remove(cell)) { _budget.Count--; Emit("layout-remove", now, new ReplayBlock { Cell = C(cell) }); changed = true; }
                    changed |= Observe(block, now);
                }
                else if (_known.Remove(cell)) { _budget.Count--; Emit("layout-remove", now, new ReplayBlock { Cell = C(cell) }); changed = true; }
                continue;
            }
            if (!_scanning) break;
            // A HashSet enumerator cannot survive structural mutations. Restart across ticks without copying the set.
            if (_scanTopology != _topology) { _iterator.Dispose(); _scanning = false; _nextScan = 0; break; }
            bool next;
            try { next = _iterator.MoveNext(); }
            catch (InvalidOperationException) { _scanning = false; _nextScan = 0; break; }
            if (next) changed |= Observe(_iterator.Current, now);
            else
            {
                _iterator.Dispose(); _scanning = false; _nextScan = now + 30000;
                if (!_ready) { Emit("layout-ready", now); _ready = true; changed = true; }
                break;
            }
        }
        return changed;
    }
    private bool Invalidate(double now)
    {
        var changed = _ready || _scanning;
        if (changed) Emit("layout-reset", now);
        if (_scanning) _iterator.Dispose();
        _scanning = false; _ready = false; if (_known.Count > 0) { _budget.Count -= _known.Count; _known = new Dictionary<Vector3I, ReplayBlock>(); }
        return changed;
    }
    private bool Observe(MySlimBlock block, double now)
    {
        if (block == null) return false;
        var captured = Capture(block);
        if (_known.TryGetValue(block.Position, out var previous) && Equal(previous, captured)) return false;
        if (!_known.ContainsKey(block.Position))
        {
            if (_budget.Count >= 262144) { Invalidate(now); _nextScan = now + 30000; return true; }
            _budget.Count++;
        }
        _known[block.Position] = captured; Emit("layout-block", now, captured); return true;
    }
    private static bool Equal(ReplayBlock a, ReplayBlock b) => a.Definition == b.Definition && a.Model == b.Model && a.Mod == b.Mod && a.Topology == b.Topology
        && a.Integrity == b.Integrity && a.Build == b.Build && a.Min.SequenceEqual(b.Min) && a.Max.SequenceEqual(b.Max)
        && a.Translation.SequenceEqual(b.Translation) && a.Rotation.SequenceEqual(b.Rotation) && a.Color.SequenceEqual(b.Color);
    private static ReplayBlock Capture(MySlimBlock block)
    {
        block.GetLocalMatrix(out var local);
        var model = block.CalculateCurrentModel(out var modelOrientation);
        // Generated armor has no single MWM; the viewer uses its existing authored topology fallback.
        if (string.IsNullOrEmpty(model)) modelOrientation = local;
        modelOrientation.Translation = local.Translation;
        var q = Quaternion.CreateFromRotationMatrix(modelOrientation);
        var definition = block.BlockDefinition;
        return new ReplayBlock
        {
            Cell = C(block.Position), Min = C(block.Min), Max = C(block.Max), Definition = definition.Id.ToString(),
            Model = LogicalModel(model, definition.Context?.ModPath), Mod = definition.Context?.ModId ?? "",
            Topology = definition.CubeDefinition?.CubeTopology.ToString() ?? "",
            Translation = V(local.Translation), Rotation = new[] { (double)q.X, q.Y, q.Z, q.W }, Color = V(block.ColorMaskHSV),
            Integrity = Math.Round(block.MaxIntegrity > 0 ? block.Integrity / block.MaxIntegrity : 0, 3), Build = Math.Round(block.BuildLevelRatio, 3)
        };
    }
    private static string LogicalModel(string model, string modPath)
    {
        var path = (model ?? "").Replace('\\', '/');
        foreach (var root in new[] { modPath, VRage.FileSystem.MyFileSystem.ContentPath })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return path.Substring(prefix.Length);
        }
        return System.IO.Path.IsPathRooted(path) || path.Contains(":") ? "" : path;
    }
    private void Emit(string kind, double now, ReplayBlock block = null) => _writer.Add(new ReplayRecord { At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Elapsed = now, Entity = _id, Kind = kind, Block = block, Count = kind == "layout-reset" ? _grid.BlocksCount : 0 });
    private static int[] C(Vector3I v) => new[] { v.X, v.Y, v.Z };
    private static double[] V(Vector3 v) => new[] { Math.Round((double)v.X, 4), Math.Round((double)v.Y, 4), Math.Round((double)v.Z, 4) };
    public void Dispose()
    {
        _disposed = true; if (_known.Count > 0) { _budget.Count -= _known.Count; _known = new Dictionary<Vector3I, ReplayBlock>(); }
        _grid.OnGridMerge -= Rebased;
        _grid.OnBlockAdded -= TopologyChanged; _grid.OnBlockRemoved -= TopologyChanged; _grid.OnBlockIntegrityChanged -= Changed;
        if (_scanning) _iterator.Dispose();
        DiscardPending();
    }
    private void DiscardPending()
    {
        while (_dirty.TryDequeue(out _)) { Interlocked.Decrement(ref _pending); Interlocked.Decrement(ref _budget.Pending); }
    }
}

internal sealed class ReplayBlockBudget { public int Count; public int Pending; }
