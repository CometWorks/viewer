using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CometWorks.EntityViewer.Magnetar.Replay;
using CometWorks.EntityViewer.Quasar;
using CometWorks.EntityViewer.Quasar.Replay;
using CometWorks.EntityViewer.Replay;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Manifests;
using Quasar.Plugin.Abstractions.Security;

var root = Path.Combine(Path.GetTempPath(), "viewer-replay-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
try
{
    var schedule = new ReplaySchedule();
    Check(schedule.Observe(Sample(0), 0, out _), "First sample must establish baseline");
    Check(!schedule.Due(999) && schedule.Due(1000), "Baseline must use one-second cadence");
    Check(!schedule.Observe(Sample(1000), 1000, out _), "Idle observations must be suppressed");
    Check(schedule.Observe(Sample(30000), 30000, out _), "Idle heartbeat must retain seekable history");
    schedule.Trigger(30010);
    Check(schedule.Due(30010), "Incident should immediately schedule detail");
    var high = Sample(30010);
    Check(schedule.Observe(high, 30010, out _) && high.High, "Transition to incident detail must be recorded even when stationary");
    Check(!schedule.Due(30209) && schedule.Due(30210), "Incident must use 200 ms cadence");
    Check(!schedule.IsHigh(45010), "Incident must expire after quiet tail");
    var low = Sample(45010);
    Check(schedule.Observe(low, 45010, out _) && !low.High, "Transition back to baseline must be recorded");
    var detector = new ReplaySchedule();
    var spinning = Sample(0); spinning.AngularVelocity = [6, 0, 0];
    detector.Observe(spinning, 0, out var first);
    spinning = Sample(1000); spinning.AngularVelocity = [6, 0, 0];
    detector.Observe(spinning, 1000, out var second);
    Check(!first && second, "Physics detector must require consecutive abnormal observations");

    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var batch = new ReplayBatch { Session = Guid.NewGuid().ToString("N"), World = "world", Records = [new ReplayRecord { At = now - 2000, Kind = "health", Value = 1 }, new ReplayRecord { At = now - 2000, Kind = "sample", Entity = "9223372036854775807", Position = [1, 2, 3] }] };
    var chunk = new ReplayChunk { Id = ReplayChunkStore.NewId(now - 2000, 0), Data = ReplayChunkStore.Compress(JsonSerializer.SerializeToUtf8Bytes(batch, json)) };
    var spoolPath = Path.Combine(root, "spool");
    var spool = new ReplayChunkStore(spoolPath, 8192);
    spool.Put(chunk, now); spool.Put(chunk, now);
    Check(spool.ReadAndAcknowledge(new(), now).Chunks.Count == 1, "Retries must not duplicate immutable chunks");
    var reopened = new ReplayChunkStore(spoolPath, 8192);
    Check(reopened.ReadAndAcknowledge(new(), now).Chunks.Single().Data.SequenceEqual(chunk.Data), "Unacknowledged segments survive restart");
    Check(reopened.ReadAndAcknowledge(new() { Acknowledge = [chunk.Id] }, now).Chunks.Count == 0, "Acknowledged segments leave spool");
    Throws(() => reopened.ReadAndAcknowledge(new() { Acknowledge = ["../escape"] }, now), "Acknowledgement path validation");
    for (var i = 0; i < 3; i++) reopened.Put(new ReplayChunk { Id = ReplayChunkStore.NewId(now - 1000 + i, 0), Data = chunk.Data }, now);
    Check(reopened.List(now).Length == 2 && reopened.LostChunks == 1, "Disk quota must evict oldest data and count lost coverage");
    reopened = new ReplayChunkStore(spoolPath, 8192);
    Check(reopened.LostChunks == 1, "Storage loss survives restart");
    reopened.Prune(now + ReplayChunkStore.RetentionMs + 1);
    Check(reopened.List(now + ReplayChunkStore.RetentionMs + 1).Length == 0, "24-hour retention must remove expired data");
    Throws(() => reopened.Put(chunk, now + ReplayChunkStore.RetentionMs + 1), "Expired segments cannot be reintroduced");
    Check(ReplayChunkStore.Expand(chunk.Data).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(batch, json)), "Compression round trip");

    var writerPath = Path.Combine(root, "writer");
    var writer = new ReplayWriter(writerPath, batch.Session, "world");
    var damageAt = now - now % 250;
    writer.Add(new ReplayRecord { At = damageAt, Kind = "damage", Entity = "grid", Source = "attacker", Detail = "Bullet cell (1,2,3)", Value = 10, Count = 1 });
    writer.Add(new ReplayRecord { At = damageAt + 1, Kind = "damage", Entity = "grid", Source = "attacker", Detail = "Bullet cell (1,2,3)", Value = 20, Count = 1 });
    writer.Dispose();
    var replacement = new ReplayWriter(writerPath, Guid.NewGuid().ToString("N"), "world", writer);
    replacement.Add(new ReplayRecord { At = now, Kind = "health", Value = 1 }); replacement.Dispose();
    await replacement.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    var written = replacement.Read(new ReplayReadRequest()).Chunks.Select(c => JsonSerializer.Deserialize<ReplayBatch>(ReplayChunkStore.Expand(c.Data), json)!).ToArray();
    var aggregated = written.SelectMany(b => b.Records).Single(r => r.Kind == "damage");
    Check(aggregated.Value == 30 && aggregated.Count == 2, "Real writer must coalesce matching damage and preserve its count/total");
    Check(written.Length == 2 && written.Select(b => b.Session).Distinct().Count() == 2, "Fast world reload must drain both writers without mixing sessions or deleting pending segments");

    await GeometryChecks.Run(root, now);
    var page = ReplayChunkStore.Compress(JsonSerializer.SerializeToUtf8Bytes(new[] { GeometryChecks.Block(0) }, json));
    var pageId = ReplayChunkStore.GeometryId(page);
    var manifest = ReplayChunkStore.Compress(JsonSerializer.SerializeToUtf8Bytes(new ReplayLayoutPage { Pages = [pageId] }, json));
    var manifestId = ReplayChunkStore.GeometryId(manifest);
    batch.Records[1].LayoutId = manifestId; batch.Records[1].GridSize = 2.5; batch.Records[1].Origin = [0, 0, 0];
    var transportChunk = new ReplayChunk { Id = chunk.Id, Data = ReplayChunkStore.Compress(JsonSerializer.SerializeToUtf8Bytes(batch, json)), Geometry = new() { [pageId] = page, [manifestId] = manifest } };

    var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
    var context = new QuasarPluginContext
    {
        InstallDirectory = Path.Combine(root, "quasar"), PluginDirectory = root, CacheDirectory = root,
        Configuration = builder.Configuration, Environment = builder.Environment,
        Manifest = new QuasarPluginManifest { Id = "cometworks.entityviewer", DisplayName = "Viewer", Version = "test", EntryAssembly = "test", EntryType = "test", ProjectPath = "test" }
    };
    var channel = new TestChannel(transportChunk);
    var plugin = new EntityViewerQuasarPlugin(); plugin.ConfigureServices(builder.Services, context);
    builder.Services.RemoveAll<IHostedService>();
    builder.Services.AddSingleton<IQuasarCompanionChannel>(channel);
    builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { });
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy(QuasarPolicyNames.CanView, policy => policy.RequireAuthenticatedUser());
        options.AddPolicy(QuasarPolicyNames.CanManageSecurity, policy => policy.RequireClaim(ClaimTypes.NameIdentifier, "admin"));
    });
    await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); plugin.ConfigureEndpoints(app, context);
    await app.StartAsync();
    var history = app.Services.GetRequiredService<ReplayHistoryService>();
    await history.StartAsync(default);
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!channel.Acknowledged && DateTime.UtcNow < deadline) await Task.Delay(50);
    Check(channel.Acknowledged, "Background collector must durably ingest and acknowledge without a browser");
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    const string api = "/_quasar/plugins/cometworks.entityviewer/api/replay/";
    Check((await client.GetAsync(api + "servers")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous history denied");
    client.DefaultRequestHeaders.Add("X-Test-User", "viewer");
    Check((await client.GetAsync(api + "server?from=" + (now - 3000) + "&to=" + now)).StatusCode == HttpStatusCode.Forbidden, "Ordinary viewers cannot access player history directly");
    client.DefaultRequestHeaders.Remove("X-Test-User"); client.DefaultRequestHeaders.Add("X-Test-User", "admin");
    var response = await client.GetAsync(api + "agent?from=" + (now - 3000) + "&to=" + now);
    Check(response.IsSuccessStatusCode && response.Headers.CacheControl!.NoStore, "Admin history works through current agent id and cannot be cached");
    var stored = await response.Content.ReadFromJsonAsync<List<ReplayBatch>>(json);
    Check(stored!.SelectMany(b => b.Geometry).Single().Value.Single().Definition.EndsWith("LargeBlockArmorBlock"), "Admin queries must include historical geometry dependencies");
    Check(stored!.SelectMany(b => b.Records).Single(r => r.Kind == "sample").Entity == "9223372036854775807", "Entity IDs remain lossless");
    Check((await client.GetAsync(api + "server?from=" + (now - 16 * 60000) + "&to=" + now)).StatusCode == HttpStatusCode.BadRequest, "Large queries must be bounded");
    await history.StopAsync(default);
    channel.Connected = false;
    using var restarted = new ReplayHistoryService(context, channel, app.Services.GetRequiredService<ILogger<ReplayHistoryService>>());
    await restarted.StartAsync(default);
    deadline = DateTime.UtcNow.AddSeconds(5);
    while (restarted.Servers().Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
    Check((await restarted.Query("server", now - 3000, now, default)).Count == 1, "Retained history survives Quasar restart and server disconnect");
    await restarted.StopAsync(default); await app.StopAsync();

    // A deterministic size/CPU reference; this is not a live dedicated-server benchmark.
    var records = new List<ReplayRecord>();
    var sampling = new ReplaySchedule[100];
    for (var i = 0; i < sampling.Length; i++) sampling[i] = new ReplaySchedule();
    var stopwatch = Stopwatch.StartNew();
    for (var tick = 0; tick < 60; tick++)
        for (var i = 0; i < sampling.Length; i++)
        {
            var sample = Sample(tick * 1000); sample.At = now + tick * 1000; sample.Entity = i.ToString();
            if (sampling[i].Observe(sample, tick * 1000, out _)) records.Add(sample);
        }
    stopwatch.Stop();
    var bytes = JsonSerializer.SerializeToUtf8Bytes(new ReplayBatch { Session = batch.Session, Records = records }, json);
    Console.WriteLine($"PASS: cadence, hysteresis, detector, compression, bounded spool, deduplication, restart, retention, background collection, authorization, offline playback. Idle synthetic sample: {records.Count}/6000 observations persisted; {stopwatch.Elapsed.TotalMilliseconds:F2} ms scheduling; {ReplayChunkStore.Compress(bytes).Length} compressed bytes per 100-grid minute (excluding filesystem/segment overhead).");
}
finally { Directory.Delete(root, true); }

static ReplayRecord Sample(double elapsed) => new() { Elapsed = elapsed, Position = [0, 0, 0], Rotation = [0, 0, 0, 1], Size = [10, 10, 10], Velocity = [0, 0, 0], AngularVelocity = [0, 0, 0] };
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Throws(Action action, string message) { try { action(); } catch (InvalidDataException) { return; } throw new Exception(message); }

sealed class TestChannel(ReplayChunk chunk) : IQuasarCompanionChannel
{
    public bool Acknowledged;
    public bool Connected = true;
    public IReadOnlyList<QuasarCompanionServer> GetConnectedServers() => Connected ? [new("server", "agent", "Test server")] : [];
    public Task<TResponse> SendAsync<TRequest, TResponse>(string serverId, string companionPluginId, string operation, TRequest request, CancellationToken token)
    {
        if (serverId != "agent") throw new Exception("Collection must target the exact live agent, not a shared cluster name");
        var read = (ReplayReadRequest)(object)request!;
        if (read.Acknowledge.Contains(chunk.Id)) Acknowledged = true;
        return Task.FromResult((TResponse)(object)new ReplayPage { Chunks = Acknowledged ? [] : [chunk] });
    }
}
sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        return Task.FromResult(user.Length == 0 ? AuthenticateResult.NoResult() : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test")), "test")));
    }
}
