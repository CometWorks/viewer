using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class ResolvedServerAsset
{
    public string LogicalPath { get; init; } = string.Empty;

    public string RootId { get; init; } = string.Empty;

    public string RootKind { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public string ArchiveEntryName { get; init; } = string.Empty;

    public long Size { get; init; }

    public DateTimeOffset? LastModifiedUtc { get; init; }

    public DateTimeOffset? SourceLastModifiedUtc { get; init; }

    public long SourceSize { get; init; }

    public string ContentType { get; init; } = "application/octet-stream";

    public bool IsArchiveEntry => !string.IsNullOrWhiteSpace(ArchiveEntryName);

    public string CacheKeyForUser(string userId) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', userId, FilePath, ArchiveEntryName,
            Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SourceSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (SourceLastModifiedUtc ?? LastModifiedUtc)?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)))));

    public Stream OpenRead()
    {
        var file = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!IsArchiveEntry)
            return file;

        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(file, ZipArchiveMode.Read);
            var entry = archive.GetEntry(ArchiveEntryName);
            if (entry is null)
                throw new FileNotFoundException($"Archive entry '{ArchiveEntryName}' not found.", FilePath);
            return new ArchiveReadStream(archive, entry.Open(), entry.Length);
        }
        catch
        {
            archive?.Dispose();
            file.Dispose();
            throw;
        }
    }

    // Each response owns its archive and reads lazily; slow clients share neither buffers nor cursors.
    private sealed class ArchiveReadStream(ZipArchive archive, Stream entry, long length) : Stream
    {
        public override bool CanRead => entry.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => entry.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => entry.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => entry.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => entry.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { entry.Dispose(); }
                finally { archive.Dispose(); }
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            try { await entry.DisposeAsync().ConfigureAwait(false); }
            finally { archive.Dispose(); }
            GC.SuppressFinalize(this);
        }
    }
}
