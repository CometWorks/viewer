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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
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
    builder.Services.AddSingleton<IQuasarCompanionChannel, TestCompanionChannel>();
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
    Directory.CreateDirectory(Path.Combine(mods, "Hidden", "Models"));
    await File.WriteAllTextAsync(Path.Combine(mods, "Hidden", "Models", "secret.mwm"), "hidden");
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
    AssetSessionModDto[] approvedMods = [new() { RootId = "123", PublishedFileId = 123 }];
    var session = sessions.CreateSession("alice", "server", "42", approvedMods);
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

    var oversized = Path.Combine(content, "Models", "oversized.mwm");
    await using (var file = new FileStream(oversized, FileMode.Create, FileAccess.Write))
        file.SetLength(AssetStreamingLimits.MaxAssetBytes + 1);
    Check(await resolver.ResolveAsync(session, new AssetResolveRequest { LogicalPath = "Models/oversized.mwm" }, default) is null,
        "Oversized loose assets are rejected before hashing");

    var limitStore = new ViewerAssetSessionStore();
    var oldest = limitStore.CreateSession("limited", "server", "1", []);
    for (var i = 1; i < AssetStreamingLimits.MaxSessionsPerUser; i++)
        limitStore.CreateSession("limited", "server", i.ToString(), []);
    Check(limitStore.TryGetSession(oldest.Id, "limited") is not null, "Session cap retains active sessions up to the limit");
    limitStore.CreateSession("limited", "server", "overflow", []);
    Check(limitStore.TryGetSession(oldest.Id, "limited") is null, "Session cap evicts the oldest per-user session");

    var boundedSession = limitStore.CreateSession("bounded", "server", "1", []);
    Check(limitStore.TryBeginResolve(boundedSession, out var resolveOne), "First per-session resolve lease");
    Check(limitStore.TryBeginResolve(boundedSession, out var resolveTwo), "Second per-session resolve lease");
    Check(!limitStore.TryBeginResolve(boundedSession, out _), "Per-session resolve concurrency is bounded");
    resolveOne!.Dispose();
    resolveTwo!.Dispose();
    var globalResolveLeases = new List<IDisposable>();
    for (var i = 0; i < AssetStreamingLimits.MaxConcurrentResolves; i++)
    {
        var globalSession = limitStore.CreateSession("global-" + i, "server", "1", []);
        Check(limitStore.TryBeginResolve(globalSession, out var globalLease), "Global resolve concurrency budget before limit");
        globalResolveLeases.Add(globalLease!);
    }
    var excessGlobalSession = limitStore.CreateSession("global-overflow", "server", "1", []);
    Check(!limitStore.TryBeginResolve(excessGlobalSession, out _), "Global resolve concurrency is bounded");
    foreach (var globalLease in globalResolveLeases) globalLease.Dispose();
    var rateSession = limitStore.CreateSession("rate", "server", "1", []);
    for (var i = 0; i < AssetStreamingLimits.MaxResolveRequestsPerMinute; i++)
    {
        Check(limitStore.TryBeginResolve(rateSession, out var resolveLease), "Resolve rate budget before limit");
        resolveLease!.Dispose();
    }
    Check(!limitStore.TryBeginResolve(rateSession, out _), "Per-session resolve rate is bounded");
    var secondRateSession = limitStore.CreateSession("rate", "server", "2", []);
    for (var i = AssetStreamingLimits.MaxResolveRequestsPerMinute;
         i < AssetStreamingLimits.MaxResolveRequestsPerUserPerMinute; i++)
    {
        Check(limitStore.TryBeginResolve(secondRateSession, out var resolveLease), "Per-user resolve rate budget before limit");
        resolveLease!.Dispose();
    }
    var thirdRateSession = limitStore.CreateSession("rate", "server", "3", []);
    Check(!limitStore.TryBeginResolve(thirdRateSession, out _), "Per-user resolve rate is bounded across sessions");

    var budgetSession = limitStore.CreateSession("budget", "server", "1", []);
    var budgetAsset = new ResolvedServerAsset { Size = AssetStreamingLimits.MaxAssetBytes };
    var budgetToken = limitStore.CreateAssetToken("budget", budgetSession.Id, budgetAsset)!;
    var budgetResponses = (int)(AssetStreamingLimits.MaxSessionResponseBytes / AssetStreamingLimits.MaxAssetBytes);
    for (var i = 0; i < budgetResponses; i++)
    {
        Check(limitStore.TryBeginStream(budgetToken, out var streamLease), "Response byte budget before limit");
        streamLease!.Dispose();
    }
    Check(!limitStore.TryBeginStream(budgetToken, out _), "Per-session response bytes are bounded");

    var tokenSession = limitStore.CreateSession("tokens", "server", "1", []);
    var firstToken = limitStore.CreateAssetToken("tokens", tokenSession.Id, new ResolvedServerAsset { Size = 1 })!;
    for (var i = 1; i <= AssetStreamingLimits.MaxTokensPerSession; i++)
        limitStore.CreateAssetToken("tokens", tokenSession.Id, new ResolvedServerAsset { Size = 1 });
    Check(limitStore.TryGetAssetToken(firstToken.Id, "tokens") is null, "Token cap evicts the oldest per-session token");

    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    AuditViewerEndpoints(app);
    const string api = "/_quasar/plugins/cometworks.entityviewer/api/assets/";
    using var alice = Client("alice");
    using var bob = Client("bob");
    using var anonymous = Client("");
    await AssertAnonymousEndpointsDenied(anonymous);
    Check((await bob.GetAsync("/_content/CometWorks.EntityViewer/api/replay/servers")).StatusCode == HttpStatusCode.Forbidden,
        "Ordinary viewers cannot inspect replay history through the static-assets prefix");

    var bobStatus = (await bob.GetFromJsonAsync<AssetStreamingStatusResponse>(api + "status"))!;
    Check(!bobStatus.CanManageStreaming && bobStatus.ActiveBaseGameContentDirectory.Length == 0 &&
          !bobStatus.BaseGameContentMessage.Contains(content, StringComparison.Ordinal) &&
          !bobStatus.Message.Contains(content, StringComparison.Ordinal),
        "Viewer status redacts host filesystem paths");
    var aliceStatus = (await alice.GetFromJsonAsync<AssetStreamingStatusResponse>(api + "status"))!;
    Check(aliceStatus.CanManageStreaming && Path.GetFullPath(aliceStatus.ActiveBaseGameContentDirectory) == Path.GetFullPath(content),
        "Streaming administrators retain path diagnostics");

    using var removedSessionEndpoint = await alice.PostAsJsonAsync(api + "sessions", new
    {
        agentId = "server",
        entityId = "42",
        mods = new[] { new { rootId = "Hidden", name = "Hidden" } },
    });
    Check(removedSessionEndpoint.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
        "Browsers cannot create sessions or choose authorized mod roots");

    var aliceSession = await IssueSceneSession(alice, "/_quasar/plugins/cometworks.entityviewer");
    var bobSession = await IssueSceneSession(bob, "/_content/CometWorks.EntityViewer");
    using (var guessed = await bob.PostAsJsonAsync(api + "sessions/" + bobSession + "/resolve",
        new AssetResolveRequest { RootId = "Hidden", LogicalPath = "Models/secret.mwm" }))
    {
        guessed.EnsureSuccessStatusCode();
        Check(!(await guessed.Content.ReadFromJsonAsync<AssetResolveResponse>())!.Found,
            "A viewer cannot guess an installed mod that was absent from the companion scene");
    }
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
    var downloads = new[] { (alice, aliceAsset), (bob, bobAsset) }.SelectMany(pair => Enumerable.Range(0, 3).Select(async _ =>
    {
        using var response = await pair.Item1.GetAsync(api + "files/" + pair.Item2.AssetToken);
        response.EnsureSuccessStatusCode();
        Check(response.Content.Headers.ContentLength == bytes.Length, "Archive response length");
        Check(response.Headers.CacheControl?.NoStore == true && response.Headers.CacheControl.Private, "Shared proxies must not cache assets");
        Check(SHA256.HashData(await response.Content.ReadAsByteArrayAsync()).SequenceEqual(expectedHash), "Concurrent archive responses contain identical complete bytes");
    }));
    await Task.WhenAll(downloads).WaitAsync(TimeSpan.FromSeconds(15));
    Console.WriteLine($"PASS: two users completed six 16 MiB archive downloads in {elapsed.ElapsedMilliseconds} ms while another response remained unread.");
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

    var revision = await Resolve(alice, aliceSession, "Models/block.mwm", "");
    var source = Path.Combine(content, "Models", "block.mwm");
    var stamp = File.GetLastWriteTimeUtc(source);
    await File.WriteAllBytesAsync(source, [5, 6, 7, 8, 9]); File.SetLastWriteTimeUtc(source, stamp);
    var replacedSameMetadata = await Resolve(alice, aliceSession, "Models/block.mwm", "");
    Check(replacedSameMetadata.ContentHash.Length == 64 && replacedSameMetadata.CacheKey != revision.CacheKey, "Same-size/same-timestamp replacements must have a different content revision");

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
    async Task<string> IssueSceneSession(HttpClient client, string routePrefix)
    {
        using var response = await client.GetAsync(routePrefix + "/api/entities/server/42/scene");
        response.EnsureSuccessStatusCode();
        Check(response.Headers.CacheControl?.NoStore == true && response.Headers.CacheControl.Private,
            "Scene responses that issue asset sessions cannot enter shared caches");
        Check(response.Headers.TryGetValues("X-Entity-Viewer-Asset-Session", out var values),
            "Companion-produced scenes issue an opaque asset session");
        return values!.Single();
    }

    async Task AssertAnonymousEndpointsDenied(HttpClient client)
    {
        foreach (var routePrefix in new[] { "/_quasar/plugins/cometworks.entityviewer", "/_content/CometWorks.EntityViewer" })
        {
            foreach (var (method, template) in ViewerApiRoutes())
            {
                var path = routePrefix + template
                    .Replace("{serverId}", "server", StringComparison.Ordinal)
                    .Replace("{entityId:long}", "42", StringComparison.Ordinal)
                    .Replace("{sessionId}", "session", StringComparison.Ordinal)
                    .Replace("{assetToken}", "token", StringComparison.Ordinal);
                if (template == "/api/replay/{serverId}")
                    path += "?from=1&to=2";
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method is "POST" or "PUT")
                    request.Content = JsonContent.Create(new { });
                using var response = await client.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.Unauthorized,
                    $"Anonymous request must be denied: {method} {path}");
            }
        }
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

static (string Method, string Template)[] ViewerApiRoutes() =>
[
    ("GET", "/api/replay/servers"),
    ("GET", "/api/replay/{serverId}/overview"),
    ("PUT", "/api/replay/{serverId}/recording"),
    ("GET", "/api/replay/{serverId}"),
    ("GET", "/api/entities/{serverId}/{entityId:long}/scene"),
    ("GET", "/api/assets/status"),
    ("POST", "/api/assets/settings/consent"),
    ("GET", "/api/assets/settings/roots"),
    ("POST", "/api/assets/settings/roots"),
    ("GET", "/api/assets/installer/status"),
    ("POST", "/api/assets/installer/start"),
    ("POST", "/api/assets/installer/input"),
    ("POST", "/api/assets/installer/cancel"),
    ("POST", "/api/assets/sessions/{sessionId}/resolve"),
    ("GET", "/api/assets/files/{assetToken}"),
];

static void AuditViewerEndpoints(WebApplication app)
{
    var prefixes = new[] { "/_quasar/plugins/cometworks.entityviewer", "/_content/CometWorks.EntityViewer" };
    var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
        .OfType<RouteEndpoint>()
        .Where(endpoint => prefixes.Any(prefix =>
            endpoint.RoutePattern.RawText?.StartsWith(prefix + "/api/", StringComparison.Ordinal) == true))
        .ToArray();

    foreach (var prefix in prefixes)
    {
        var actual = endpoints
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith(prefix, StringComparison.Ordinal))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => (method, Template: endpoint.RoutePattern.RawText![prefix.Length..])))
            .OrderBy(route => route.method, StringComparer.Ordinal)
            .ThenBy(route => route.Template, StringComparer.Ordinal)
            .ToArray();
        var expected = ViewerApiRoutes()
            .OrderBy(route => route.Method, StringComparer.Ordinal)
            .ThenBy(route => route.Template, StringComparer.Ordinal)
            .ToArray();
        Check(actual.SequenceEqual(expected), $"Complete Viewer API endpoint inventory for {prefix}");
    }

    foreach (var endpoint in endpoints)
    {
        var route = endpoint.RoutePattern.RawText!;
        var relative = prefixes.Select(prefix => route.StartsWith(prefix, StringComparison.Ordinal) ? route[prefix.Length..] : null)
            .First(value => value is not null)!;
        var expectedPolicy = relative.StartsWith("/api/replay", StringComparison.Ordinal) ||
                             relative.StartsWith("/api/assets/settings/", StringComparison.Ordinal) ||
                             relative.StartsWith("/api/assets/installer/", StringComparison.Ordinal)
            ? QuasarPolicyNames.CanManageSecurity
            : QuasarPolicyNames.CanView;
        var authorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        Check(authorization.Any(data => data.Policy == expectedPolicy), $"{route} requires {expectedPolicy}");
        Check(authorization.All(data => string.IsNullOrWhiteSpace(data.AuthenticationSchemes)),
            $"{route} inherits Quasar's default authentication scheme");
        Check(endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null, $"{route} cannot override Quasar authorization anonymously");
    }
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

sealed class TestCompanionChannel : IQuasarCompanionChannel
{
    public Task<TResponse> SendAsync<TRequest, TResponse>(string serverId, string companionPluginId, string operation,
        TRequest request, CancellationToken cancellationToken)
    {
        if (companionPluginId != "cometworks.entityviewer" || operation != "get-entity-scene")
            throw new InvalidOperationException("Only the scene operation is expected in streaming checks.");
        var scene = JsonSerializer.Deserialize<JsonElement>("""
            {
              "grid": { "id": "42" },
              "mods": [
                { "rootId": "123", "publishedFileId": 123 }
              ]
            }
            """);
        return Task.FromResult((TResponse)(object)scene);
    }
}
