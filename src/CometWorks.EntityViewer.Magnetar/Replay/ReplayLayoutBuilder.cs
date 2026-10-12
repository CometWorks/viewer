using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CometWorks.EntityViewer.Replay;
using Newtonsoft.Json;

namespace CometWorks.EntityViewer.Magnetar.Replay;

// Worker-thread state only. Pages are stable cell-hash buckets; one changed block rewrites one bucket.
internal sealed class ReplayLayoutBuilder
{
    private readonly Dictionary<string, Grid> _grids = new Dictionary<string, Grid>();
    private sealed class Grid
    {
        public readonly Dictionary<int, Dictionary<string, ReplayBlock>> Pages = new Dictionary<int, Dictionary<string, ReplayBlock>>();
        public readonly Dictionary<int, KeyValuePair<string, byte[]>> Encoded = new Dictionary<int, KeyValuePair<string, byte[]>>();
        public readonly Dictionary<string, byte[]> Templates = new Dictionary<string, byte[]>();
        public bool Ready;
        public int Count;
        public int Buckets = 1;
        public KeyValuePair<string, byte[]> Manifest;
        public string[] RequiredTemplates = Array.Empty<string>();
    }
    private int _blocks;
    public void Clear() { _grids.Clear(); _blocks = 0; }
    public bool Apply(ReplayRecord record)
    {
        if (record.Kind == "layout-reset" || record.Kind == "removed")
        {
            if (_grids.TryGetValue(record.Entity, out var old)) _blocks -= old.Count;
            _grids.Remove(record.Entity);
            if (record.Kind == "layout-reset" && _grids.Count < 8192)
            {
                var fresh = new Grid();
                while (fresh.Buckets < 256 && fresh.Buckets * 64 < record.Count) fresh.Buckets *= 2;
                _grids[record.Entity] = fresh;
            }
            return record.Kind == "layout-reset";
        }
        if (!record.Kind.StartsWith("layout-", StringComparison.Ordinal)) return false;
        if (!_grids.TryGetValue(record.Entity, out var grid)) return true;
        if (record.Kind == "layout-ready") { grid.Ready = true; return true; }
        var block = record.Block;
        if (block == null) return true;
        var cell = block.Cell;
        var key = string.Join(",", cell);
        if (grid.Count >= grid.Buckets * 128 && grid.Buckets < 256)
        {
            var blocks = grid.Pages.Values.SelectMany(p => p.Values).ToArray();
            grid.Buckets *= 2; grid.Pages.Clear(); grid.Encoded.Clear(); grid.Manifest = default;
            foreach (var existing in blocks)
            {
                var index = Bucket(existing.Cell, grid.Buckets);
                if (!grid.Pages.TryGetValue(index, out var entries)) grid.Pages[index] = entries = new Dictionary<string, ReplayBlock>();
                entries[string.Join(",", existing.Cell)] = existing;
            }
        }
        var bucket = Bucket(cell, grid.Buckets);
        if (!grid.Pages.TryGetValue(bucket, out var page)) grid.Pages[bucket] = page = new Dictionary<string, ReplayBlock>();
        if (record.Kind == "layout-remove")
        {
            if (page.Remove(key)) { grid.Count--; _blocks--; grid.Encoded.Remove(bucket); grid.Manifest = default; }
        }
        else
        {
            if (!page.ContainsKey(key))
            {
                if (grid.Count >= ReplayLayoutPage.MaxBlocksPerGrid || _blocks >= 262144) { _blocks -= grid.Count; _grids.Remove(record.Entity); return true; }
                grid.Count++; _blocks++;
            }
            // Capture objects are immutable after enqueue; serialize only dirty pages.
            if (block.Parts != null)
            {
                var data = ReplayChunkStore.Compress(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(block.Parts, ReplayWriter.Json)));
                var id = ReplayChunkStore.GeometryId(data);
                block = block.StorageCopy(); block.PartsId = id; block.Parts = null;
                grid.Templates[id] = data;
            }
            page[key] = block; grid.Encoded.Remove(bucket); grid.Manifest = default;
        }
        return true;
    }

    private static int Bucket(int[] cell, int buckets) => unchecked((cell[0] * 73856093 ^ cell[1] * 19349663 ^ cell[2] * 83492791) & (buckets - 1));

    public Dictionary<string, byte[]> Attach(ReplayRecord record)
    {
        var result = new Dictionary<string, byte[]>();
        if (record.Kind != "sample" || !record.GeometryReady || !_grids.TryGetValue(record.Entity, out var grid) || !grid.Ready) return result;
        foreach (var pair in grid.Pages.OrderBy(p => p.Key))
        {
            if (pair.Value.Count == 0) continue;
            if (!grid.Encoded.TryGetValue(pair.Key, out var encoded))
            {
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(pair.Value.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value), ReplayWriter.Json));
                if (bytes.Length > ReplayChunkStore.MaxGeometryBytes) return new Dictionary<string, byte[]>();
                var data = ReplayChunkStore.Compress(bytes);
                encoded = new KeyValuePair<string, byte[]>(ReplayChunkStore.GeometryId(data), data);
                grid.Encoded[pair.Key] = encoded;
            }
            result[encoded.Key] = encoded.Value;
        }
        if (grid.Manifest.Value == null)
        {
            var bytes = ReplayChunkStore.Compress(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new ReplayLayoutPage { Pages = result.Keys.ToArray() }, ReplayWriter.Json)));
            grid.Manifest = new KeyValuePair<string, byte[]>(ReplayChunkStore.GeometryId(bytes), bytes);
            var needed = new HashSet<string>(grid.Pages.Values.SelectMany(p => p.Values).Select(b => b.PartsId).Where(id => id.Length > 0));
            grid.RequiredTemplates = needed.ToArray();
            foreach (var id in grid.Templates.Keys.ToArray()) if (!needed.Contains(id)) grid.Templates.Remove(id);
        }
        foreach (var id in grid.RequiredTemplates) result[id] = grid.Templates[id];
        record.LayoutId = grid.Manifest.Key;
        result[grid.Manifest.Key] = grid.Manifest.Value;
        return result;
    }
}
