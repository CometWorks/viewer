using System.IO.Compression;
using CometWorks.EntityViewer.Quasar.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Plugin.Abstractions;

// Offline correctness fixtures only. No web host, listener, Steam or game process.
var root = Path.Combine(Path.GetTempPath(), "viewer-asset-boundaries-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
try
{
    var content = Path.Combine(root, "Content");
    var mods = Path.Combine(root, "Mods");
    var outside = Path.Combine(root, "OtherContent");
    foreach (var folder in new[] { content, mods, outside }) Directory.CreateDirectory(folder);
    foreach (var name in new[] { "Data", "Models", "Textures" }) Directory.CreateDirectory(Path.Combine(content, name));
    Write(Path.Combine(content, "Models", "Block.mwm"), "base");
    Write(Path.Combine(outside, "Models", "Block.mwm"), "other");
    Write(Path.Combine(mods, "LocalMod", "Models", "Block.mwm"), "mod");
    Archive(Path.Combine(mods, "123.sbm"), "Wrapper/MODELS/Archive.mwm");
    Archive(Path.Combine(mods, "Legacy", "test_legacy.bin"), "Models/Legacy.mwm");
    Archive(Path.Combine(mods, "content", "244850", "456.sbm"), "Models/Workshop.mwm");
    var paths = new EntityViewerStreamingPaths(new QuasarPluginContext
    {
        InstallDirectory = root,
        PluginDirectory = root,
        CacheDirectory = root,
        // Paths reads InstallDirectory only; these collaborators are never used by this fixture.
        Manifest = null!,
        Configuration = null!,
        Environment = null!,
    });
    var settings = new EntityViewerStreamingSettings
    {
        StreamingEnabled = true,
        ConsentAccepted = true,
        ConsentVersion = EntityViewerStreamingSettings.CurrentConsentVersion,
        BaseGameSourceMode = EntityViewerStreamingSettings.ExternalInstallSourceMode,
        BaseGameContentPath = content,
        DedicatedServerModsPath = mods,
    };
    var store = new FixtureSettings(settings);
    using var resolver = new ServerAssetResolver(paths, store, NullLogger<ServerAssetResolver>.Instance);
    var session = new ViewerAssetSession
    {
        AgentId = "server",
        Mods = [new() { RootId = "local", Name = "LocalMod" }, new() { RootId = "123", PublishedFileId = 123 },
            new() { RootId = "legacy", Name = "Legacy" }, new() { RootId = "456", PublishedFileId = 456 },
            new() { RootId = "bad", Name = "../OtherContent", FriendlyName = "..\\OtherContent" }],
    };
    Check(await Read("models/block") == "base", "case-insensitive lookup and extension inference");
    Check(await Read("Content/Models/Block.mwm") == "base", "Content prefix");
    Check(await Read("Models/Block.mwm", "local") == "mod", "directory mod");
    Check(await Read("Models/Archive.mwm", "123") == "archive", "archive case lookup and wrapper");
    Check(await Read("Mods/123/Models/Archive.mwm") == "archive", "Mods prefix");
    Check(await Read("content/244850/456/Models/Workshop.mwm") == "archive", "nested Workshop layout");
    Check(await Read("Models/Legacy.mwm", "legacy") == "archive", "legacy archive");
    foreach (var invalid in new[] { "../Models/Block.mwm", "Models/../Models/Block.mwm", "./Models/Block.mwm",
        "/Models/Block.mwm", "C:\\Models\\Block.mwm", "\\\\server\\Models\\Block.mwm", "Models//Block.mwm",
        "Models/./Block.mwm", "Models/Block.mwm.", "Models/bad:Block.mwm", "Models/CON", "Models/Block\0.mwm", "Models/Block.mwm ",
        "content/244850/../Models/Block.mwm", "Mods/123/../Models/Block.mwm" })
        Check(await Resolve(invalid) is null, "invalid logical component: " + invalid.Replace('\0', '?'));
    foreach (var invalid in new[] { "../OtherContent", "LocalMod/Models", "LocalMod\\Models", ".", "..", "/LocalMod" })
        Check(await Resolve("Models/Block.mwm", invalid) is null, "non-leaf root alias");
    Check(await Resolve("Models/Block.mwm", "bad") is { RootKind: "content" }, "invalid mod metadata cannot select outside root; base fallback preserved");

    // Authorization ownership/expiry is independent of path validation.
    var tokens = new ViewerAssetSessionStore();
    var owned = tokens.CreateSession("alice", new());
    var baseAsset = (await Resolve("Models/Block.mwm"))!;
    var token = tokens.CreateAssetToken("alice", owned.Id, baseAsset);
    Check(tokens.TryGetSession(owned.Id, "bob") is null && tokens.TryGetAssetToken(token.Id, "bob") is null, "user ownership");
    settings.StreamingEnabled = false;
    Check(await Resolve("Models/Block.mwm") is null, "streaming consent gate");
    settings.StreamingEnabled = true;

    // These fixtures require symlink permission on Windows; fail clearly when unavailable.
    var contentAlias = Path.Combine(root, "ContentAlias");
    Directory.CreateSymbolicLink(contentAlias, content);
    settings.BaseGameContentPath = contentAlias;
    Check(await Read("Models/Block.mwm") == "base", "configured root symlink is an approved root");
    settings.BaseGameContentPath = content;
    var modsAlias = Path.Combine(root, "ModsAlias");
    Directory.CreateSymbolicLink(modsAlias, mods);
    settings.DedicatedServerModsPath = modsAlias;
    Check(await Read("Models/Archive.mwm", "123") == "archive", "configured Mods symlink");
    settings.DedicatedServerModsPath = mods;
    Directory.CreateSymbolicLink(Path.Combine(content, "Inside"), Path.Combine(content, "Models"));
    Check(await Read("Inside/Block.mwm") == "base", "internal link remains within approved root");
    Directory.CreateSymbolicLink(Path.Combine(content, "Outside"), outside);
    File.CreateSymbolicLink(Path.Combine(content, "Models", "Outside.mwm"), Path.Combine(outside, "Models", "Block.mwm"));
    Check(await Resolve("Outside/Models/Block.mwm") is null, "directory link leaves approved root");
    Check(await Resolve("Models/Outside.mwm") is null, "file link leaves approved root");
    Directory.CreateSymbolicLink(Path.Combine(mods, "LinkedMod"), outside);
    var linkedSession = new ViewerAssetSession { Mods = [new() { RootId = "linked", Name = "LinkedMod" }] };
    Check(await resolver.ResolveAsync(linkedSession, new() { LogicalPath = "Models/Block.mwm", RootId = "linked" }, default)
        is { RootKind: "content" }, "linked mod cannot leave Mods root");
    Archive(Path.Combine(outside, "outside.sbm"), "Models/Outside.mwm");
    File.CreateSymbolicLink(Path.Combine(mods, "789.sbm"), Path.Combine(outside, "outside.sbm"));
    var archiveSession = new ViewerAssetSession { Mods = [new() { RootId = "789", PublishedFileId = 789 }] };
    Check(await resolver.ResolveAsync(archiveSession, new() { LogicalPath = "Models/Outside.mwm", RootId = "789" }, default) is null,
        "archive link leaves Mods root");
    Archive(Path.Combine(outside, "outside_legacy.bin"), "Models/Outside.mwm");
    File.CreateSymbolicLink(Path.Combine(mods, "Legacy", "outside_legacy.bin"), Path.Combine(outside, "outside_legacy.bin"));
    File.Delete(Path.Combine(mods, "Legacy", "test_legacy.bin"));
    Check(await Resolve("Models/Outside.mwm", "legacy") is null, "legacy link leaves Mods root");

    Directory.CreateSymbolicLink(Path.Combine(content, "Broken"), Path.Combine(root, "Missing"));
    Directory.CreateSymbolicLink(Path.Combine(content, "Cycle"), Path.Combine(content, "Cycle"));
    Check(await Resolve("Broken/Block.mwm") is null, "broken link fails closed");
    Check(await Resolve("Cycle/Block.mwm") is null, "cyclic link fails closed");
    var autoMods = Path.Combine(root, "Magnetars", "server", "DedicatedServer", "Mods");
    Write(Path.Combine(autoMods, "LocalMod", "Models", "Auto.mwm"), "auto");
    settings.DedicatedServerModsPath = string.Empty;
    Check(await Read("Models/Auto.mwm", "local") == "auto", "managed per-agent Mods fallback");
    var invalidAgent = new ViewerAssetSession { AgentId = "../server", Mods = session.Mods };
    Check(await resolver.ResolveAsync(invalidAgent, new() { LogicalPath = "Models/Auto.mwm", RootId = "local" }, default) is null,
        "managed agent id must be a leaf");
    Directory.Move(autoMods, autoMods + "Saved");
    Directory.CreateSymbolicLink(autoMods, outside);
    Check(await Read("Models/Block.mwm", "local") == "base", "managed fallback cannot establish an outside linked root");
    settings.DedicatedServerModsPath = mods;

    var cachedMod = (await Resolve("Models/Block.mwm", "local"))!;
    Directory.Move(Path.Combine(mods, "LocalMod"), Path.Combine(mods, "SavedLocalMod"));
    Directory.CreateSymbolicLink(Path.Combine(mods, "LocalMod"), outside);
    Throws(() => cachedMod.OpenRead(), "cached directory mod final open rechecks ancestry");

    // Cached token opens recheck the actual ancestry, for loose files and archives.
    Directory.Move(Path.Combine(content, "Models"), Path.Combine(content, "SavedModels"));
    Directory.CreateSymbolicLink(Path.Combine(content, "Models"), Path.Combine(outside, "Models"));
    Throws(() => token.Asset.OpenRead(), "cached loose-file final open rechecks ancestry");
    var cachedArchive = (await Resolve("Models/Archive.mwm", "123"))!;
    File.Delete(Path.Combine(mods, "123.sbm"));
    File.CreateSymbolicLink(Path.Combine(mods, "123.sbm"), Path.Combine(outside, "outside.sbm"));
    Throws(() => cachedArchive.OpenRead(), "cached archive final open rechecks source");

    if (!OperatingSystem.IsWindows())
    {
        var caseSibling = Path.Combine(root, "content");
        Write(Path.Combine(caseSibling, "Models", "Case.mwm"), "case sibling");
        Directory.CreateSymbolicLink(Path.Combine(content, "CaseSibling"), caseSibling);
        Check(await Resolve("CaseSibling/Models/Case.mwm") is null, "Linux containment respects case-distinct sibling roots");
    }
    Console.WriteLine($"PASS: {checks} offline asset boundary checks ({Environment.OSVersion.Platform}).");

    async Task<ResolvedServerAsset?> Resolve(string path, string rootId = "") =>
        await resolver.ResolveAsync(session, new() { LogicalPath = path, RootId = rootId }, default);
    async Task<string?> Read(string path, string rootId = "")
    {
        var asset = await Resolve(path, rootId);
        if (asset is null) return null;
        using var stream = asset.OpenRead();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}

void Check(bool valid, string message)
{
    if (!valid) throw new Exception(message);
    checks++;
}
void Throws(Func<Stream> open, string message)
{
    try { using var stream = open(); }
    catch (FileNotFoundException) { checks++; return; }
    throw new Exception(message);
}
static void Write(string path, string text)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
}
static void Archive(string path, string entry)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
    writer.Write("archive");
}
sealed class FixtureSettings(EntityViewerStreamingSettings settings) : IEntityViewerStreamingSettingsStore
{
    public Task<EntityViewerStreamingSettings> GetAsync(CancellationToken token) => Task.FromResult(settings.Copy());
    public Task<EntityViewerStreamingSettings> UpdateAsync(Func<EntityViewerStreamingSettings, EntityViewerStreamingSettings> update,
        CancellationToken token) => throw new NotSupportedException();
}
