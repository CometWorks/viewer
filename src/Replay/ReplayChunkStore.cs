#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace CometWorks.EntityViewer.Replay;

// Immutable gzip segments. All access is on worker/request threads, never simulation callbacks.
public sealed class ReplayChunkStore
{
    public const int MaxTransferBytes = 4 * 1024 * 1024;
    public const int MaxGeometryBytes = 256 * 1024;
    public const int MaxCompressedBytes = 512 * 1024;
    public const int MaxExpandedBytes = 4 * 1024 * 1024;
    public const long RetentionMs = 24L * 60 * 60 * 1000;
    private readonly string _directory;
    private readonly long _quota;
    private readonly object _gate = new object();
    private readonly SortedDictionary<string, FileInfo> _files = new SortedDictionary<string, FileInfo>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _references = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _parts = new Dictionary<string, string[]>(StringComparer.Ordinal);
    private long _bytes;
    public long LostChunks { get; private set; }

    public ReplayChunkStore(string directory, long quota)
    {
        _directory = directory;
        _quota = quota;
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.GetFiles(directory, "*.tmp")) File.Delete(path);
        foreach (var file in new DirectoryInfo(directory).GetFiles("*.gz"))
            if (ValidId(Path.GetFileNameWithoutExtension(file.Name))) { _files.Add(file.Name, file); _bytes += Cost(file); }
        // A segment is the commit marker. Orphan sidecars/pages from interrupted writes are safe to remove.
        foreach (var file in _files.Values)
        {
            var refs = File.Exists(file.FullName + ".refs") ? File.ReadAllLines(file.FullName + ".refs") : Array.Empty<string>();
            if (refs.Length > 32768 || refs.Any(id => !ValidGeometryId(id))) throw new InvalidDataException("Invalid geometry index.");
            _parts[file.Name] = refs;
            foreach (var id in refs) _references[id] = _references.TryGetValue(id, out var count) ? count + 1 : 1;
            if (refs.Length > 0) _bytes += Cost(new FileInfo(file.FullName + ".refs"));
        }
        foreach (var file in new DirectoryInfo(directory).GetFiles("*.blocks"))
            if (_references.ContainsKey(Path.GetFileNameWithoutExtension(file.Name))) _bytes += Cost(file); else file.Delete();
        foreach (var file in new DirectoryInfo(directory).GetFiles("*.refs"))
            if (!_files.ContainsKey(file.Name.Substring(0, file.Name.Length - 5))) file.Delete();
        var lossPath = Path.Combine(directory, "loss-count");
        if (File.Exists(lossPath) && long.TryParse(File.ReadAllText(lossPath), out var lost)) LostChunks = lost;
    }

    public static string NewId(long at, int events) => at.ToString("D13") + "-" + events + "-" + Guid.NewGuid().ToString("N");
    public static bool ValidId(string id) => id != null && id.Length <= 70 && id.Length >= 48
        && id.All(c => c == '-' || c >= '0' && c <= '9' || c >= 'a' && c <= 'f')
        && id.Split('-').Length == 3 && id.Split('-')[2].Length == 32
        && int.TryParse(id.Split('-')[1], out _) && long.TryParse(id.Substring(0, 13), out _);
    public static long Timestamp(string id) => long.Parse(id.Substring(0, 13));

    public static bool ValidGeometryId(string id) => id != null && id.Length == 64 && id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    public static string GeometryId(byte[] bytes)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    public void Put(ReplayChunk chunk, long now)
    {
        if (!ValidId(chunk.Id) || chunk.Data.Length > MaxCompressedBytes || chunk.Data.Length == 0)
            throw new InvalidDataException("Invalid replay segment.");
        if (Timestamp(chunk.Id) < now - RetentionMs || Timestamp(chunk.Id) > now + 60000)
            throw new InvalidDataException("Replay segment outside retention window.");
        if (chunk.Geometry == null || chunk.Geometry.Count > 32768 || chunk.Geometry.Sum(p => (long)p.Value.Length) + chunk.Data.Length > MaxTransferBytes
            || chunk.Geometry.Any(p => !ValidGeometryId(p.Key) || p.Value.Length == 0 || p.Value.Length > MaxGeometryBytes || GeometryId(p.Value) != p.Key))
            throw new InvalidDataException("Invalid replay geometry.");
        lock (_gate)
        {
            var path = Path.Combine(_directory, chunk.Id + ".gz");
            if (File.Exists(path)) return;
            var ids = chunk.Geometry.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var temporary = path + ".tmp";
            try
            {
                foreach (var pair in chunk.Geometry)
                {
                    var partPath = Path.Combine(_directory, pair.Key + ".blocks");
                    if (!File.Exists(partPath)) WriteAtomic(partPath, pair.Value);
                }
                if (ids.Length > 0) WriteAtomic(path + ".refs", System.Text.Encoding.UTF8.GetBytes(string.Join("\n", ids)));
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(chunk.Data, 0, chunk.Data.Length);
                    stream.Flush(true);
                }
                File.Move(temporary, path);
                var file = new FileInfo(path); _files.Add(file.Name, file); _bytes += Cost(file);
                _parts[file.Name] = ids;
                if (ids.Length > 0) _bytes += Cost(new FileInfo(path + ".refs"));
                foreach (var id in ids)
                {
                    if (_references.TryGetValue(id, out var count)) _references[id] = count + 1;
                    else { _references[id] = 1; _bytes += Cost(new FileInfo(Path.Combine(_directory, id + ".blocks"))); }
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (!File.Exists(path))
                {
                    if (File.Exists(path + ".refs")) File.Delete(path + ".refs");
                    foreach (var id in ids)
                        if (!_references.ContainsKey(id)) File.Delete(Path.Combine(_directory, id + ".blocks"));
                }
            }
            PruneLocked(now);
        }
    }

    public ReplayPage ReadAndAcknowledge(ReplayReadRequest request, long now)
    {
        if (request.Acknowledge == null || request.Acknowledge.Count > 8 || request.Acknowledge.Any(id => !ValidId(id)))
            throw new InvalidDataException("Invalid replay acknowledgement.");
        lock (_gate)
        {
            foreach (var id in request.Acknowledge)
                if (_files.TryGetValue(id + ".gz", out var file)) Remove(file);
            PruneLocked(now);
            var page = new ReplayPage { LostChunks = LostChunks };
            var bytes = 0;
            foreach (var file in Files())
            {
                var chunk = Read(file);
                var cost = chunk.Data.Length + chunk.Geometry.Sum(p => p.Value.Length);
                if (page.Chunks.Count == 8 || bytes + cost > MaxTransferBytes) break;
                page.Chunks.Add(chunk);
                bytes += cost;
            }
            return page;
        }
    }

    public List<ReplayChunk> ReadRange(long from, long to, long now)
    {
        lock (_gate)
        {
            PruneLocked(now);
            var files = Files().Where(f => Timestamp(f.Name) >= Math.Max(now - RetentionMs, from - 60000) && Timestamp(f.Name) <= to).ToList();
            if (files.Sum(f => f.Length) > 8 * 1024 * 1024 || files.Count > 512)
                throw new InvalidDataException("Too much replay data. Select a shorter time range.");
            var result = new List<ReplayChunk>();
            var bytes = 0L;
            var geometry = new Dictionary<string, byte[]>();
            foreach (var file in files)
            {
                var previousGeometryBytes = geometry.Sum(p => (long)p.Value.Length);
                var chunk = Read(file, geometry);
                bytes += chunk.Data.Length + geometry.Sum(p => (long)p.Value.Length) - previousGeometryBytes;
                if (bytes > 32 * 1024 * 1024) throw new InvalidDataException("Too much replay geometry. Select a shorter time range.");
                result.Add(chunk);
            }
            return result;
        }
    }

    public string[] List(long now)
    {
        lock (_gate) { PruneLocked(now); return Files().Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToArray(); }
    }

    public void Prune(long now) { lock (_gate) PruneLocked(now); }

    private FileInfo[] Files() => _files.Values.ToArray();
    private static long Cost(FileInfo file) => Math.Max(4096, ((file.Length + 4095) / 4096) * 4096);

    private void PruneLocked(long now)
    {
        var previousLoss = LostChunks;
        foreach (var file in Files())
        {
            if (Timestamp(file.Name) >= now - RetentionMs && _bytes <= _quota) break;
            Remove(file);
            if (Timestamp(file.Name) >= now - RetentionMs) LostChunks++;
        }
        if (LostChunks != previousLoss) File.WriteAllText(Path.Combine(_directory, "loss-count"), LostChunks.ToString());
    }

    private ReplayChunk Read(FileInfo file, Dictionary<string, byte[]>? geometry = null)
    {
        if (file.Length > MaxCompressedBytes) throw new InvalidDataException("Replay segment exceeds read budget.");
        var chunk = new ReplayChunk { Id = Path.GetFileNameWithoutExtension(file.Name), Data = File.ReadAllBytes(file.FullName) };
        foreach (var id in _parts[file.Name])
        {
            if (geometry != null && geometry.TryGetValue(id, out var cached)) { chunk.Geometry[id] = cached; continue; }
            var path = Path.Combine(_directory, id + ".blocks");
            // Missing dependencies are represented as incomplete geometry by the reader.
            if (File.Exists(path) && new FileInfo(path).Length > MaxGeometryBytes) throw new InvalidDataException("Replay geometry page exceeds read budget.");
            if (File.Exists(path)) { chunk.Geometry[id] = File.ReadAllBytes(path); if (geometry != null) geometry[id] = chunk.Geometry[id]; }
        }
        return chunk;
    }

    private void Remove(FileInfo file)
    {
        var cost = Cost(file); // FileInfo.Delete invalidates cached length on some runtimes.
        file.Delete(); _files.Remove(file.Name); _bytes -= cost;
        var refsPath = file.FullName + ".refs";
        if (File.Exists(refsPath)) { var refsCost = Cost(new FileInfo(refsPath)); File.Delete(refsPath); _bytes -= refsCost; }
        if (_parts.TryGetValue(file.Name, out var ids))
        {
            _parts.Remove(file.Name);
            foreach (var id in ids)
            {
                if (--_references[id] != 0) continue;
                _references.Remove(id);
                var part = new FileInfo(Path.Combine(_directory, id + ".blocks"));
                if (part.Exists) { var partCost = Cost(part); part.Delete(); _bytes -= partCost; }
            }
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static byte[] Compress(byte[] bytes)
    {
        if (bytes.Length > MaxExpandedBytes) throw new InvalidDataException("Replay segment too large.");
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(bytes, 0, bytes.Length);
            var result = output.ToArray();
            if (result.Length > MaxCompressedBytes) throw new InvalidDataException("Compressed replay segment too large.");
            return result;
        }
    }

    public static byte[] Expand(byte[] bytes)
    {
        if (bytes.Length > MaxCompressedBytes) throw new InvalidDataException("Replay segment too large.");
        using (var input = new MemoryStream(bytes))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            var buffer = new byte[8192];
            int count;
            while ((count = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + count > MaxExpandedBytes) throw new InvalidDataException("Expanded replay segment too large.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
    }
}
