#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace CometWorks.EntityViewer.Replay;

// Simulation-thread owned. Only meaningful activity renews a deadline, never observations.
public sealed class ReplayIncidentTracker
{
    public const int QuietMs = 60000;
    private const int MaxActive = 32, MaxAnchors = 256;
    private readonly List<Active> _active = new List<Active>();
    private readonly Action<ReplayRecord> _write;
    public int Count => _active.Count;
    public ReplayIncidentTracker(Action<ReplayRecord> write) { _write = write; }
    private sealed class Active
    {
        public string Id = Guid.NewGuid().ToString("N");
        public long At;
        public double Start, Last, Deadline;
        public double[] Seed = Array.Empty<double>();
        public double Radius = 500;
        public int Cap;
        public bool Incomplete;
        public readonly List<double[]> Anchors = new List<double[]>();
        public readonly List<string> Merged = new List<string>();
        public double Until => Math.Min(Last + QuietMs, Deadline);
        public long Wall(double time) => At + (long)(time - Start);
    }
    public static bool IsActivity(ReplayRecord record) => record.Kind == "damage" || record.Kind == "physics"
        || record.Kind == "death" || record.Kind == "explosion" || record.Kind == "split" || record.Kind == "merge"
        || record.Kind == "block-added" || record.Kind == "block-removed" || record.Kind == "action" && (record.Actions.GetValueOrDefault() & 6) != 0;
    private static double Distance(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
    private static bool Valid(double[]? p) => p != null && p.Length == 3 && p.All(v => !double.IsNaN(v) && !double.IsInfinity(v));

    public void Observe(ReplayRecord record, double now, ReplayRecordingSettings settings)
    {
        Advance(now, record.At);
        if (!IsActivity(record) || !Valid(record.Position)) return;
        var p = record.Position!;
        // Overlapping 500 m member contexts connect, as with partition member-to-member gaps.
        // The aggregate envelope is deliberately NOT used for connectivity.
        var matches = _active.Where(a => Distance(a.Seed, p) <= a.Cap
            && a.Anchors.Any(anchor => Distance(anchor, p) <= 1000)).OrderBy(a => a.Start).ToArray();
        var incident = matches.FirstOrDefault();
        var changed = false;
        if (incident == null)
        {
            if (_active.Count >= MaxActive)
            {
                _write(new ReplayRecord { At = record.At, Elapsed = now, Kind = "gap", Detail = "Active incident limit; activity retained without incident context" });
                return;
            }
            incident = new Active { At = record.At, Start = now, Last = now, Deadline = now + settings.MaxIncidentMinutes * 60000,
                Seed = (double[])p.Clone(), Cap = settings.MaxContextRadius };
            incident.Anchors.Add(incident.Seed); _active.Add(incident); changed = true;
        }
        foreach (var other in matches.Skip(1))
        {
            // Earliest incident owns the union. Never move its seed or reset its hard deadline.
            if (incident.Merged.Count + other.Merged.Count + 1 > 31 || incident.Anchors.Count + other.Anchors.Count > MaxAnchors)
            {
                incident.Incomplete = other.Incomplete = true; changed = true;
                Write(other, now, record.At); continue;
            }
            if (Distance(incident.Seed, other.Seed) + other.Radius > incident.Cap) continue;
            incident.Deadline = Math.Min(incident.Deadline, other.Deadline);
            incident.Radius = Math.Max(incident.Radius, Distance(incident.Seed, other.Seed) + other.Radius);
            incident.Anchors.AddRange(other.Anchors); incident.Merged.Add(other.Id); incident.Merged.AddRange(other.Merged);
            incident.Incomplete |= other.Incomplete;
            Write(other, now, record.At, "merged", incident.Id); _active.Remove(other); changed = true;
        }
        if (incident.Anchors.All(anchor => Distance(anchor, p) > 0))
        {
            if (incident.Anchors.Count < MaxAnchors) { incident.Anchors.Add((double[])p.Clone()); changed = true; }
            else { incident.Incomplete = true; changed = true; }
        }
        var radius = Math.Min(incident.Cap, Math.Max(incident.Radius, Distance(incident.Seed, p) + 500));
        changed |= radius != incident.Radius; incident.Radius = radius;
        changed |= incident.Last != now;
        incident.Last = now; record.IncidentId = incident.Id;
        if (changed) Write(incident, now, record.At);
    }
    public void Advance(double now, long at, bool checkpoint = false)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var a = _active[i];
            if (now >= a.Until) { Write(a, now, at, a.Deadline <= a.Last + QuietMs ? "duration" : "quiet"); _active.RemoveAt(i); }
            else if (checkpoint) Write(a, now, at);
        }
    }
    public double HighUntil(double[]? position, double now)
    {
        if (!Valid(position)) return 0;
        double until = 0;
        foreach (var a in _active) if (now < a.Until && Distance(a.Seed, position!) <= a.Radius) until = Math.Max(until, a.Until);
        return until;
    }
    public void Close(double now, long at)
    {
        Advance(now, at);
        foreach (var a in _active) Write(a, now, at, "stopped");
        _active.Clear();
    }
    private void Write(Active a, double now, long at, string reason = "", string mergedInto = "") => _write(new ReplayRecord
    {
        At = at, Elapsed = now, Kind = "incident", IncidentId = a.Id,
        Incident = new ReplayIncident { Id = a.Id, StartedAt = a.At, LastActivityAt = a.Wall(a.Last),
            Until = a.Wall(reason == "stopped" || reason == "merged" ? Math.Min(now, a.Until) : a.Until), Deadline = a.Wall(a.Deadline),
            Seed = (double[])a.Seed.Clone(), Radius = a.Radius, MaxRadius = a.Cap, Ended = reason.Length > 0, Reason = reason,
            MergedInto = mergedInto, MergedIds = a.Merged.ToArray(), Incomplete = a.Incomplete }
    });
}
