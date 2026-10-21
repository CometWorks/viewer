namespace CometWorks.EntityViewer.Quasar.Streaming;

// Lookup may ignore SE asset-name casing; containment must follow the host filesystem.
internal static class AssetPathBoundary
{
    internal static bool IsLeaf(string value) =>
        !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        !value.EndsWith('.') && !value.EndsWith(' ') &&
        !value.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)) &&
        !IsDeviceName(value.Split('.')[0]);

    private static bool IsDeviceName(string value) =>
        value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
        value.Length == 4 && (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && value[3] is >= '1' and <= '9';

    internal static bool IsLogicalPath(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Replace('\\', '/').Split('/').All(IsLeaf);

    internal static string ResolveExisting(string path, int links = 0)
    {
        if (links > 40)
            throw new IOException("Too many asset filesystem links.");
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                continue;
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(current) : new FileInfo(current);
            var target = info.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new IOException("Unsupported asset filesystem reparse point.");
            current = ResolveExisting(target.FullName, links + 1);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    internal static bool IsUnder(string path, string approvedRoot)
    {
        var relative = Path.GetRelativePath(approvedRoot, path);
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static string? TryResolveUnder(string path, string approvedRoot)
    {
        try
        {
            var resolved = ResolveExisting(path);
            return IsUnder(resolved, approvedRoot) ? resolved : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static FileStream OpenRead(string path, string approvedRoot)
    {
        if (string.IsNullOrEmpty(approvedRoot))
            throw new FileNotFoundException("Asset has no approved source root.");
        var resolved = TryResolveUnder(path, approvedRoot)
            ?? throw new FileNotFoundException("Asset is no longer inside its approved source root.");
        // Managed APIs cannot atomically bind every ancestor to this open. Asset roots must
        // not be writable by untrusted local users; native handle-relative opens would be
        // needed to defend against a concurrent local directory/link replacement.
        return new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}
