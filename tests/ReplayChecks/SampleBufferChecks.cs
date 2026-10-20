using System.Text.Json;
using CometWorks.EntityViewer.Magnetar.Replay;
using CometWorks.EntityViewer.Replay;

internal static class SampleBufferChecks
{
    public static void Run()
    {
        var schedule = new ReplaySchedule();
        var first = Sample(schedule, 0);
        Check(schedule.Observe(first, 0, out _), "First rented sample establishes baseline");
        var frozen = JsonSerializer.Serialize(first);
        var idle = Sample(schedule, 1000, .1);
        Check(!schedule.Observe(idle, 1000, out _), "Small idle movement is suppressed");
        Check(ReferenceEquals(idle, Sample(schedule, 2000, .2)), "Only suppressed samples are reused");
        Check(!schedule.Observe(idle, 2000, out _), "Reused sample still compares against emitted baseline");
        Check(JsonSerializer.Serialize(first) == frozen, "Reusing buffers cannot mutate an emitted record or its vectors");
        var moved = Sample(schedule, 3000, .3);
        Check(schedule.Observe(moved, 3000, out _) && !ReferenceEquals(moved, schedule.RentSample()), "Emission transfers ownership, including when a writer later drops the record");

        var evidence = Sample(schedule, 4000, .3);
        Check(!schedule.Observe(evidence, 4000, out _), "Evidence may originate in a suppressed observation");
        evidence.Kind = "physics";
        schedule.DetachSample();
        frozen = JsonSerializer.Serialize(evidence);
        var next = Sample(schedule, 5000, 100);
        Check(next.Kind == "sample" && JsonSerializer.Serialize(evidence) == frozen, "Explicit evidence transfer cannot be reused or retain physics metadata");

        // Sub-threshold velocity changes at short intervals are individually suppressed,
        // but both accelerations still count. Comparing only to the emitted baseline misses this.
        var acceleration = new ReplaySchedule();
        acceleration.Observe(Sample(acceleration, 0), 0, out _);
        var accelerating = Sample(acceleration, .2, velocity: .06);
        Check(!acceleration.Observe(accelerating, .2, out var warning) && !warning, "First acceleration alone is suppressed");
        accelerating = Sample(acceleration, .4);
        Check(acceleration.Observe(accelerating, .4, out warning) && warning, "Previous suppressed velocity must survive buffer reuse for sustained acceleration detection");

        var color = new ReplaySchedule();
        var colored = Sample(color, 0); colored.Color = [0, 0, 0];
        color.Observe(colored, 0, out _);
        colored = Sample(color, 1000); colored.Color = [.0099, 0, 0];
        Check(!color.Observe(colored, 1000, out _), "Sub-threshold color is suppressed");
        colored.Color[0] = .01;
        Check(color.Observe(colored, 2000, out _), "Exact color threshold is retained");
        Check(color.Observe(Sample(color, 3000), 3000, out _), "Losing an appearance vector is observable");
        Check(!color.Observe(Sample(color, 4000), 4000, out _), "Two missing appearance vectors are unchanged");
        Console.WriteLine("PASS: reusable sample ownership, detached evidence, suppressed acceleration and appearance thresholds.");
    }

    private static ReplayRecord Sample(ReplaySchedule schedule, double now, double x = 0, double velocity = 0)
    {
        var sample = schedule.RentSample(); sample.Elapsed = now;
        sample.Position ??= [0, 0, 0]; sample.Position[0] = x;
        sample.Velocity ??= [0, 0, 0]; sample.Velocity[0] = velocity;
        sample.AngularVelocity ??= [0, 0, 0]; sample.Rotation ??= [0, 0, 0, 1];
        return sample;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
