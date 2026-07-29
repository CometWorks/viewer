using Quasar.Plugin.Abstractions;

namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class EntityViewerStreamingPaths
{
    public EntityViewerStreamingPaths(QuasarPluginContext context)
    {
        QuasarDirectory = Path.GetFullPath(context.InstallDirectory);
        ManagedRuntimeDirectory = Path.Combine(QuasarDirectory, "ManagedRuntime");
        ManagedToolsDirectory = Path.Combine(ManagedRuntimeDirectory, "Tools");
        PluginToolsDirectory = Path.Combine(ManagedToolsDirectory, "CometWorks.EntityViewer");
        ManagedGameClientDirectory = Path.Combine(ManagedToolsDirectory, "SpaceEngineersClient");
        ManagedGameContentDirectory = Path.Combine(ManagedGameClientDirectory, "Content");
        ManagedDedicatedServerDirectory = Path.Combine(ManagedToolsDirectory, "SpaceEngineersDedicatedServer");
        ManagedDedicatedServerContentDirectory = Path.Combine(ManagedDedicatedServerDirectory, "Content");
        SettingsPath = Path.Combine(PluginToolsDirectory, "asset-streaming-settings.json");
    }

    public string QuasarDirectory { get; }

    public string ManagedRuntimeDirectory { get; }

    public string ManagedToolsDirectory { get; }

    public string PluginToolsDirectory { get; }

    public string ManagedGameClientDirectory { get; }

    public string ManagedGameContentDirectory { get; }

    public string ManagedDedicatedServerDirectory { get; }

    public string ManagedDedicatedServerContentDirectory { get; }

    public string SettingsPath { get; }

    public string ResolveConfiguredPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim()
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(normalized)
            ? normalized
            : Path.Combine(QuasarDirectory, normalized));
    }

    public string ToStoredPath(string? value)
    {
        var resolved = ResolveConfiguredPath(value);
        if (string.IsNullOrWhiteSpace(resolved))
            return string.Empty;

        var root = Path.TrimEndingDirectorySeparator(QuasarDirectory);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!resolved.Equals(root, comparison) &&
            !resolved.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            return resolved;
        }

        var relative = Path.GetRelativePath(root, resolved);
        return relative == "."
            ? string.Empty
            : relative.Replace('\\', '/');
    }
}
