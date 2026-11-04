using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class ResolvedServerAsset
{
    public string ContentHash { get; internal set; } = string.Empty;

    public string LogicalPath { get; init; } = string.Empty;

    public string RootId { get; init; } = string.Empty;

    public string RootKind { get; init; } = string.Empty;

    internal string ApprovedRoot { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public string ArchiveEntryName { get; init; } = string.Empty;

    public long Size { get; init; }

    public DateTimeOffset? LastModifiedUtc { get; init; }

    public DateTimeOffset? SourceLastModifiedUtc { get; init; }

    public long SourceSize { get; init; }

    public string ContentType { get; init; } = "application/octet-stream";

    public bool IsArchiveEntry => !string.IsNullOrWhiteSpace(ArchiveEntryName);

    public string CacheKeyForUser(string userId) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', "asset-v2", userId, FilePath, ArchiveEntryName, ContentHash,
            Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SourceSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (SourceLastModifiedUtc ?? LastModifiedUtc)?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)))));

    public Stream OpenRead()
    {
        if (Size < 0 || Size > AssetStreamingLimits.MaxAssetBytes)
            throw new InvalidDataException("Asset exceeds the streaming size limit.");
        var file = AssetPathBoundary.OpenRead(FilePath, ApprovedRoot);
        if (!IsArchiveEntry)
        {
            if (file.Length != Size)
            {
                file.Dispose();
                throw new FileNotFoundException("Asset changed after it was authorized.", FilePath);
            }
            return new BoundedFileReadStream(file, Size);
        }

        ZipArchive? archive = null;
        try
        {
            if (SourceSize < 0 || SourceSize > AssetStreamingLimits.MaxArchiveBytes || file.Length != SourceSize)
                throw new FileNotFoundException("Asset archive changed after it was authorized.", FilePath);
            archive = new ZipArchive(file, ZipArchiveMode.Read);
            var entry = archive.GetEntry(ArchiveEntryName);
            if (entry is null)
                throw new FileNotFoundException($"Archive entry '{ArchiveEntryName}' not found.", FilePath);
            if (entry.Length != Size || entry.Length > AssetStreamingLimits.MaxAssetBytes)
                throw new InvalidDataException("Asset archive entry exceeds its authorized size.");
            return new ArchiveReadStream(archive, entry.Open(), entry.Length);
        }
        catch
        {
            archive?.Dispose();
            file.Dispose();
            throw;
        }
    }

    // A source can grow after the initial length check. Expose only the bytes that were
    // fingerprinted and authorized so a concurrent local write cannot bypass the response cap.
    private sealed class BoundedFileReadStream(FileStream file, long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => file.Position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            file.Read(buffer, offset, BoundedCount(count));

        public override int Read(Span<byte> buffer) => file.Read(buffer[..BoundedCount(buffer.Length)]);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            file.ReadAsync(buffer, offset, BoundedCount(count), cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            file.ReadAsync(buffer[..BoundedCount(buffer.Length)], cancellationToken);

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(file.Position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0 || target > length)
                throw new IOException("Attempted to seek outside the authorized asset length.");
            return file.Seek(target, SeekOrigin.Begin);
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) file.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await file.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private int BoundedCount(int requested) =>
            file.Position >= length ? 0 : (int)Math.Min(requested, length - file.Position);
    }

    // Each response owns its archive and reads lazily; slow clients share neither buffers nor cursors.
    private sealed class ArchiveReadStream : Stream
    {
        private readonly ZipArchive _archive;
        private readonly Stream _entry;
        private readonly long _length;
        private long _remaining;

        public ArchiveReadStream(ZipArchive archive, Stream entry, long length)
        {
            _archive = archive;
            _entry = entry;
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => _entry.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var bounded = BoundedCount(count);
            return bounded == 0 ? 0 : Record(_entry.Read(buffer, offset, bounded));
        }
        public override int Read(Span<byte> buffer)
        {
            var bounded = BoundedCount(buffer.Length);
            return bounded == 0 ? 0 : Record(_entry.Read(buffer[..bounded]));
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var bounded = BoundedCount(count);
            return bounded == 0 ? 0 : Record(await _entry.ReadAsync(buffer, offset, bounded, cancellationToken).ConfigureAwait(false));
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bounded = BoundedCount(buffer.Length);
            return bounded == 0 ? 0 : Record(await _entry.ReadAsync(buffer[..bounded], cancellationToken).ConfigureAwait(false));
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _entry.Dispose(); }
                finally { _archive.Dispose(); }
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            try { await _entry.DisposeAsync().ConfigureAwait(false); }
            finally { _archive.Dispose(); }
            GC.SuppressFinalize(this);
        }

        private int BoundedCount(int requested) => (int)Math.Min(requested, _remaining);

        private int Record(int read)
        {
            if (read == 0 && _remaining > 0)
                throw new InvalidDataException("Asset archive entry ended before its authorized size.");
            _remaining -= read;
            return read;
        }
    }
}
