using System.Text.Json;
using CometWorks.EntityViewer.Magnetar.Replay;
using CometWorks.EntityViewer.Replay;

internal static class GeometryChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static ReplayBlock Block(int x, double integrity = 1) => new()
    {
        Cell = [x, 0, 0], Min = [x, 0, 0], Max = [x, 0, 0], Definition = "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock",
        Translation = [x * 2.5, 0, 0], Rotation = [0, 0, 0, 1], Color = [0, -1, 0], Integrity = integrity, Build = 1
    };
    private static ReplayRecord Sample(long at) => new() { At = at, Entity = "grid", Kind = "sample", GeometryReady = true, GridSize = 2.5, Origin = [0, 0, 0] };
    private static string[] Pages(ReplayRecord sample, IReadOnlyDictionary<string, byte[]> parts) =>
        JsonSerializer.Deserialize<ReplayLayoutPage>(ReplayChunkStore.Expand(parts[sample.LayoutId]), Json)!.Pages;
    private static ReplayRecord Change(string kind, ReplayBlock? block = null) => new() { Kind = kind, Entity = "grid", Block = block, Count = 128 };
    public static async Task Run(string root, long now)
    {
        var builder = new ReplayLayoutBuilder();
        builder.Apply(Change("layout-reset"));
        builder.Apply(Change("layout-block", Block(0))); builder.Apply(Change("layout-block", Block(1)));
        var incomplete = Sample(now);
        builder.Attach(incomplete);
        Check(incomplete.LayoutId == "", "A partially captured keyframe must never appear complete");
        builder.Apply(Change("layout-ready"));
        var before = Sample(now); var beforeParts = builder.Attach(before);
        Check(ReferenceEquals(beforeParts, builder.Attach(Sample(now + 1))), "Unchanged layouts reuse immutable dependency sets");
        Check(Pages(before, beforeParts).Length == 2, "Capture must partition blocks into stable pages");
        builder.Apply(Change("layout-remove", Block(0)));
        var after = Sample(now + 1); var afterParts = builder.Attach(after);
        Check(!ReferenceEquals(beforeParts, afterParts) && beforeParts.Count == 3, "Layout mutation replaces dependencies without modifying older snapshots");
        Check(Pages(after, afterParts).Length == 1 && Pages(before, beforeParts).Intersect(Pages(after, afterParts)).Count() == 1, "Destruction must preserve unaffected pages and remove the destroyed block");
        builder.Apply(Change("layout-block", Block(0)));
        var restored = Sample(now + 2); var restoredParts = builder.Attach(restored);
        Check(before.LayoutId == restored.LayoutId, "Rebuilding identical geometry must reuse content hashes");
        builder.Apply(Change("layout-block", Block(0, .5)));
        var damaged = Sample(now + 3); var damagedParts = builder.Attach(damaged);
        Check(Pages(before, beforeParts).Intersect(Pages(damaged, damagedParts)).Count() == 1, "Integrity changes must replace only the affected page");
        builder.Clear();
        builder.Apply(Change("layout-ready"));
        var gap = Sample(now); builder.Attach(gap);
        Check(gap.LayoutId == "", "Lost capture state requires a fresh complete baseline");
        Check(Pages(before, beforeParts).Length == 2, "Clearing capture cannot invalidate already published dependencies");

        builder.Apply(new ReplayRecord { Kind = "layout-reset", Entity = "grid", Count = 50000 });
        for (var i = 0; i < 50000; i++) builder.Apply(Change("layout-block", Block(i)));
        builder.Apply(Change("layout-ready"));
        var large = Sample(now); var largeParts = builder.Attach(large);
        Check(large.LayoutId.Length > 0 && Pages(large, largeParts).Sum(id =>
            JsonSerializer.Deserialize<ReplayBlock[]>(ReplayChunkStore.Expand(largeParts[id]), Json)!.Length) == 50000,
            "A 50,000-block grid must retain its complete layout");
        Check(largeParts.Sum(part => part.Value.Length) < ReplayChunkStore.MaxTransferBytes,
            "The 50,000-block fixture must fit the existing transfer budget");
        builder.Apply(Change("layout-block", Block(50000)));
        var oversized = Sample(now); builder.Attach(oversized);
        Check(oversized.LayoutId == "", "A 50,001-block grid must fall back rather than claim a complete layout");
        builder.Clear();

        var path = Path.Combine(root, "geometry");
        var store = new ReplayChunkStore(path, 1024 * 1024);
        ReplayChunk Chunk(long at) => new() { Id = ReplayChunkStore.NewId(at, 0), Data = ReplayChunkStore.Compress([1, 2, 3]), Geometry = new(beforeParts) };
        var oldest = Chunk(now - ReplayChunkStore.RetentionMs + 1000); var latest = Chunk(now);
        store.Put(oldest, now); store.Put(latest, now);
        Check(Directory.GetFiles(path, "*.blocks").Length == 3, "Identical geometry must be stored once across timeline segments");
        store = new ReplayChunkStore(path, 1024 * 1024);
        store.Prune(now + 2000);
        var read = store.ReadAndAcknowledge(new(), now + 2000);
        Check(read.Chunks.Count == 1 && read.Chunks[0].Geometry.Count == 3, "Expiring the original keyframe must preserve pages needed by retained samples after restart");
        store.ReadAndAcknowledge(new() { Acknowledge = [latest.Id] }, now + 2000);
        Check(Directory.GetFiles(path, "*.blocks").Length == 0, "Unreferenced block pages must be deleted with their final segment");
        var invalid = Chunk(now); invalid.Geometry = new() { [new string('a', 64)] = [1, 2, 3] };
        try { store.Put(invalid, now); throw new Exception("Corrupt geometry was accepted"); } catch (InvalidDataException) { }

        var writer = new ReplayWriter(Path.Combine(root, "geometry-writer"), Guid.NewGuid().ToString("N"), "world");
        foreach (var record in new[] { Change("layout-reset"), Change("layout-block", Block(0)), Change("layout-block", Block(1)), Change("layout-ready"), Sample(now), Change("layout-remove", Block(0)), Sample(now + 1) })
        { if (record.At == 0) record.At = now; writer.Add(record); }
        writer.Dispose(); await writer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var chunk = writer.Read(new()).Chunks.Single();
        var batch = JsonSerializer.Deserialize<ReplayBatch>(ReplayChunkStore.Expand(chunk.Data), Json)!;
        Check(batch.Records.Count == 2 && Pages(batch.Records[0], chunk.Geometry).Length == 2 && Pages(batch.Records[1], chunk.Geometry).Length == 1,
            "Real writer must materialize historical layouts and omit internal capture messages");
        Check(chunk.Geometry.Count == 4, "Writer must send each required page only once per segment");
        Console.WriteLine("PASS: 50,000-block boundary, geometry capture completeness, removal/rebuild, integrity deltas, immutable pages, deduplication, retention dependency survival, corruption rejection, writer materialization.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
