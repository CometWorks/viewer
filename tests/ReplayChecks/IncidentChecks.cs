using CometWorks.EntityViewer.Replay;
using CometWorks.EntityViewer.Magnetar.Replay;

internal static class IncidentChecks
{
    public static void Run()
    {
        var output = new List<ReplayRecord>();
        var tracker = new ReplayIncidentTracker(output.Add);
        var settings = new ReplayRecordingSettings();
        const long epoch = 1700000000000;
        ReplayRecord Trigger(double time, double x = 0, double y = 0, string kind = "damage")
        {
            var record = new ReplayRecord { At = epoch + (long)time, Elapsed = time, Kind = kind, Entity = "grid", Position = [x, y, 0] };
            tracker.Observe(record, time, settings); return record;
        }
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var first = Trigger(0);
        Check(first.IncidentId.Length == 32, "Trigger must be tagged before writing");
        Check(tracker.HighUntil(500, 0, 0, 1) == 60000 && tracker.HighUntil(500.01, 0, 0, 1) == 0, "Scalar context includes the exact boundary only");
        Check(tracker.HighUntil(double.NaN, 0, 0, 1) == 0 && tracker.HighUntil(0, double.PositiveInfinity, 0, 1) == 0
            && tracker.HighUntil([0, 0], 1) == 0, "Invalid context positions remain excluded");
        var initial = output.Single().Incident!;
        Trigger(59000, kind: "sample");
        Trigger(59999, kind: "movement");
        Check(tracker.HighUntil([0, 0, 0], 59999) == 60000, "Observations cannot prolong incidents");
        tracker.Advance(60000, epoch + 60000);
        Check(tracker.Count == 0 && output.Last().Incident!.Until == epoch + 60000 && output.Last().Incident!.Reason == "quiet", "Exact one-minute quiet expiry");
        first = Trigger(100000);
        Trigger(150000);
        Check(initial.Seed.SequenceEqual(new double[] { 0, 0, 0 }) && initial.Until == epoch + 60000, "Later activity cannot mutate published incident snapshots");
        tracker.Advance(200000, epoch + 200000, true);
        Check(tracker.Count == 1 && output.Last().Incident!.Until == epoch + 210000, "Activity extends quiet deadline");
        settings.MaxIncidentMinutes = 60; // Renewal cannot change the running incident's captured cap.
        for (var t = 200000; t < 1000000; t += 50000) Check(Trigger(t).IncidentId == first.IncidentId, "Activity remains in incident until cap");
        var next = Trigger(1000000);
        Check(next.IncidentId != first.IncidentId && output.Any(r => r.Incident?.Id == first.IncidentId && r.Incident.Reason == "duration" && r.Incident.Until == epoch + 1000000), "Default 15-minute deadline rolls over despite settings renewal");
        tracker.Close(1000001, epoch + 1000001);
        output.Clear();
        settings = new ReplayRecordingSettings { MaxIncidentMinutes = 60 };
        first = Trigger(0);
        for (var t = 50000; t < 3600000; t += 50000) Trigger(t);
        next = Trigger(3600000);
        Check(next.IncidentId != first.IncidentId && output.Any(r => r.Incident?.Id == first.IncidentId && r.Incident.Until == epoch + 3600000 && r.Incident.Ended), "One-hour hard deadline");
        tracker.Close(3600001, epoch + 3600001); output.Clear();
        first = Trigger(0);
        for (var x = 900; x <= 9900; x += 900) Check(Trigger(x, x).IncidentId == first.IncidentId, "Connected battle must expand beyond 500m");
        Check(output.Last().Incident!.Radius == 10000 && tracker.HighUntil([9500, 0, 0], 10000) > 10000, "Battle envelope reaches bounded 10km");
        Check(Trigger(10000, 10800).IncidentId != first.IncidentId, "Same entity cannot drag seed beyond cap");
        Check(Trigger(10001, 5000, 5000).IncidentId != first.IncidentId, "Empty interior of aggregate envelope must not connect unrelated activity");
        tracker.Close(11000, epoch + 11000); output.Clear();
        first = Trigger(0);
        var separate = Trigger(1000, 1800);
        Check(first.IncidentId != separate.IncidentId, "Distant triggers start separate incidents");
        var bridge = Trigger(2000, 900);
        Check(tracker.Count == 1 && bridge.IncidentId == first.IncidentId, "Bridge merges connected incidents into earliest");
        Check(output.Last().Incident!.MergedIds.Contains(separate.IncidentId) && output.Last().Incident!.Deadline == epoch + 3600000, "Merge preserves identity and original deadline");
        tracker.Advance(7000, epoch + 7000, true);
        Check(output.Last().At == epoch + 7000 && output.Last().Incident!.StartedAt == epoch, "Mid-incident checkpoint contains original boundary");
        var schedule = new ReplaySchedule();
        schedule.ContextUntil(62000, 10000); schedule.ContextUntil(62000, 61999);
        Check(!schedule.IsHigh(62000), "Nearby scheduling never adds another quiet tail");
        tracker.Close(8000, epoch + 8000); output.Clear();
        settings.MaxContextRadius = 500;
        first = Trigger(0); Check(Trigger(1, 499).IncidentId == first.IncidentId, "Minimum radius permits activity within the context");
        Check(Trigger(2, 501).IncidentId != first.IncidentId, "Minimum radius stays bounded");
        tracker.Close(3, epoch + 3); output.Clear();
        settings = new ReplayRecordingSettings();
        first = Trigger(0); Trigger(1, 250);
        Check(Trigger(2, 1250).IncidentId == first.IncidentId, "Every admitted anchor can connect the next member sphere");
        Trigger(59000, 1250);
        Check(output.Last().Incident!.Until == epoch + 119000, "Stationary activity immediately records renewed deadline");
        tracker.Close(59001, epoch + 59001); output.Clear();
        settings = new ReplayRecordingSettings();
        for (var n = 0; n < 33; n++) Trigger(n, n * 20000);
        Check(tracker.Count == 32 && output.Last().Kind == "gap", "Incident overload is bounded and reported");
        tracker.Close(34, epoch + 34); output.Clear();
        for (var n = 0; n <= 256; n++) Trigger(n, n);
        Check(output.Last().Incident!.Incomplete, "Anchor capacity is explicit, preserving established connectivity");
        Console.WriteLine("Incident timing, merging, spatial growth, renewal and capacity checks passed.");
    }
}
