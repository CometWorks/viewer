using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class ServerAssetResolver(
    EntityViewerStreamingPaths paths,
    IEntityViewerStreamingSettingsStore settingsStore,
    ILogger<ServerAssetResolver> logger) : IDisposable
{
    private static readonly string[] KnownExtensions = [".mwm", ".dds", ".png", ".jpg", ".jpeg", ".webp", ".sbc", ".xml"];
    private readonly MemoryCache _archiveIndexes = new(new MemoryCacheOptions { SizeLimit = 100_000 });

    public void Dispose() => _archiveIndexes.Dispose();

    public async Task<ResolvedServerAsset?> ResolveAsync(
        ViewerAssetSession session,
        AssetResolveRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.StreamingEnabled || !settings.HasCurrentConsent)
            return null;

        // Validate before removing recognized Content/Mods/Workshop prefixes.
        var input = (request.LogicalPath ?? string.Empty).Replace('\\', '/');
        if (!AssetPathBoundary.IsLogicalPath(input))
            return null;
        var normalized = NormalizeLogicalPath(input);
        if (string.IsNullOrWhiteSpace(normalized.Path))
            return null;

        try
        {
            var rootId = string.IsNullOrWhiteSpace(request.RootId) ? normalized.ModName : request.RootId;
            var mod = FindSessionMod(session, rootId, normalized.ModName);
            // Unknown path-like aliases stay invalid; a matched protocol ID is opaque.
            if (mod is null && !string.IsNullOrWhiteSpace(rootId) && !AssetPathBoundary.IsLeaf(rootId))
                return null;

            if (mod is not null)
            {
                var modAsset = TryResolveModAsset(session, settings, mod, normalized.Path);
                if (modAsset is not null)
                    return await Fingerprint(modAsset, cancellationToken).ConfigureAwait(false);
            }

            return await Fingerprint(TryResolveContentAsset(settings, normalized.Path), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null; // A changed or unreadable source is an unavailable asset.
        }
    }

    private static async Task<ResolvedServerAsset?> Fingerprint(ResolvedServerAsset? asset, CancellationToken token)
    {
        if (asset == null) return null;
        // Quasar I/O only, never the simulation thread. Hash the actual entry bytes: updates
        // can preserve both file length and timestamps (especially Workshop ZIP entries).
        await using var stream = asset.OpenRead();
        asset.ContentHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        return asset;
    }

    private ResolvedServerAsset? TryResolveContentAsset(
        EntityViewerStreamingSettings settings,
        string logicalPath)
    {
        var contentRoot = ResolveApprovedRoot(ResolveContentRoot(settings));
        if (string.IsNullOrWhiteSpace(contentRoot) || !Directory.Exists(contentRoot))
            return null;

        foreach (var candidate in CandidatePaths(logicalPath))
        {
            var resolvedPath = TryResolveFileUnderDirectory(contentRoot, candidate, contentRoot);
            if (resolvedPath is null)
                continue;

            var info = new FileInfo(resolvedPath);
            return new ResolvedServerAsset
            {
                LogicalPath = candidate,
                RootKind = "content",
                ApprovedRoot = contentRoot,
                FilePath = resolvedPath,
                Size = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc,
                ContentType = ContentTypeForPath(candidate),
            };
        }

        return null;
    }

    private ResolvedServerAsset? TryResolveModAsset(
        ViewerAssetSession session,
        EntityViewerStreamingSettings settings,
        AssetSessionModDto mod,
        string logicalPath)
    {
        var modsRoot = ResolveApprovedRoot(ResolveModsRoot(session, settings));
        if (string.IsNullOrWhiteSpace(modsRoot))
            return null;

        var modRoot = TryFindModRoot(modsRoot, mod);
        if (modRoot is null)
            return null;

        foreach (var candidate in CandidatePaths(logicalPath))
        {
            var resolved = modRoot.Kind switch
            {
                "mod-directory" => TryResolveDirectoryModAsset(modRoot.Path, modsRoot, mod.RootId, candidate),
                "mod-archive" => TryResolveArchiveAsset(modRoot.Path, modsRoot, mod.RootId, candidate, "mod-archive"),
                _ => null,
            };
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    private ResolvedServerAsset? TryResolveDirectoryModAsset(string modDirectory, string approvedRoot, string rootId, string logicalPath)
    {
        var file = TryResolveFileUnderDirectory(modDirectory, logicalPath, approvedRoot);
        if (file is not null)
        {
            var info = new FileInfo(file);
            return new ResolvedServerAsset
            {
                LogicalPath = logicalPath,
                RootId = rootId,
                RootKind = "mod-directory",
                ApprovedRoot = approvedRoot,
                FilePath = file,
                Size = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc,
                ContentType = ContentTypeForPath(logicalPath),
            };
        }

        var legacyArchive = TryFindLegacyArchive(modDirectory);
        return legacyArchive is null ? null : TryResolveArchiveAsset(legacyArchive, approvedRoot, rootId, logicalPath, "mod-archive");
    }

    private ResolvedServerAsset? TryResolveArchiveAsset(string archivePath, string approvedRoot, string rootId, string logicalPath, string rootKind)
    {
        try
        {
            archivePath = AssetPathBoundary.TryResolveUnder(archivePath, approvedRoot)!;
            if (archivePath is null)
                return null;
            var info = new FileInfo(archivePath);
            var key = (archivePath, info.LastWriteTimeUtc, info.Length);
            var index = _archiveIndexes.GetOrCreate(key, cacheEntry =>
            {
                using var file = AssetPathBoundary.OpenRead(archivePath, approvedRoot);
                using var archive = new ZipArchive(file, ZipArchiveMode.Read);
                var entries = new Dictionary<string, ArchiveAsset>(StringComparer.Ordinal);
                foreach (var candidate in archive.Entries)
                {
                    if (!AssetPathBoundary.IsLogicalPath(candidate.FullName)) continue;
                    entries.TryAdd(ArchivePathKey(candidate.FullName),
                        new ArchiveAsset(candidate.FullName, candidate.Length, candidate.LastWriteTime.UtcDateTime));
                }
                cacheEntry.SetSize(Math.Max(1, entries.Count)).SetAbsoluteExpiration(TimeSpan.FromMinutes(10));
                return new ArchiveIndex(DetectSingleTopLevel(archive), entries);
            })!;
            var targetKey = ArchivePathKey(logicalPath);
            if (!index.Entries.TryGetValue(targetKey, out var entry) &&
                (string.IsNullOrWhiteSpace(index.TopLevel) ||
                 !index.Entries.TryGetValue(ArchivePathKey($"{index.TopLevel}/{logicalPath}"), out entry)))
                return null;

            return new ResolvedServerAsset
            {
                LogicalPath = logicalPath,
                RootId = rootId,
                RootKind = rootKind,
                ApprovedRoot = approvedRoot,
                FilePath = archivePath,
                ArchiveEntryName = entry!.Name,
                Size = entry.Size,
                LastModifiedUtc = entry.Modified,
                SourceSize = info.Length,
                SourceLastModifiedUtc = info.LastWriteTimeUtc,
                ContentType = ContentTypeForPath(logicalPath),
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not inspect mod archive {ArchivePath}.", archivePath);
            return null;
        }
    }

    private string ResolveContentRoot(EntityViewerStreamingSettings settings)
        => EntityViewerContentRoots.ResolveContentRoot(settings, paths);

    private string ResolveModsRoot(ViewerAssetSession session, EntityViewerStreamingSettings settings)
    {
        var configuredModsPath = paths.ResolveConfiguredPath(settings.DedicatedServerModsPath);
        if (Directory.Exists(configuredModsPath))
        {
            return configuredModsPath;
        }

        var magnetarsRoot = Path.Combine(paths.QuasarDirectory, "Magnetars");
        var agentDirectoryName = session.AgentId;
        if (!AssetPathBoundary.IsLeaf(agentDirectoryName))
            return string.Empty;
        var candidate = Path.Combine(magnetarsRoot, agentDirectoryName, "DedicatedServer", "Mods");
        var fullCandidate = Path.GetFullPath(candidate);
        var approved = ResolveApprovedRoot(magnetarsRoot);
        return Directory.Exists(fullCandidate) && !string.IsNullOrEmpty(approved)
            ? AssetPathBoundary.TryResolveUnder(fullCandidate, approved) ?? string.Empty
            : string.Empty;
    }

    private static AssetSessionModDto? FindSessionMod(ViewerAssetSession session, string rootId, string modName)
    {
        // RootId is a protocol identifier (for example mods-direct:LocalMod), not a path.
        if (!string.IsNullOrWhiteSpace(rootId))
        {
            var identified = session.Mods.FirstOrDefault(mod => string.Equals(mod.RootId, rootId, StringComparison.Ordinal));
            if (identified is not null)
                return identified;
        }

        foreach (var mod in session.Mods)
        {
            if (MatchesModAlias(mod, rootId) || MatchesModAlias(mod, modName))
                return mod;
        }

        return null;
    }

    private static bool MatchesModAlias(AssetSessionModDto mod, string value)
    {
        var normalized = NormalizeModAlias(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return NormalizeModAlias(mod.Name) == normalized ||
               NormalizeModAlias(mod.FriendlyName) == normalized ||
               (mod.PublishedFileId != 0 && mod.PublishedFileId.ToString() == normalized);
    }

    private static ModRoot? TryFindModRoot(string modsRoot, AssetSessionModDto mod)
    {
        foreach (var root in EnumerateModSearchRoots(modsRoot))
        {
            if (AssetPathBoundary.TryResolveUnder(root, modsRoot) is null && root != modsRoot)
                continue;
            foreach (var name in ModCandidateNames(mod).Where(AssetPathBoundary.IsLeaf))
            {
                var match = TryFindTopLevelChild(root, name, modsRoot);
                if (match is not null)
                    return match;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateModSearchRoots(string modsRoot)
    {
        yield return modsRoot;

        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(modsRoot));
        if (rootName.Equals("244850", StringComparison.OrdinalIgnoreCase))
            yield break;

        if (rootName.Equals("content", StringComparison.OrdinalIgnoreCase))
        {
            var workshop = Path.Combine(modsRoot, "244850");
            if (Directory.Exists(workshop))
                yield return workshop;
            yield break;
        }

        var nested = Path.Combine(modsRoot, "content", "244850");
        if (Directory.Exists(nested))
            yield return nested;
    }

    private static ModRoot? TryFindTopLevelChild(string root, string name, string approvedRoot)
    {
        if (string.IsNullOrWhiteSpace(root) || !AssetPathBoundary.IsLeaf(name) || !Directory.Exists(root))
            return null;

        foreach (var candidateName in CandidateModRootNames(name))
        {
            var exact = AssetPathBoundary.TryResolveUnder(Path.Combine(root, candidateName), approvedRoot);
            if (exact is null)
                continue;
            if (Directory.Exists(exact))
                return new ModRoot("mod-directory", exact);
            if (File.Exists(exact) && IsModArchive(candidateName))
                return new ModRoot("mod-archive", exact);
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                var fileName = Path.GetFileName(entry);
                foreach (var candidateName in CandidateModRootNames(name))
                {
                    if (!fileName.Equals(candidateName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var resolved = AssetPathBoundary.TryResolveUnder(entry, approvedRoot);
                    if (resolved is null)
                        continue;
                    if (Directory.Exists(resolved))
                        return new ModRoot("mod-directory", resolved);
                    if (File.Exists(resolved) && IsModArchive(entry))
                        return new ModRoot("mod-archive", resolved);
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static IEnumerable<string> CandidateModRootNames(string name)
    {
        var value = name.Trim();
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        yield return value;
        var stem = ArchiveStem(value);
        if (!string.IsNullOrWhiteSpace(stem) && !stem.Equals(value, StringComparison.OrdinalIgnoreCase))
            yield return stem;
        else if (!IsModArchive(value))
            yield return value + ".sbm";
    }

    private static IEnumerable<string> ModCandidateNames(AssetSessionModDto mod)
    {
        if (mod.PublishedFileId != 0)
            yield return mod.PublishedFileId.ToString();
        if (!string.IsNullOrWhiteSpace(mod.Name))
            yield return mod.Name;
        if (!string.IsNullOrWhiteSpace(mod.FriendlyName))
            yield return mod.FriendlyName;
    }

    private static string? TryResolveFileUnderDirectory(string root, string relativePath, string approvedRoot)
    {
        if (!AssetPathBoundary.IsLogicalPath(relativePath))
            return null;
        var rootFullPath = Path.GetFullPath(root);
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = rootFullPath;

        for (var i = 0; i < parts.Length; i++)
        {
            var exact = Path.Combine(current, parts[i]);
            var isLast = i == parts.Length - 1;
            if (isLast)
            {
                var filePath = File.Exists(exact) ? exact : FindChild(current, parts[i], wantDirectory: false);
                if (filePath is null)
                    return null;

                var full = Path.GetFullPath(filePath);
                return AssetPathBoundary.TryResolveUnder(full, approvedRoot);
            }

            var directory = Directory.Exists(exact) ? exact : FindChild(current, parts[i], wantDirectory: true);
            if (directory is null)
                return null;

            current = AssetPathBoundary.TryResolveUnder(directory, approvedRoot)!;
            if (current is null)
                return null;
        }

        return null;
    }

    private static string? FindChild(string parent, string name, bool wantDirectory)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                if (!Path.GetFileName(entry).Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (wantDirectory && Directory.Exists(entry))
                    return entry;
                if (!wantDirectory && File.Exists(entry))
                    return entry;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ResolveApprovedRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return string.Empty;
        try { return AssetPathBoundary.ResolveExisting(root); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> CandidatePaths(string logicalPath)
    {
        var extension = Path.GetExtension(logicalPath);
        if (KnownExtensions.Any(value => value.Equals(extension, StringComparison.OrdinalIgnoreCase)))
        {
            yield return logicalPath;
            yield break;
        }

        yield return logicalPath + ".mwm";
        yield return logicalPath;
    }

    private static NormalizedPath NormalizeLogicalPath(string path)
    {
        var value = (path ?? string.Empty).Replace('\\', '/');
        var workshopMatch = System.Text.RegularExpressions.Regex.Match(
            value,
            @"(?:^|/)content/244850/([^/]+)/(.+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (workshopMatch.Success)
            return new NormalizedPath(StripModArchivePrefix(workshopMatch.Groups[2].Value), workshopMatch.Groups[1].Value);

        if (value.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
            value = value["Content/".Length..];

        if (value.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = value["Mods/".Length..];
            var slash = relative.IndexOf('/');
            if (slash >= 0)
                return new NormalizedPath(StripModArchivePrefix(relative[(slash + 1)..]), relative[..slash]);
        }

        return new NormalizedPath(StripModArchivePrefix(value), string.Empty);
    }

    private static string StripModArchivePrefix(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 1 && IsModArchive(parts[0]))
            parts.RemoveAt(0);
        return string.Join('/', parts);
    }

    private static string NormalizeModAlias(string value)
    {
        var alias = (value ?? string.Empty).Trim().Replace('\\', '/');
        return AssetPathBoundary.IsLeaf(alias) ? alias.ToLowerInvariant() : string.Empty;
    }

    private static string ArchiveStem(string path)
    {
        var value = path ?? string.Empty;
        if (value.EndsWith(".sbm", StringComparison.OrdinalIgnoreCase))
            return value[..^".sbm".Length];
        if (value.EndsWith("_legacy.bin", StringComparison.OrdinalIgnoreCase))
            return value[..^"_legacy.bin".Length];
        return value;
    }

    private static bool IsModArchive(string path) =>
        path.EndsWith(".sbm", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("_legacy.bin", StringComparison.OrdinalIgnoreCase);

    private static string? TryFindLegacyArchive(string modDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(modDirectory, "*_legacy.bin", SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string ArchivePathKey(string path) =>
        (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();

    private static string DetectSingleTopLevel(ZipArchive archive)
    {
        var firstParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasAssetDirectory = false;
        foreach (var entry in archive.Entries)
        {
            if (!AssetPathBoundary.IsLogicalPath(entry.FullName.TrimEnd('/')))
                continue;
            var parts = ArchivePathKey(entry.FullName).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            firstParts.Add(parts[0]);
            if (parts[0] is "data" or "models" or "textures")
                hasAssetDirectory = true;
        }

        return !hasAssetDirectory && firstParts.Count == 1 ? firstParts.First() : string.Empty;
    }

    private static string ContentTypeForPath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".dds" => "image/vnd-ms.dds",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".xml" or ".sbc" => "application/xml",
            ".mwm" => "application/octet-stream",
            _ => "application/octet-stream",
        };
    }

    private sealed record NormalizedPath(string Path, string ModName);

    private sealed record ModRoot(string Kind, string Path);
    private sealed record ArchiveAsset(string Name, long Size, DateTimeOffset Modified);
    private sealed record ArchiveIndex(string TopLevel, Dictionary<string, ArchiveAsset> Entries);
}
