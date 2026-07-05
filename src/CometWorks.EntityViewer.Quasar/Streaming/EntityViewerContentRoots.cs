namespace CometWorks.EntityViewer.Quasar.Streaming;

public sealed class EntityViewerContentRootProbe
{
    public string Path { get; init; } = string.Empty;

    public bool DirectoryExists { get; init; }

    public bool HasDataDirectory { get; init; }

    public bool HasModelsDirectory { get; init; }

    public bool HasTexturesDirectory { get; init; }

    public bool HasGeometryContent => DirectoryExists && HasDataDirectory && HasModelsDirectory;

    public bool IsUsable => HasGeometryContent && HasTexturesDirectory;

    public string Message { get; init; } = string.Empty;
}

public sealed class EntityViewerManagedContentSelection
{
    public string Source { get; init; } = "ManagedSteamCmd";

    public string ContentDirectory { get; init; } = string.Empty;

    public EntityViewerContentRootProbe ActiveProbe { get; init; } = EntityViewerContentRoots.Probe(string.Empty);

    public EntityViewerContentRootProbe ClientProbe { get; init; } = EntityViewerContentRoots.Probe(string.Empty);

    public EntityViewerContentRootProbe DedicatedServerProbe { get; init; } = EntityViewerContentRoots.Probe(string.Empty);

    public bool IsUsable => ActiveProbe.IsUsable;

    public string Message { get; init; } = string.Empty;
}

public static class EntityViewerContentRoots
{
    public static EntityViewerManagedContentSelection SelectManaged(EntityViewerStreamingPaths paths)
    {
        var client = Probe(paths.ManagedGameContentDirectory);
        var dedicated = Probe(paths.ManagedDedicatedServerContentDirectory);

        if (client.IsUsable)
        {
            return new EntityViewerManagedContentSelection
            {
                Source = "ManagedSteamCmd",
                ContentDirectory = paths.ManagedGameContentDirectory,
                ActiveProbe = client,
                ClientProbe = client,
                DedicatedServerProbe = dedicated,
                Message = BuildReadyMessage("Managed SteamCMD client Content", client),
            };
        }

        return new EntityViewerManagedContentSelection
        {
            Source = "ManagedSteamCmd",
            ContentDirectory = paths.ManagedGameContentDirectory,
            ActiveProbe = client,
            ClientProbe = client,
            DedicatedServerProbe = dedicated,
            Message = $"Managed client Content not ready. {client.Message}",
        };
    }

    public static EntityViewerContentRootProbe Probe(string path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
        {
            return new EntityViewerContentRootProbe
            {
                Path = normalized,
                DirectoryExists = false,
                Message = string.IsNullOrWhiteSpace(normalized)
                    ? "Content path is empty."
                    : $"Content path does not exist: {normalized}",
            };
        }

        var hasData = Directory.Exists(System.IO.Path.Combine(normalized, "Data"));
        var hasModels = Directory.Exists(System.IO.Path.Combine(normalized, "Models"));
        var hasTextures = Directory.Exists(System.IO.Path.Combine(normalized, "Textures"));
        var missing = new List<string>();
        if (!hasData)
            missing.Add("Data");
        if (!hasModels)
            missing.Add("Models");
        if (!hasTextures)
            missing.Add("Textures");

        return new EntityViewerContentRootProbe
        {
            Path = normalized,
            DirectoryExists = true,
            HasDataDirectory = hasData,
            HasModelsDirectory = hasModels,
            HasTexturesDirectory = hasTextures,
            Message = missing.Count == 0
                ? "Content folder is ready."
                : hasData && hasModels && !hasTextures
                    ? "Content folder has Data and Models, but top-level Textures folder is missing."
                : $"Content folder is missing required {string.Join(" and ", missing)} folder(s).",
        };
    }

    public static string ResolveContentRoot(EntityViewerStreamingPaths paths) => SelectManaged(paths).ContentDirectory;

    public static bool LooksUsable(string path) => Probe(path).IsUsable;

    private static string BuildReadyMessage(string label, EntityViewerContentRootProbe probe)
    {
        return probe.HasTexturesDirectory
            ? $"{label} is ready."
            : $"{label} is usable, but has no top-level Textures folder.";
    }
}
