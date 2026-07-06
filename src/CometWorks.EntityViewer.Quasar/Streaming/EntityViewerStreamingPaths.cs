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
}
