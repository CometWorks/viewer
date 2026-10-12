#nullable enable
using System;
using CometWorks.EntityViewer.Replay;

namespace CometWorks.EntityViewer.Magnetar.Replay;

// Monotonic milliseconds govern cadence and hysteresis; wall-clock jumps cannot extend incidents.
internal sealed class ReplaySchedule
{
    public const int LowInterval = 1000;
    public const int HighInterval = 200;
    public const int QuietTail = 15000;
    public double HighUntil { get; private set; }
    private double _next;
    private double _lastWritten = double.NegativeInfinity;
    private ReplayRecord? _written;
    private ReplayRecord? _observed;
    private int _anomalies;
    private double _nextTrigger;

    public void Invalidate() { _written = null; }
    public void Trigger(double now) { HighUntil = now + QuietTail; _next = Math.Min(_next, now); }
    public bool Due(double now) => now >= _next;
    public bool IsHigh(double now) => now < HighUntil;

    public bool Observe(ReplayRecord record, double now, out bool anomaly)
    {
        record.High = IsHigh(now);
        _next = now + (record.High ? HighInterval : LowInterval);
        var angular = Length(record.AngularVelocity);
        var dt = _observed == null ? 0 : (now - _observed.Elapsed) / 1000;
        var acceleration = _observed != null && dt > 0 && dt < 3 ? Distance(record.Velocity, _observed.Velocity) / dt : 0;
        _anomalies = angular > 5 || acceleration > 100 ? _anomalies + 1 : 0;
        anomaly = _anomalies >= 2 && now >= _nextTrigger;
        if (anomaly) { Trigger(now); _nextTrigger = now + 5000; record.High = true; }
        _observed = record;
        if (_written != null && record.High == _written.High && now - _lastWritten < 30000
            && Distance(record.Position, _written.Position) < (record.High ? 0.01 : 0.25)
            && Distance(record.Velocity, _written.Velocity) < 0.1
            && Distance(record.AngularVelocity, _written.AngularVelocity) < 0.05
            && Distance(record.Rotation, _written.Rotation) < (record.High ? 0.001 : 0.01)
            && Distance(record.Gravity, _written.Gravity) < 0.01 && Distance(record.Color, _written.Color) < 0.01
            && record.Actions == _written.Actions && record.Character == _written.Character && record.Weapon == _written.Weapon && record.Related == _written.Related
            && Distance(record.View, _written.View) < 1
            && record.Model == _written.Model && record.Mod == _written.Mod && record.Detail == _written.Detail
            && Distance(record.Size, _written.Size) < 0.01 && record.Source == _written.Source && record.Name == _written.Name)
            return false;
        _written = record;
        _lastWritten = now;
        return true;
    }

    private static double Length(double[]? a) => a == null ? 0 : Math.Sqrt(Sum(a, null));
    private static double Distance(double[]? a, double[]? b) => a == null || b == null ? (a == b ? 0 : double.PositiveInfinity) : Math.Sqrt(Sum(a, b));
    private static double Sum(double[] a, double[]? b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) { var d = a[i] - (b == null ? 0 : b[i]); sum += d * d; }
        return sum;
    }
}
