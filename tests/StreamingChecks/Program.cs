using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using CometWorks.EntityViewer.Quasar;
using CometWorks.EntityViewer.Quasar.Streaming;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Manifests;
using Quasar.Plugin.Abstractions.Security;

var root = Path.Combine(Path.GetTempPath(), $"viewer-streaming-checks-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    var context = new QuasarPluginContext
    {
        InstallDirectory = root, PluginDirectory = root, CacheDirectory = root,
        Configuration = builder.Configuration, Environment = builder.Environment,
        Manifest = new QuasarPluginManifest
        {
            Id = "cometworks.entityviewer", DisplayName = "Entity Viewer", Version = "test",
            EntryAssembly = "test", EntryType = "test", ProjectPath = "test",
        },
    };
    var plugin = new EntityViewerQuasarPlugin();
    plugin.ConfigureServices(builder.Services, context);
    builder.Services.RemoveAll<IHostedService>(); // No SteamCMD background work in these checks.
    builder.Services.AddSingleton<IQuasarCompanionChannel, UnusedCompanionChannel>();
    builder.Services.AddAuthentication("test")
        .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy(QuasarPolicyNames.CanView, policy => policy.RequireAuthenticatedUser());
        options.AddPolicy(QuasarPolicyNames.CanManageSecurity, policy => policy.RequireClaim(ClaimTypes.NameIdentifier, "alice"));
    });
    await using var app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    plugin.ConfigureEndpoints(app, context);

    var content = Path.Combine(root, "Content");
    foreach (var directory in new[] { "Data", "Models", "Textures" })
        Directory.CreateDirectory(Path.Combine(content, directory));
    await File.WriteAllBytesAsync(Path.Combine(content, "Models", "block.mwm"), [1, 2, 3, 4]);
    var mods = Path.Combine(root, "Mods");
    Directory.CreateDirectory(mods);
    var archivePath = Path.Combine(mods, "123.sbm");
    var bytes = RandomNumberGenerator.GetBytes(16 * 1024 * 1024);
    var expectedHash = SHA256.HashData(bytes);
    WriteArchive(archivePath, bytes);
    var settingsStore = app.Services.GetRequiredService<IEntityViewerStreamingSettingsStore>();
    await settingsStore.UpdateAsync(settings =>
    {
        settings.StreamingEnabled = settings.ConsentAccepted = true;
        settings.ConsentVersion = EntityViewerStreamingSettings.CurrentConsentVersion;
        settings.BaseGameSourceMode = EntityViewerStreamingSettings.ExternalInstallSourceMode;
        settings.BaseGameContentPath = content;
        settings.DedicatedServerModsPath = mods;
        return settings;
    }, default);

    // Returned settings cannot mutate the shared authorization snapshot.
    (await settingsStore.GetAsync(default)).StreamingEnabled = false;
    Check((await settingsStore.GetAsync(default)).StreamingEnabled, "Settings snapshots must be isolated");
    var reads = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => settingsStore.GetAsync(default)));
    Check(reads.All(settings => settings.StreamingEnabled), "Concurrent settings readers");

    var sessions = app.Services.GetRequiredService<ViewerAssetSessionStore>();
    var request = new AssetSessionRequest { Mods = [new AssetSessionModDto { RootId = "123", PublishedFileId = 123 }] };
    var session = sessions.CreateSession("alice", request);
    var resolver = app.Services.GetRequiredService<ServerAssetResolver>();
    var archiveAsset = await resolver.ResolveAsync(session,
        new AssetResolveRequest { RootId = "123", LogicalPath = "Models/mod.mwm" }, default);
    Check(archiveAsset is not null, "Archive resolves with case-insensitive paths and a top-level directory");
    await using (var stream = archiveAsset!.OpenRead())
    {
        Check(!stream.CanSeek && stream is not MemoryStream, "Archive bytes must stream lazily, without full buffering");
        await stream.ReadExactlyAsync(new byte[4]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await stream.ReadExactlyAsync(new byte[4], cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
    }
    var oldKey = archiveAsset.CacheKeyForUser("alice");
    Check(oldKey != archiveAsset.CacheKeyForUser("bob"), "Persistent cache keys must be user-specific");
    WriteArchive(archivePath, [5, 6, 7, 8]);
    var replaced = await resolver.ResolveAsync(session,
        new AssetResolveRequest { RootId = "123", LogicalPath = "Models/mod.mwm" }, default);
    Check(replaced!.Size == 4 && replaced.CacheKeyForUser("alice") != oldKey, "Archive index and cache revision must refresh on replacement");
    WriteArchive(archivePath, bytes);

    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    const string api = "/_quasar/plugins/cometworks.entityviewer/api/assets/";
    using var alice = Client("alice");
    using var bob = Client("bob");
    using var anonymous = Client("");
    Check((await anonymous.GetAsync(api + "status")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous clients cannot resolve assets");
    var aliceSession = await CreateSession(alice);
    var bobSession = await CreateSession(bob);
    var aliceAsset = await Resolve(alice, aliceSession, "Models/mod.mwm", "123");
    var bobAsset = await Resolve(bob, bobSession, "Models/mod.mwm", "123");
    Check(aliceAsset.CacheKey != bobAsset.CacheKey, "HTTP cache keys are isolated by user");
    Check((await bob.GetAsync(api + "files/" + aliceAsset.AssetToken)).StatusCode == HttpStatusCode.NotFound,
        "Another user cannot download the first user's token");
    var crossSession = await bob.PostAsJsonAsync(api + "sessions/" + aliceSession + "/resolve",
        new AssetResolveRequest { LogicalPath = "Models/block.mwm" });
    Check(crossSession.StatusCode == HttpStatusCode.NotFound, "Another user cannot resolve the first user's session");

    // Keep one large response unread while other users finish independent downloads.
    using var slow = await alice.GetAsync(api + "files/" + aliceAsset.AssetToken, HttpCompletionOption.ResponseHeadersRead);
    var elapsed = Stopwatch.StartNew();
    var downloads = new[] { (alice, aliceAsset), (bob, bobAsset) }.SelectMany(pair => Enumerable.Range(0, 4).Select(async _ =>
    {
        using var response = await pair.Item1.GetAsync(api + "files/" + pair.Item2.AssetToken);
        response.EnsureSuccessStatusCode();
        Check(response.Content.Headers.ContentLength == bytes.Length, "Archive response length");
        Check(response.Headers.CacheControl?.NoStore == true && response.Headers.CacheControl.Private, "Shared proxies must not cache assets");
        Check(SHA256.HashData(await response.Content.ReadAsByteArrayAsync()).SequenceEqual(expectedHash), "Concurrent archive responses contain identical complete bytes");
    }));
    await Task.WhenAll(downloads).WaitAsync(TimeSpan.FromSeconds(15));
    Console.WriteLine($"PASS: two users completed eight 16 MiB archive downloads in {elapsed.ElapsedMilliseconds} ms while another response remained unread.");
    slow.Dispose();

    var plain = await Resolve(alice, aliceSession, "Models/block.mwm", "");
    using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, api + "files/" + plain.AssetToken);
    rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 2);
    using var range = await alice.SendAsync(rangeRequest);
    Check(range.StatusCode == HttpStatusCode.PartialContent && (await range.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 2, 3 }), "Plain files retain range support");
    var plainAgain = await Resolve(alice, aliceSession, "Models/block.mwm", "");
    Check(plain.CacheKey == plainAgain.CacheKey && plain.AssetToken != plainAgain.AssetToken, "Fresh authorization yields a stable cache key");
    await File.WriteAllBytesAsync(Path.Combine(content, "Models", "block.mwm"), [9, 8, 7, 6, 5]);
    Check((await Resolve(alice, aliceSession, "Models/block.mwm", "")).CacheKey != plain.CacheKey, "Changed file invalidates the browser cache");

    await settingsStore.UpdateAsync(settings => { settings.StreamingEnabled = false; return settings; }, default);
    Check((await alice.GetAsync(api + "files/" + aliceAsset.AssetToken)).StatusCode == HttpStatusCode.Conflict, "Revoked streaming rejects existing file tokens");
    var disabled = await alice.PostAsJsonAsync(api + "sessions/" + aliceSession + "/resolve", new AssetResolveRequest { LogicalPath = "Models/block.mwm" });
    Check(!(await disabled.Content.ReadFromJsonAsync<AssetResolveResponse>())!.Found, "Revoked streaming cannot authorize cache reuse");
    // Out-of-band settings edits still invalidate the cached snapshot.
    var paths = app.Services.GetRequiredService<EntityViewerStreamingPaths>();
    await File.WriteAllTextAsync(paths.SettingsPath, "{}");
    Check(!(await settingsStore.GetAsync(default)).ConsentAccepted, "External settings replacement is detected");
    await app.StopAsync();
    Console.WriteLine("PASS: authorization, consent revocation, revisions, snapshot isolation, asynchronous streaming, cancellation and ranges.");

    HttpClient Client(string user)
    {
        var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
        if (user.Length > 0) client.DefaultRequestHeaders.Add("X-Test-User", user);
        return client;
    }
    async Task<string> CreateSession(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(api + "sessions", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AssetSessionResponse>())!.SessionId;
    }
    async Task<AssetResolveResponse> Resolve(HttpClient client, string id, string logicalPath, string rootId)
    {
        using var response = await client.PostAsJsonAsync(api + "sessions/" + id + "/resolve", new AssetResolveRequest { LogicalPath = logicalPath, RootId = rootId });
        response.EnsureSuccessStatusCode();
        var asset = (await response.Content.ReadFromJsonAsync<AssetResolveResponse>())!;
        Check(asset.Found, "HTTP asset resolution");
        return asset;
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void WriteArchive(string path, byte[] bytes)
{
    using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var archive = new ZipArchive(file, ZipArchiveMode.Create);
    using var stream = archive.CreateEntry("Package/MODELS/mod.mwm", CompressionLevel.Fastest).Open();
    stream.Write(bytes);
}

sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        return Task.FromResult(string.IsNullOrEmpty(user) ? AuthenticateResult.NoResult()
            : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test")), "test")));
    }
}

sealed class UnusedCompanionChannel : IQuasarCompanionChannel
{
    public Task<TResponse> SendAsync<TRequest, TResponse>(string serverId, string companionPluginId, string operation,
        TRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
}
