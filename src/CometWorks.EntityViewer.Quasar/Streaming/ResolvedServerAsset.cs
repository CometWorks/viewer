using System.IO.Compression;

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

    public string ContentType { get; init; } = "application/octet-stream";

    public bool IsArchiveEntry => !string.IsNullOrWhiteSpace(ArchiveEntryName);

    public Stream OpenRead()
    {
        if (!IsArchiveEntry)
            return File.OpenRead(FilePath);

        using var archive = ZipFile.OpenRead(FilePath);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName, ArchiveEntryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new FileNotFoundException($"Archive entry '{ArchiveEntryName}' not found.", FilePath);

        var memory = new MemoryStream();
        using (var entryStream = entry.Open())
        {
            entryStream.CopyTo(memory);
        }

        memory.Position = 0;
        return memory;
    }
}
