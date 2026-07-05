using Quasar.Plugin.Abstractions;

namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class EntityViewerStreamingPaths
{
    private const string InstallDirectoryEnvironmentVariable = "QUASAR_INSTALL_DIR";
    private const string InstallDirectoryFileEnvironmentVariable = "QUASAR_INSTALL_DIR_FILE";
    private const string DevInstallDirectoryFileName = ".quasar-install-dir";

    public EntityViewerStreamingPaths(QuasarPluginContext context)
    {
        QuasarDirectory = ResolveQuasarDirectory(context);
        ManagedRuntimeDirectory = Path.Combine(QuasarDirectory, "ManagedRuntime");
        ManagedToolsDirectory = Path.Combine(ManagedRuntimeDirectory, "Tools");
        PluginToolsDirectory = Path.Combine(ManagedToolsDirectory, "CometWorks.EntityViewer");
        ManagedGameClientDirectory = Path.Combine(ManagedToolsDirectory, "SpaceEngineersClient");
        ManagedGameContentDirectory = Path.Combine(ManagedGameClientDirectory, "Content");
        SettingsPath = Path.Combine(PluginToolsDirectory, "asset-streaming-settings.json");
    }

    public string QuasarDirectory { get; }

    public string ManagedRuntimeDirectory { get; }

    public string ManagedToolsDirectory { get; }

    public string PluginToolsDirectory { get; }

    public string ManagedGameClientDirectory { get; }

    public string ManagedGameContentDirectory { get; }

    public string SettingsPath { get; }

    private static string ResolveQuasarDirectory(QuasarPluginContext context)
    {
        var envOverride = Environment.GetEnvironmentVariable(InstallDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envOverride))
            return Path.GetFullPath(envOverride.Trim());

        var fileOverride = TryReadInstallDirectoryOverrideFile(context);
        if (!string.IsNullOrWhiteSpace(fileOverride))
            return Path.GetFullPath(fileOverride);

        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    private static string? TryReadInstallDirectoryOverrideFile(QuasarPluginContext context)
    {
        var envFile = Environment.GetEnvironmentVariable(InstallDirectoryFileEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envFile))
        {
            var fromEnvFile = TryReadPathFile(envFile);
            if (!string.IsNullOrWhiteSpace(fromEnvFile))
                return fromEnvFile;
        }

        foreach (var directory in EnumerateProbeDirectories(context))
        {
            var candidate = Path.Combine(directory, DevInstallDirectoryFileName);
            var value = TryReadPathFile(candidate);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateProbeDirectories(QuasarPluginContext context)
    {
        yield return context.PluginDirectory;
        yield return context.CacheDirectory;
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }

    private static string? TryReadPathFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var value = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }
}
