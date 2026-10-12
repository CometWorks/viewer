using System.Text.Json;
using CometWorks.EntityViewer.Magnetar.Replay;
using CometWorks.EntityViewer.Quasar.Replay;
using CometWorks.EntityViewer.Replay;

internal static class ContextChecks
{
    public static async Task Run(string root, long now)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var voxel = new ReplayVoxelPage { Body = "planet", Origin = [100, 200, 300], Rotation = [0, 0, 0, 1],
            Min = [32, 64, 96], Size = [2, 2, 2], Content = [0, 0, 255, 255, 0, 0, 255, 255], Materials = new byte[8],
            Palette = [new ReplayVoxelMaterial { Index = 0, ColorMetalY = "Textures/terrain.dds" }] };
        var writer = new ReplayWriter(Path.Combine(root, "context"), Guid.NewGuid().ToString("N"), "world");
        writer.Add(new ReplayRecord { At = now, Kind = "voxel-sample", Entity = "voxel:p/0", Terrain = voxel, Value = now });
        writer.Add(new ReplayRecord { At = now + 10000, Kind = "voxel-sample", Entity = "voxel:p/0", Terrain = voxel, Value = now });
        writer.Add(new ReplayRecord { At = now + 11000, Kind = "voxel-invalidated", Entity = "voxel:p/0" });
        writer.Dispose(); await writer.Completion;
        var chunks = writer.Read(new ReplayReadRequest()).Chunks;
        var decoded = chunks.Select(ReplayHistoryService.Decode).ToArray();
        Check(decoded.SelectMany(b => b.Records).Count() == 3, "Terrain observations/checkpoints and invalidations survive writer");
        Check(decoded.SelectMany(b => b.Voxels).Select(p => p.Key).Distinct().Count() == 1, "Repeated terrain checkpoints share one content-addressed page");
        Check(decoded.SelectMany(b => b.Records).All(r => r.Terrain == null), "Capture-only voxel objects never enter timeline records");
        var chunk = chunks[0];
        var terrainId = chunk.Geometry.Keys.Single();
        var missing = new ReplayChunk { Id = chunk.Id, Data = chunk.Data };
        Reject(() => ReplayHistoryService.Decode(missing), "Missing terrain dependencies are rejected");
        void RejectVoxel(Action<ReplayVoxelPage> change)
        {
            var copy = JsonSerializer.Deserialize<ReplayVoxelPage>(JsonSerializer.Serialize(voxel, json), json)!;
            change(copy);
            var data = ReplayChunkStore.Compress(JsonSerializer.SerializeToUtf8Bytes(copy, json));
            var id = ReplayChunkStore.GeometryId(data);
            var batch = JsonSerializer.Deserialize<ReplayBatch>(ReplayChunkStore.Expand(chunk.Data), json)!;
            foreach (var r in batch.Records.Where(r => r.Kind == "voxel-sample")) r.TerrainId = id;
            Reject(() => ReplayHistoryService.Decode(new ReplayChunk { Id = chunk.Id,
                Data = ReplayChunkStore.Compress(JsonSerializer.SerializeToUtf8Bytes(batch, json)), Geometry = new() { [id] = data } }), "Invalid voxel data is rejected");
        }
        RejectVoxel(v => v.Size = [33, 33, 33]);
        RejectVoxel(v => v.Lod = 30);
        RejectVoxel(v => v.Rotation = [0, 0, 0, 0]);
        RejectVoxel(v => v.Palette[0].ColorMetalY = "../secret.dds");

        var parts = new ReplayModelParts { Parts = [new ReplayModelPart { Model = "Models/armor.mwm", Matrix = [1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1] }] };
        var layout = new ReplayLayoutBuilder();
        layout.Apply(new ReplayRecord { Kind = "layout-reset", Entity = "grid" });
        for (var i = 0; i < 2; i++)
        {
            var block = GeometryChecks.Block(i); block.Parts = parts; block.Patterns = [(byte)i, 0, 4, 4];
            layout.Apply(new ReplayRecord { Kind = "layout-block", Entity = "grid", Block = block });
            Check(block.Parts == parts && block.PartsId == "", "Writer must not mutate captured model metadata");
        }
        layout.Apply(new ReplayRecord { Kind = "layout-ready", Entity = "grid" });
        var sample = new ReplayRecord { At = now, Entity = "grid", GeometryReady = true };
        var blobs = layout.Attach(sample);
        var segment = new ReplayChunk { Id = ReplayChunkStore.NewId(now, 0), Geometry = blobs, Data = ReplayChunkStore.Compress(JsonSerializer.SerializeToUtf8Bytes(
            new ReplayBatch { Session = Guid.NewGuid().ToString("N"), Records = [sample] }, json)) };
        var result = ReplayHistoryService.Decode(segment);
        Check(result.ModelParts.Count == 1 && result.Geometry.Values.SelectMany(b => b).All(b => b.PartsId == result.ModelParts.Keys.Single()), "Armor surfaces deduplicate independently of per-block UV patterns");
        segment.Geometry.Remove(result.ModelParts.Keys.Single());
        Reject(() => ReplayHistoryService.Decode(segment), "Missing armor template dependencies are rejected");
        Console.WriteLine("PASS: historical terrain checkpoints, immutable armor templates, deduplication and context trust-boundary validation.");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { return; } throw new Exception(message); }
}
