using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading;
using CometWorks.EntityViewer.Replay;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.Game.Multiplayer;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace CometWorks.EntityViewer.Magnetar.Replay;

internal sealed class ServerReplayRecorder : IDisposable
{
    private readonly ReplayBlockBudget _blockBudget = new ReplayBlockBudget();
    private const int MaxTracked = 8192;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<long, Track> _grids = new Dictionary<long, Track>();
    private readonly List<Track> _scan = new List<Track>();
    private readonly Dictionary<string, Track> _players = new Dictionary<string, Track>();
    private readonly ConcurrentQueue<ReplayRecord> _damage = new ConcurrentQueue<ReplayRecord>();
    private readonly ConcurrentQueue<MyEntity> _added = new ConcurrentQueue<MyEntity>();
    private int _damageCount;
    private int _addedCount;
    private long _lost;
    private int _cursor;
    private MySession _session;
    private MyDamageSystem _damageSystem;
    private int _damageGeneration;
    private ReplayWriter _writer;
    private ReplayWriter _retiringWriter;
    private double _nextPlayers;
    private double _nextHealth;
    private bool _active;
    private readonly Dictionary<long, Hotspot> _hotspots = new Dictionary<long, Hotspot>();
    private double _retryAt;
    private string _error = "";
    private sealed class Hotspot { public Vector3D Position; public double Until; }

    public ReplayPage Read(ReplayReadRequest request)
    {
        var page = _writer?.Read(request) ?? new ReplayPage { Error = "Session recorder is not ready." };
        if (_error.Length != 0) page.Error = _error;
        return page;
    }

    public void Update()
    {
        if (_clock.Elapsed.TotalMilliseconds < _retryAt) return;
        try { UpdateCore(); _error = ""; }
        catch (Exception e)
        {
            _error = "Replay capture failed: " + e.Message;
            Interlocked.Increment(ref _lost);
            _retryAt = _clock.Elapsed.TotalMilliseconds + 5000;
            Console.WriteLine(_error);
        }
    }

    private void UpdateCore()
    {
        var session = MySession.Static;
        if (session == null || !session.Ready) { if (_session != null) Reset(); return; }
        if (!ReferenceEquals(session, _session)) Start(session);
        var now = _clock.Elapsed.TotalMilliseconds;
        var started = Stopwatch.GetTimestamp();
        var budget = Stopwatch.Frequency / 2000; // 0.5 ms of capture work per update, checked between entities.
        for (var i = 0; i < 32 && Stopwatch.GetTimestamp() - started < budget && _added.TryDequeue(out var entity); i++)
        {
            Interlocked.Decrement(ref _addedCount);
            if (entity is MyCubeGrid grid && !_grids.ContainsKey(grid.EntityId))
            {
                if (_grids.Count >= MaxTracked) { Interlocked.Increment(ref _lost); continue; }
                var track = new Track { Grid = grid, Geometry = new ReplayGridCapture(grid, _writer, _blockBudget) };
                _grids.Add(grid.EntityId, track); _scan.Add(track);
            }
        }
        for (var i = 0; i < 64 && Stopwatch.GetTimestamp() - started < budget && _damage.TryDequeue(out var damage); i++)
        {
            Interlocked.Decrement(ref _damageCount);
            _writer.Add(damage);
            if (long.TryParse(damage.Entity, out var id) && _grids.TryGetValue(id, out var damaged)) damaged.Schedule.Trigger(now);
            if (damage.Position != null) Hot(id, new Vector3D(damage.Position[0], damage.Position[1], damage.Position[2]), now);
        }
        if (now >= _nextPlayers && Stopwatch.GetTimestamp() - started < budget)
        {
            CapturePlayers(now, started, budget);
            _nextPlayers = now + (_hotspots.Count > 0 ? ReplaySchedule.HighInterval : ReplaySchedule.LowInterval);
        }
        var examined = 0;
        while (_scan.Count > 0 && examined++ < 128 && Stopwatch.GetTimestamp() - started < budget)
        {
            if (_cursor >= _scan.Count) _cursor = 0;
            var track = _scan[_cursor];
            var grid = track.Grid;
            if (grid.Closed || grid.MarkedForClose)
            {
                Emit("removed", grid.EntityId.ToString(CultureInfo.InvariantCulture), now);
                track.Geometry.Dispose();
                _grids.Remove(grid.EntityId);
                _scan[_cursor] = _scan[_scan.Count - 1]; _scan.RemoveAt(_scan.Count - 1);
                continue;
            }
            _cursor++;
            if (track.Geometry.Update(now, started, budget)) track.Schedule.Invalidate();
            if (!track.Schedule.Due(now)) continue;
            var position = grid.PositionComp.GetPosition();
            if (Nearby(position, now)) track.Schedule.Trigger(now);
            var record = Sample(grid, now);
            record.GeometryReady = track.Geometry.Ready;
            if (track.Schedule.Observe(record, now, out var anomaly)) _writer.Add(record);
            if (anomaly)
            {
                var evidence = Sample(grid, now);
                evidence.Kind = "physics";
                evidence.Detail = "Suspected instability: sustained angular speed >5 rad/s or acceleration >100 m/s². Correlation, not attribution.";
                _writer.Add(evidence);
                Hot(grid.EntityId, position, now);
            }
        }
        if (now >= _nextHealth)
        {
            foreach (var key in _hotspots.Keys.Where(key => _hotspots[key].Until <= now).ToArray()) _hotspots.Remove(key);
            var lost = Interlocked.Exchange(ref _lost, 0);
            if (lost > 0) _writer.Add(new ReplayRecord { At = Utc(), Elapsed = now, Kind = "gap", Value = lost, Detail = "Capture event/track limit" });
            _writer.Add(new ReplayRecord { At = Utc(), Elapsed = now, Tick = (long)MySandboxGame.Static.SimulationFrameCounter, Kind = "health", Value = Sync.ServerSimulationRatio, Count = _grids.Count, Detail = "Simulation speed; 1 Hz baseline / 5 Hz incident target" });
            _nextHealth = now + 5000;
        }
    }

    private void Start(MySession session)
    {
        Reset();
        _session = session; _active = true;
        var sessionId = Guid.NewGuid().ToString("N");
        _writer = new ReplayWriter(Path.Combine(session.CurrentPath, "EntityViewerReplay"), sessionId, session.WorldId.ToString("N"), _retiringWriter);
        _retiringWriter = null;
        MyEntities.OnEntityAdd += OnAdded;
        // One discovery pass per world session; subsequent discovery uses lifecycle events.
        foreach (var entity in MyEntities.GetEntities()) OnAdded(entity);
        _damageSystem = MyDamageSystem.Static;
        var generation = _damageGeneration;
        _damageSystem?.RegisterAfterDamageHandler(int.MaxValue, (target, info) => { if (generation == Volatile.Read(ref _damageGeneration)) OnDamage(target, info); });
    }

    private void OnAdded(MyEntity entity)
    {
        if (!_active || !(entity is MyCubeGrid)) return;
        if (Interlocked.Increment(ref _addedCount) > MaxTracked) { Interlocked.Decrement(ref _addedCount); Interlocked.Increment(ref _lost); return; }
        _added.Enqueue(entity);
    }

    private void OnDamage(object target, MyDamageInformation info)
    {
        if (!_active || !ReferenceEquals(MyDamageSystem.Static, _damageSystem) || !(target is MySlimBlock block) || info.Amount <= 0) return;
        if (Interlocked.Increment(ref _damageCount) > 1024) { Interlocked.Decrement(ref _damageCount); Interlocked.Increment(ref _lost); return; }
        try
        {
            var p = block.WorldPosition;
            if (float.IsNaN(info.Amount) || float.IsInfinity(info.Amount)) throw new InvalidDataException("Non-finite damage amount");
            _damage.Enqueue(new ReplayRecord
            {
                At = Utc(), Elapsed = _clock.Elapsed.TotalMilliseconds, Tick = (long)(MySandboxGame.Static?.SimulationFrameCounter ?? 0), Kind = "damage", Entity = block.CubeGrid.EntityId.ToString(CultureInfo.InvariantCulture),
                Source = info.AttackerId == 0 ? "" : info.AttackerId.ToString(CultureInfo.InvariantCulture), Position = V(p), Value = info.Amount, Count = 1,
                Detail = info.Type.ToString() + " cell " + block.Position + (info.IsDeformation ? " deformation" : "")
            });
        }
        catch
        {
            Interlocked.Decrement(ref _damageCount);
            Interlocked.Increment(ref _lost); // Observation must never interrupt game damage application.
        }
    }

    private void CapturePlayers(double now, long started, long budget)
    {
        var present = new HashSet<string>();
        var complete = true;
        var online = _session.Players.GetOnlinePlayers();
        if (online.Count > 128) Interlocked.Add(ref _lost, online.Count - 128);
        foreach (var player in online.Take(128))
        {
            if (Stopwatch.GetTimestamp() - started >= budget) { complete = false; break; }
            var controlled = player.Controller?.ControlledEntity?.Entity;
            if (controlled == null) continue;
            var id = "player:" + player.Id.SteamId + ":" + player.Id.SerialId;
            present.Add(id);
            if (!_players.TryGetValue(id, out var track)) { track = new Track(); _players.Add(id, track); }
            if (!track.Schedule.Due(now)) continue;
            var p = controlled.PositionComp.GetPosition();
            if (Nearby(p, now)) track.Schedule.Trigger(now);
            var sample = new ReplayRecord { At = Utc(), Elapsed = now, Kind = "sample", Type = "player", Entity = id, Name = (player.DisplayName ?? "").Substring(0, Math.Min(128, (player.DisplayName ?? "").Length)), Source = controlled.EntityId.ToString(CultureInfo.InvariantCulture), Position = V(p), Size = new[] { 2d, 2d, 2d } };
            if (track.Schedule.Observe(sample, now, out _)) _writer.Add(sample);
        }
        if (!complete) { Interlocked.Increment(ref _lost); return; }
        foreach (var id in _players.Keys.Where(id => !present.Contains(id)).ToArray()) { Emit("removed", id, now); _players.Remove(id); }
    }

    private ReplayRecord Sample(MyCubeGrid grid, double now)
    {
        var matrix = grid.WorldMatrix;
        var q = Quaternion.CreateFromRotationMatrix(matrix);
        var size = grid.PositionComp.LocalAABB.Size;
        var rotation = new[] { Math.Round(q.X, 4), Math.Round(q.Y, 4), Math.Round(q.Z, 4), Math.Round(q.W, 4) };
        if (rotation.Any(v => double.IsNaN(v) || double.IsInfinity(v))) { rotation = null; Interlocked.Increment(ref _lost); }
        return new ReplayRecord
        {
            At = Utc(), Elapsed = now, Tick = (long)MySandboxGame.Static.SimulationFrameCounter, Entity = grid.EntityId.ToString(CultureInfo.InvariantCulture),
            Name = (grid.DisplayName ?? "").Substring(0, Math.Min(128, (grid.DisplayName ?? "").Length)), Position = V(grid.PositionComp.WorldAABB.Center),
            Rotation = rotation, Origin = V(matrix.Translation), GridSize = grid.GridSize,
            Size = V(size), Velocity = V(grid.Physics?.LinearVelocity ?? Vector3.Zero), AngularVelocity = V(grid.Physics?.AngularVelocity ?? Vector3.Zero)
        };
    }
    private void Emit(string kind, string entity, double now) => _writer.Add(new ReplayRecord { At = Utc(), Elapsed = now, Entity = entity, Kind = kind });
    private static double[] V(Vector3D v) => v.IsValid() ? new[] { Math.Round(v.X, 2), Math.Round(v.Y, 2), Math.Round(v.Z, 2) } : null;
    private bool Nearby(Vector3D position, double now) => _hotspots.Values.Any(h => now < h.Until && Vector3D.DistanceSquared(position, h.Position) < 1000000);
    private void Hot(long id, Vector3D position, double now)
    {
        if (_hotspots.Count >= 32 && !_hotspots.ContainsKey(id)) { Interlocked.Increment(ref _lost); return; }
        _hotspots[id] = new Hotspot { Position = position, Until = now + ReplaySchedule.QuietTail };
    }
    private static long Utc() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private void Reset()
    {
        _active = false; Interlocked.Increment(ref _damageGeneration);
        MyEntities.OnEntityAdd -= OnAdded;
        // The game clears damage handlers on session unload. This callback is inert after Dispose.
        if (_writer != null) { _writer.Dispose(); _retiringWriter = _writer; }
        _writer = null; _session = null;
        foreach (var track in _scan) track.Geometry.Dispose();
        _grids.Clear(); _scan.Clear(); _players.Clear(); _hotspots.Clear(); _cursor = 0; _nextPlayers = 0; _nextHealth = 0;
        while (_added.TryDequeue(out _)) Interlocked.Decrement(ref _addedCount);
        while (_damage.TryDequeue(out _)) Interlocked.Decrement(ref _damageCount);
    }
    public void Dispose() => Reset();
    private sealed class Track { public MyCubeGrid Grid; public ReplayGridCapture Geometry; public ReplaySchedule Schedule = new ReplaySchedule(); }
}
