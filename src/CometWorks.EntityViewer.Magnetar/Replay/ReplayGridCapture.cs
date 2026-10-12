using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CometWorks.EntityViewer.Replay;
using Sandbox.Game.Entities;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Cube;
using VRageMath;

namespace CometWorks.EntityViewer.Magnetar.Replay;

// Only small immutable block observations cross to the worker. No full-grid object builder or mesh capture.
internal sealed class ReplayGridCapture : IDisposable
{
    private readonly MyCubeGrid _grid;
    private readonly Action<ReplayRecord> _event;
    private struct Change { public Vector3I Cell; public long At; public string Kind; }
    private readonly ReplayWriter _writer;
    private readonly string _id;
    private readonly ConcurrentQueue<Change> _dirty = new ConcurrentQueue<Change>();
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

    public ReplayGridCapture(MyCubeGrid grid, ReplayWriter writer, ReplayBlockBudget budget, Action<ReplayRecord> eventSink)
    {
        _event = eventSink; _budget = budget; _grid = grid; _writer = writer; _id = grid.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        grid.OnBlockAdded += Added;
        grid.OnBlockRemoved += Removed;
        grid.OnBlockIntegrityChanged += Changed;
        grid.OnGridMerge += Rebased;
        grid.OnGridSplit += Split;
    }
    private void Rebased(MyCubeGrid first, MyCubeGrid second) { Interlocked.Increment(ref _topology); Interlocked.Exchange(ref _overflow, 1); Lineage("merge", first, second); }
    private void Split(MyCubeGrid first, MyCubeGrid second) => Lineage("split", first, second);
    private void Lineage(string kind, MyCubeGrid first, MyCubeGrid second)
    {
        if (_disposed || first != _grid) return;
        try { _event(new ReplayRecord { At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Kind = kind, Entity = first.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Related = second.EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture), Position = new[] { first.WorldMatrix.Translation.X, first.WorldMatrix.Translation.Y, first.WorldMatrix.Translation.Z },
            Detail = kind == "split" ? "Original grid → fragment" : "Merged grid pair; survivor determined by subsequent observations" }); }
        catch { Interlocked.Exchange(ref _overflow, 1); }
    }
    private void Added(MySlimBlock block) { Interlocked.Increment(ref _topology); Changed(block, "block-added"); }
    private void Removed(MySlimBlock block) { Interlocked.Increment(ref _topology); Changed(block, "block-removed"); }
    private void Changed(MySlimBlock block) => Changed(block, null);
    private void Changed(MySlimBlock block, string kind)
    {
        if (_disposed || _grid.BlocksCount > ReplayLayoutPage.MaxBlocksPerGrid) return;
        if (Interlocked.Increment(ref _pending) > 1024) { Interlocked.Decrement(ref _pending); Interlocked.Exchange(ref _overflow, 1); return; }
        if (Interlocked.Increment(ref _budget.Pending) > 4096)
        { Interlocked.Decrement(ref _budget.Pending); Interlocked.Decrement(ref _pending); Interlocked.Exchange(ref _overflow, 1); return; }
        _dirty.Enqueue(new Change { Cell = block.Position, At = kind == null ? 0 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Kind = kind });
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
            if (_dirty.TryDequeue(out var change))
            {
                Interlocked.Decrement(ref _pending); Interlocked.Decrement(ref _budget.Pending);
                var cell = change.Cell;
                if (change.Kind != null) _event(new ReplayRecord { At = change.At, Elapsed = now, Kind = change.Kind, Entity = _id, Cell = C(cell), Position = WorldCell(cell), Detail = "Block topology callback; layout captured subsequently" });
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
        _known[block.Position] = captured; Emit("layout-block", now, captured, PrepareParts(block)); return true;
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
    private static Action<ReplayRecord> PrepareParts(MySlimBlock block)
    {
        if (!block.ShowParts || block.BlockDefinition.CubeDefinition == null) return null;
        var definition = block.BlockDefinition; var position = block.Position; var gridSize = block.CubeGrid.GridSize;
        block.Orientation.GetMatrix(out Matrix rotation);
        // Immutable definition + value snapshots only: no live entity traversal or MWM load
        // on the simulation thread, and no delayed reads of changing block state.
        return record => { var copy = record.Block.StorageCopy(); CaptureParts(definition, position, rotation, gridSize, copy); record.Block = copy; };
    }
    private static void CaptureParts(MyCubeBlockDefinition definition, Vector3I position, Matrix rotation, float gridSize, ReplayBlock record)
    {
        var models = new List<string>(); var matrices = new List<MatrixD>();
        var normals = new List<Vector3>(); var offsets = new List<Vector4UByte>();
        MyCubeGrid.GetCubeParts(definition, position, rotation, gridSize,
            models, matrices, normals, offsets, topologyCheck: true);
        var parts = new List<ReplayModelPart>(); var patterns = new List<byte>();
        for (var i = 0; i < models.Count && i < 32; i++)
        {
            var m = matrices[i];
            // Cube-part matrices already contain orientation and grid scale. Store translation
            // once per block so identical authored surfaces deduplicate across the entire grid.
            m.Translation = Vector3D.Zero;
            parts.Add(new ReplayModelPart { Model = LogicalModel(models[i], definition.Context?.ModPath),
                Matrix = new[] { m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44 } });
            var p = offsets[i]; patterns.AddRange(new[] { p.X, p.Y, p.Z, p.W });
        }
        if (parts.Count > 0) { record.Parts = new ReplayModelParts { Parts = parts.ToArray() }; record.Patterns = patterns.ToArray(); }
    }

    internal static string LogicalModel(string model, string modPath)
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
    private void Emit(string kind, double now, ReplayBlock block = null, Action<ReplayRecord> prepare = null) => _writer.Add(new ReplayRecord { At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Elapsed = now, Entity = _id, Kind = kind, Block = block, Count = kind == "layout-reset" ? _grid.BlocksCount : 0 }, prepare);
    private double[] WorldCell(Vector3I cell) { var p = _grid.GridIntegerToWorld(cell); return p.IsValid() ? new[] { p.X, p.Y, p.Z } : null; }
    private static int[] C(Vector3I v) => new[] { v.X, v.Y, v.Z };
    private static double[] V(Vector3 v) => new[] { Math.Round((double)v.X, 4), Math.Round((double)v.Y, 4), Math.Round((double)v.Z, 4) };
    public void Dispose()
    {
        _disposed = true; if (_known.Count > 0) { _budget.Count -= _known.Count; _known = new Dictionary<Vector3I, ReplayBlock>(); }
        _grid.OnGridMerge -= Rebased; _grid.OnGridSplit -= Split;
        _grid.OnBlockAdded -= Added; _grid.OnBlockRemoved -= Removed; _grid.OnBlockIntegrityChanged -= Changed;
        if (_scanning) _iterator.Dispose();
        DiscardPending();
    }
    private void DiscardPending()
    {
        while (_dirty.TryDequeue(out _)) { Interlocked.Decrement(ref _pending); Interlocked.Decrement(ref _budget.Pending); }
    }
}

internal sealed class ReplayBlockBudget { public int Count; public int Pending; }
