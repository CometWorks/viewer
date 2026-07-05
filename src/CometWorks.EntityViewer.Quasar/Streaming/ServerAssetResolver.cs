using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class ServerAssetResolver(
    EntityViewerStreamingPaths paths,
    IEntityViewerStreamingSettingsStore settingsStore,
    ILogger<ServerAssetResolver> logger)
{
    private static readonly string[] KnownExtensions = [".mwm", ".dds", ".png", ".jpg", ".jpeg", ".webp", ".sbc", ".xml"];

    public async Task<ResolvedServerAsset?> ResolveAsync(
        ViewerAssetSession session,
        AssetResolveRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.StreamingEnabled || !settings.HasCurrentConsent)
            return null;

        var normalized = NormalizeLogicalPath(request.LogicalPath);
        if (string.IsNullOrWhiteSpace(normalized.Path))
            return null;

        var rootId = string.IsNullOrWhiteSpace(request.RootId) ? normalized.ModName : request.RootId.Trim();
        var sourceKind = request.SourceKind ?? string.Empty;
        var shouldResolveMod = !string.IsNullOrWhiteSpace(rootId) ||
                               !string.IsNullOrWhiteSpace(normalized.ModName) ||
                               sourceKind.Equals("mod", StringComparison.OrdinalIgnoreCase);

        if (shouldResolveMod)
        {
            var modAsset = TryResolveModAsset(session, settings, rootId, normalized.ModName, normalized.Path);
            if (modAsset is not null)
                return modAsset;
        }

        return TryResolveContentAsset(settings, normalized.Path);
    }

    private ResolvedServerAsset? TryResolveContentAsset(EntityViewerStreamingSettings settings, string logicalPath)
    {
        var contentRoot = ResolveContentRoot(settings);
        if (string.IsNullOrWhiteSpace(contentRoot) || !Directory.Exists(contentRoot))
            return null;

        foreach (var candidate in CandidatePaths(logicalPath))
        {
            var resolvedPath = TryResolveFileUnderDirectory(contentRoot, candidate);
            if (resolvedPath is null)
                continue;

            var info = new FileInfo(resolvedPath);
            return new ResolvedServerAsset
            {
                LogicalPath = candidate,
                RootKind = "content",
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
        string rootId,
        string modName,
        string logicalPath)
    {
        if (string.IsNullOrWhiteSpace(settings.DedicatedServerModsPath) ||
            !Directory.Exists(settings.DedicatedServerModsPath))
        {
            return null;
        }

        var mod = FindSessionMod(session, rootId, modName);
        if (mod is null)
            return null;

        var modRoot = TryFindModRoot(settings.DedicatedServerModsPath, mod);
        if (modRoot is null)
            return null;

        foreach (var candidate in CandidatePaths(logicalPath))
        {
            var resolved = modRoot.Kind switch
            {
                "mod-directory" => TryResolveDirectoryModAsset(modRoot.Path, mod.RootId, candidate),
                "mod-archive" => TryResolveArchiveAsset(modRoot.Path, mod.RootId, candidate, "mod-archive"),
                _ => null,
            };
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    private ResolvedServerAsset? TryResolveDirectoryModAsset(string modDirectory, string rootId, string logicalPath)
    {
        var file = TryResolveFileUnderDirectory(modDirectory, logicalPath);
        if (file is not null)
        {
            var info = new FileInfo(file);
            return new ResolvedServerAsset
            {
                LogicalPath = logicalPath,
                RootId = rootId,
                RootKind = "mod-directory",
                FilePath = file,
                Size = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc,
                ContentType = ContentTypeForPath(logicalPath),
            };
        }

        var legacyArchive = TryFindLegacyArchive(modDirectory);
        return legacyArchive is null ? null : TryResolveArchiveAsset(legacyArchive, rootId, logicalPath, "mod-archive");
    }

    private ResolvedServerAsset? TryResolveArchiveAsset(string archivePath, string rootId, string logicalPath, string rootKind)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var targetKey = ArchivePathKey(logicalPath);
            var singleTopLevel = DetectSingleTopLevel(archive);
            var entry = archive.Entries.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Name) &&
                                                                    ArchivePathKey(candidate.FullName) == targetKey)
                        ?? (!string.IsNullOrWhiteSpace(singleTopLevel)
                            ? archive.Entries.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Name) &&
                                                                         ArchivePathKey(candidate.FullName) == ArchivePathKey($"{singleTopLevel}/{logicalPath}"))
                            : null);
            if (entry is null)
                return null;

            return new ResolvedServerAsset
            {
                LogicalPath = logicalPath,
                RootId = rootId,
                RootKind = rootKind,
                FilePath = archivePath,
                ArchiveEntryName = entry.FullName,
                Size = entry.Length,
                LastModifiedUtc = entry.LastWriteTime.UtcDateTime,
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
    {
        if (settings.BaseGameSourceMode.Equals("ExternalInstall", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.BaseGameContentPath))
        {
            return Path.GetFullPath(settings.BaseGameContentPath);
        }

        return paths.ManagedGameContentDirectory;
    }

    private static AssetSessionModDto? FindSessionMod(ViewerAssetSession session, string rootId, string modName)
    {
        foreach (var mod in session.Mods)
        {
            if (MatchesMod(mod, rootId) || MatchesMod(mod, modName))
                return mod;
        }

        return null;
    }

    private static bool MatchesMod(AssetSessionModDto mod, string value)
    {
        var normalized = NormalizeModAlias(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return NormalizeModAlias(mod.RootId) == normalized ||
               NormalizeModAlias(mod.Name) == normalized ||
               NormalizeModAlias(mod.FriendlyName) == normalized ||
               (mod.PublishedFileId != 0 && mod.PublishedFileId.ToString() == normalized);
    }

    private static ModRoot? TryFindModRoot(string modsRoot, AssetSessionModDto mod)
    {
        foreach (var root in EnumerateModSearchRoots(modsRoot))
        {
            foreach (var name in ModCandidateNames(mod))
            {
                var match = TryFindTopLevelChild(root, name);
                if (match is not null)
                    return match;

                if (!ArchiveStem(name).Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                match = TryFindTopLevelChild(root, ArchiveStem(name));
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

    private static ModRoot? TryFindTopLevelChild(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(name) || !Directory.Exists(root))
            return null;

        foreach (var candidateName in CandidateModRootNames(name))
        {
            var exact = Path.Combine(root, candidateName);
            if (Directory.Exists(exact))
                return new ModRoot("mod-directory", exact);
            if (File.Exists(exact) && IsModArchive(exact))
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

                    if (Directory.Exists(entry))
                        return new ModRoot("mod-directory", entry);
                    if (File.Exists(entry) && IsModArchive(entry))
                        return new ModRoot("mod-archive", entry);
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
        if (!string.IsNullOrWhiteSpace(mod.RootId))
            yield return mod.RootId;
    }

    private static string? TryResolveFileUnderDirectory(string root, string relativePath)
    {
        var rootFullPath = Path.GetFullPath(root);
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
                return IsPathUnder(full, rootFullPath) ? full : null;
            }

            var directory = Directory.Exists(exact) ? exact : FindChild(current, parts[i], wantDirectory: true);
            if (directory is null)
                return null;

            current = directory;
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

    private static bool IsPathUnder(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
        var value = (path ?? string.Empty).Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
            value = value[2..];

        value = value.Replace("//", "/", StringComparison.Ordinal);
        if (value.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
            value = value["Content/".Length..];

        var workshopMatch = System.Text.RegularExpressions.Regex.Match(
            value,
            @"(?:^|/)content/244850/([^/]+)/(.+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (workshopMatch.Success)
            return new NormalizedPath(StripModArchivePrefix(workshopMatch.Groups[2].Value), workshopMatch.Groups[1].Value);

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
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count > 1 && IsModArchive(parts[0]))
            parts.RemoveAt(0);
        return string.Join('/', parts);
    }

    private static string NormalizeModAlias(string value)
    {
        var alias = (value ?? string.Empty).Trim().Replace('\\', '/');
        var slash = alias.IndexOf('/');
        if (slash >= 0)
            alias = alias[..slash];
        return alias.ToLowerInvariant();
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
        (path ?? string.Empty).Replace('\\', '/').TrimStart('.', '/').Replace("//", "/", StringComparison.Ordinal).ToLowerInvariant();

    private static string DetectSingleTopLevel(ZipArchive archive)
    {
        var firstParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasAssetDirectory = false;
        foreach (var entry in archive.Entries)
        {
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
}
