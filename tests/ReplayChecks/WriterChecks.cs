using CometWorks.EntityViewer.Magnetar.Replay;
using CometWorks.EntityViewer.Quasar.Replay;
using CometWorks.EntityViewer.Replay;

internal static class WriterChecks
{
    public static async Task Run(string root, long now)
    {
        var writer = new ReplayWriter(Path.Combine(root, "writer-boundaries"), Guid.NewGuid().ToString("N"), "world");
        writer.Add(new ReplayRecord { At = now, Kind = "layout-reset", Entity = "grid" });
        writer.Add(new ReplayRecord { At = now, Kind = "layout-block", Entity = "grid", Block = GeometryChecks.Block(0) });
        writer.Add(new ReplayRecord { At = now, Kind = "layout-ready", Entity = "grid" });
        var terrain = Terrain(new Random(1));
        for (var i = 0; i < 260; i++)
        {
            writer.Add(Sample(now - 1000));
            if (i == 0 || i == 129) writer.Add(new ReplayRecord { At = now - 1000, Kind = "voxel-sample", Entity = "voxel:planet/0", Terrain = terrain, Value = now - 1000 });
        }
        // Delayed events force a time boundary in each direction, even within a reused batch.
        writer.Add(Sample(now - 90000)); writer.Add(Sample(now - 90001)); writer.Add(Sample(now - 1000));
        writer.Dispose(); await writer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var chunks = Drain(writer);
        var batches = chunks.Select(ReplayHistoryService.Decode).ToArray();
        Check(batches.Length >= 5 && batches.Sum(b => b.Records.Count) == 265, "Count and timestamp flushes preserve every observation");
        Check(batches.All(b => b.Records.Count <= 128 && b.Records.Max(r => r.At) - b.Records.Min(r => r.At) <= 60000), "Segment counters reset after each flush");
        Check(batches.SelectMany(b => b.Records).Where(r => r.Kind == "sample").Select(r => r.LayoutId).Distinct().Count() == 1, "Unchanged layouts remain identical across segments");
        for (var i = 0; i < batches.Length; i++)
        {
            var batch = batches[i];
            Check(batch.Geometry.Values.Single().Single().Cell[0] == 0, "Every segment includes the complete cached layout");
            var hasTerrain = batch.Records.Any(r => r.Kind == "voxel-sample");
            Check(chunks[i].Geometry.Count == (hasTerrain ? 3 : 2), "Terrain cannot contaminate cached layout dependencies or subsequent segments");
        }

        var bounded = new ReplayWriter(Path.Combine(root, "writer-geometry-budget"), Guid.NewGuid().ToString("N"), "world");
        var random = new Random(42);
        for (var i = 0; i < 40; i++) bounded.Add(new ReplayRecord { At = now, Kind = "voxel-sample", Entity = "voxel:planet/" + i, Terrain = Terrain(random), Value = now });
        bounded.Dispose(); await bounded.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        chunks = Drain(bounded);
        Check(chunks.Count == 2 && chunks.All(c => c.Geometry.Sum(p => p.Value.Length) <= 2 * 1024 * 1024), "Geometry bytes trigger and reset the two-MiB segment budget");
        Check(chunks.Select(ReplayHistoryService.Decode).Sum(b => b.Records.Count) == 40, "Geometry budget flushes keep all terrain dependencies");
        Console.WriteLine("PASS: cached layout/terrain ownership across count, time and geometry segment boundaries.");
    }

    private static ReplayRecord Sample(long at) => new() { At = at, Entity = "grid", GeometryReady = true, GridSize = 2.5, Origin = [0, 0, 0] };
    private static ReplayVoxelPage Terrain(Random random)
    {
        var content = new byte[32 * 32 * 32]; var materials = new byte[content.Length];
        random.NextBytes(content); random.NextBytes(materials);
        return new ReplayVoxelPage { Body = "planet", Origin = [0, 0, 0], Rotation = [0, 0, 0, 1], Min = [0, 0, 0], Size = [32, 32, 32], Content = content, Materials = materials };
    }
    private static List<ReplayChunk> Drain(ReplayWriter writer)
    {
        var result = new List<ReplayChunk>(); var request = new ReplayReadRequest();
        while (true)
        {
            var page = writer.Read(request);
            Check(page.Error.Length == 0 && page.LostChunks == 0, "Writer cannot hide storage errors or dropped coverage");
            if (page.Chunks.Count == 0) return result;
            result.AddRange(page.Chunks); request.Acknowledge = page.Chunks.Select(c => c.Id).ToList();
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
