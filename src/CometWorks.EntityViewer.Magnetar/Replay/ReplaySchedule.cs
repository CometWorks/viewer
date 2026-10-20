#nullable enable
using System;
using CometWorks.EntityViewer.Replay;

namespace CometWorks.EntityViewer.Magnetar.Replay;

// Monotonic milliseconds govern cadence and hysteresis; wall-clock jumps cannot extend incidents.
internal sealed class ReplaySchedule
{
    public const int LowInterval = 1000;
    public const int HighInterval = 200;
    public const int QuietTail = ReplayIncidentTracker.QuietMs;
    public double HighUntil { get; private set; }
    private double _next;
    private double _lastWritten = double.NegativeInfinity;
    private ReplayRecord? _written;
    private ReplayRecord? _scratch;
    private bool _observed, _hadVelocity;
    private double _observedAt, _vx, _vy, _vz;

    // Only suppressed samples are reused. Emitted records and their arrays stay immutable.
    public ReplayRecord RentSample() => _scratch ??= new ReplayRecord();
    public void DetachSample() { _scratch = null; }
    private int _anomalies;
    private double _nextTrigger;

    public void Invalidate() { _written = null; }
    public void Trigger(double now) { HighUntil = now + QuietTail; _next = Math.Min(_next, now); }
    public void ContextUntil(double until, double now) { HighUntil = until; if (until > now) _next = Math.Min(_next, now); }
    public bool Due(double now) => now >= _next;
    public bool IsHigh(double now) => now < HighUntil;

    public bool Observe(ReplayRecord record, double now, out bool anomaly)
    {
        record.High = IsHigh(now);
        _next = now + (record.High ? HighInterval : LowInterval);
        var dt = _observed ? (now - _observedAt) / 1000 : 0;
        var velocity = record.Velocity;
        var delta = velocity == null ? (_hadVelocity ? double.PositiveInfinity : 0) : !_hadVelocity ? double.PositiveInfinity
            : Square(velocity[0] - _vx) + Square(velocity[1] - _vy) + Square(velocity[2] - _vz);
        var unstable = Sum(record.AngularVelocity, null) > 25 || _observed && dt > 0 && dt < 3 && delta > 10000 * dt * dt;
        _anomalies = unstable ? _anomalies + 1 : 0;
        anomaly = _anomalies >= 2 && now >= _nextTrigger;
        if (anomaly) { Trigger(now); _nextTrigger = now + 5000; record.High = true; }
        _observed = true; _observedAt = record.Elapsed; _hadVelocity = velocity != null;
        if (velocity != null) { _vx = velocity[0]; _vy = velocity[1]; _vz = velocity[2]; }
        if (_written != null && record.High == _written.High && now - _lastWritten < 30000
            && DistanceSquared(record.Position, _written.Position) < (record.High ? 0.0001 : 0.0625)
            && DistanceSquared(record.Velocity, _written.Velocity) < 0.01
            && DistanceSquared(record.AngularVelocity, _written.AngularVelocity) < 0.0025
            && DistanceSquared(record.Rotation, _written.Rotation) < (record.High ? 0.000001 : 0.0001)
            && DistanceSquared(record.Gravity, _written.Gravity) < 0.0001 && DistanceSquared(record.Color, _written.Color) < 0.0001
            && record.Movement == _written.Movement && record.Actions == _written.Actions && record.Character == _written.Character && record.Weapon == _written.Weapon && record.Related == _written.Related
            && DistanceSquared(record.View, _written.View) < 1
            && Math.Abs(record.ModelScale - _written.ModelScale) < 0.001
            && record.Model == _written.Model && record.Mod == _written.Mod && record.Detail == _written.Detail
            && DistanceSquared(record.Size, _written.Size) < 0.0001 && record.Source == _written.Source && record.Name == _written.Name)
            return false;
        if (ReferenceEquals(_scratch, record)) _scratch = null;
        _written = record;
        _lastWritten = now;
        return true;
    }

    private static double Square(double value) => value * value;
    private static double DistanceSquared(double[]? a, double[]? b) => a == null || b == null ? (a == b ? 0 : double.PositiveInfinity) : Sum(a, b);
    private static double Sum(double[]? a, double[]? b)
    {
        if (a == null) return 0;
        double sum = 0;
        for (var i = 0; i < a.Length; i++) { var d = a[i] - (b == null ? 0 : b[i]); sum += d * d; }
        return sum;
    }
}
