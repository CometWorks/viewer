using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using CometWorks.EntityViewer.Quasar;
using CometWorks.EntityViewer.Quasar.Streaming;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Quasar.Hosting;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Manifests;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;

// Actual Viewer + core SDK + native peer, behind an isolated real Caddy boundary.
// No Quasar application service or game process is started.
const string prefix = "/direct-panel";
const string alice = "76561198000000001", bob = "76561198000000002";
var key = Guid.NewGuid().ToString("N");
var root = Path.Combine(Path.GetTempPath(), "viewer-rtc-" + key);
Directory.CreateDirectory(root);
Process? proxy = null;
try
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
    var context = new QuasarPluginContext { InstallDirectory = root, PluginDirectory = root, CacheDirectory = root,
        Configuration = builder.Configuration, Environment = builder.Environment,
        Manifest = new QuasarPluginManifest { Id = "cometworks.entityviewer", DisplayName = "Viewer", Version = "test", EntryAssembly = "test", EntryType = "test", ProjectPath = "test" } };
    var plugin = new EntityViewerQuasarPlugin(); plugin.ConfigureServices(builder.Services, context);
    using var sdk = new QuasarSdkFixture(builder.Services, Path.Combine(root, "quasar"));
    builder.Services.RemoveAll<IHostedService>();
    builder.Services.AddSingleton<IQuasarCompanionChannel, Companion>();
    builder.Services.AddAuthentication("test").AddCookie("test", options => {
        options.Events.OnRedirectToLogin = c => QuasarSdkEndpoints.WriteAuthorizationErrorAsync(c.HttpContext, 401);
        options.Events.OnRedirectToAccessDenied = c => QuasarSdkEndpoints.WriteAuthorizationErrorAsync(c.HttpContext, 403);
    });
    builder.Services.AddAuthorization(options => typeof(Quasar.Program).GetMethod("ConfigureAuthorizationPolicies", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [options]));
    builder.Services.AddSingleton<IAuthorizationPolicyProvider, QuasarAuthorizationPolicyProvider>();
    builder.Services.AddQuasarRtc(builder.Configuration);
    builder.Services.PostConfigure<QuasarRtcOptions>(options => { options.StunUrls = []; options.PeerExecutable = Environment.GetEnvironmentVariable("QUASAR_RTC_TEST_PEER")!; });
    var manager = Environment.GetEnvironmentVariable("NEBULA_RTC_TEST_MANAGER");
    builder.Services.AddHttpClient("Nebula").ConfigurePrimaryHttpMessageHandler(() => manager is null ? new Leases() : new FixtureManager(new Uri(manager)));
    await HostedPanelState.SaveAsync(new HostedPanelState { Choice = "hosted", PanelId = prefix[1..], Credential = Environment.GetEnvironmentVariable("NEBULA_RTC_TEST_CREDENTIAL") ?? prefix[1..] + "." + new string('a', 64) });
    await using var app = builder.Build();
    await app.Services.GetRequiredService<RbacConfigCatalog>().UpdateAsync(config => {
        config.SubjectRoleMappings = [new() { Subject = alice, Roles = [QuasarRoles.Admin] }, new() { Subject = bob, Roles = [QuasarRoles.Viewer] }]; return Task.CompletedTask;
    });
    var hosted = typeof(Quasar.Program).Assembly.GetType("Quasar.Services.HostedPanelRequest")!.GetMethod("InvokeAsync")!
        .CreateDelegate<Func<HttpContext, RequestDelegate, QuasarAuthOptions, Task>>();
    app.Use((http, next) => hosted(http, next, new QuasarAuthOptions()));
    app.UsePathBase(prefix);
    var quasarWeb = Path.GetFullPath("../../../../../../quasar/Quasar/wwwroot", AppContext.BaseDirectory);
    // Explicit paths work for a separate runtime checkout too.
    var runtimeWeb = Environment.GetEnvironmentVariable("QUASAR_RTC_TEST_WEBROOT") ?? quasarWeb;
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(runtimeWeb) });
    app.Use(async (http, next) => {
        if (manager is not null && http.Request.Path == "/_quasar/sdk/v1/rtc/attempts" && http.Request.Method == "POST")
        {
            http.Request.EnableBuffering();
            using var offer = await JsonDocument.ParseAsync(http.Request.Body);
            http.Request.Body.Position = 0;
            if (!offer.RootElement.TryGetProperty("relayTicket", out _)) { http.Response.StatusCode = 503; return; }
        }
        await next(http);
    });
    app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
    app.MapQuasarSdk(); app.MapQuasarRtc(); plugin.ConfigureEndpoints(app, context);
    app.MapGet("/", () => Results.Content("<!doctype html><title>Viewer direct fixture</title>", "text/html"));
    var viewerWeb = Path.GetFullPath("../../../../../src/CometWorks.EntityViewer/wwwroot", AppContext.BaseDirectory);
    app.UseStaticFiles(new StaticFileOptions { RequestPath = "/_quasar/plugins/cometworks.entityviewer", FileProvider = new PhysicalFileProvider(viewerWeb) });
    var fixture = app.MapGroup("/fixture");
    fixture.AddEndpointFilter(async (c, next) => c.HttpContext.Request.Headers["X-Fixture-Key"] == key ? await next(c) : Results.NotFound());
    fixture.MapPost("/login", async (HttpContext http, QuasarRoleMapper roles) => {
        await http.SignInAsync("test", roles.CreateSteamPrincipal(http.Request.Query["user"] == "bob" ? bob : alice),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }); return Results.NoContent();
    });
    fixture.MapPost("/reset-consent", (IEntityViewerStreamingSettingsStore settings) => settings.UpdateAsync(current => { current.StreamingEnabled = current.ConsentAccepted = false; return current; }, default));
    fixture.MapPost("/logout", async (HttpContext http, QuasarBrowserSessions sessions) => { await sessions.RevokeAsync(http); await http.SignOutAsync("test"); return Results.NoContent(); });
    fixture.MapPost("/expire", (QuasarBrowserSessions sessions, HttpContext http) => sessions.RevokeAsync(http));
    var content = Path.Combine(root, "Content"); var mods = Path.Combine(root, "Mods");
    foreach (var dir in new[] { "Data", "Models", "Textures" }) Directory.CreateDirectory(Path.Combine(content, dir));
    Directory.CreateDirectory(mods);
    await File.WriteAllBytesAsync(Path.Combine(content, "Models", "block.mwm"), [1,2,3,4]);
    using (var zip = ZipFile.Open(Path.Combine(mods, "123.sbm"), ZipArchiveMode.Create)) {
        await using var entry = zip.CreateEntry("Package/Models/mod.mwm", CompressionLevel.Fastest).Open();
        var bytes = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
        for (var i = 0; i < 256; i++) await entry.WriteAsync(bytes);
    }
    await app.Services.GetRequiredService<IEntityViewerStreamingSettingsStore>().UpdateAsync(settings => {
        settings.BaseGameSourceMode = EntityViewerStreamingSettings.ExternalInstallSourceMode;
        settings.BaseGameContentPath = content; settings.DedicatedServerModsPath = mods; return settings;
    }, default);
    await app.StartAsync();
    var upstream = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var port = new TcpListener(IPAddress.Loopback, 0); port.Start(); var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
    var log = Path.Combine(root, "access.jsonl"); var config = Path.Combine(root, "Caddyfile");
    await File.WriteAllTextAsync(config, $$"""
        { 
            admin off
            auto_https off
            persist_config off
        }
        http://127.0.0.1:{{number}} {
            log {
                output file {{log}}
                format json
            }
            reverse_proxy {{upstream}} {
                header_up X-Quasar-Hosted {{HostedPanelState.Read()!.LocalProxySecret}}
            }
        }
        """);
    proxy = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("NEBULA_CADDY")!) {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        ArgumentList = { "run", "--config", config, "--adapter", "caddyfile" } })!;
    var stdout = proxy.StandardOutput.BaseStream.CopyToAsync(Stream.Null); var stderr = proxy.StandardError.BaseStream.CopyToAsync(Stream.Null);
    using var http = new HttpClient();
    for (var i = 0; ; i++) {
        try { if ((await http.GetAsync($"http://127.0.0.1:{number}{prefix}/")).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
        if (i == 100 || proxy.HasExited) throw new IOException("Caddy fixture did not start."); await Task.Delay(50);
    }
    var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("--test"); start.ArgumentList.Add(Path.GetFullPath("../../../../../tests/DirectChecks/browser.test.mjs", AppContext.BaseDirectory));
    start.Environment["VIEWER_RTC_URL"] = $"http://127.0.0.1:{number}{prefix}/"; start.Environment["VIEWER_RTC_KEY"] = key; start.Environment["VIEWER_RTC_ACCESS_LOG"] = log;
    using var node = Process.Start(start)!; var output = node.StandardOutput.ReadToEndAsync(); var error = node.StandardError.ReadToEndAsync();
    try { await node.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4)); }
    finally { if (!node.HasExited) { node.Kill(entireProcessTree: true); await node.WaitForExitAsync(); } }
    Console.Write(await output); Console.Error.Write(await error);
    if (node.ExitCode != 0) throw new Exception("Viewer direct browser checks failed.");
}
finally { if (proxy is { HasExited: false }) { proxy.Kill(true); await proxy.WaitForExitAsync(); } proxy?.Dispose(); try { Directory.Delete(root, true); } catch (IOException) { } }

sealed class Companion : IQuasarCompanionChannel
{
    public Task<TResponse> SendAsync<TRequest, TResponse>(string serverId, string plugin, string operation, TRequest request, CancellationToken token)
        => Task.FromResult((TResponse)(object)JsonSerializer.SerializeToElement(new { request, large = new string('x', 150000), mods = new[] { new { rootId = "123", publishedFileId = 123 } } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}
sealed class Leases : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
        return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.OK) {
            Content = JsonContent.Create(new { id = path.EndsWith("/leases") ? Guid.NewGuid().ToString("N") : path.Split('/')[^2], version = 1,
                grant = path.EndsWith("/leases") ? new string('A',64) : null, validForSeconds = path.EndsWith("/leases") ? 30 : 120 }) });
    }
}

sealed class FixtureManager(Uri manager) : DelegatingHandler(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        request.RequestUri = new Uri(manager, request.RequestUri!.PathAndQuery);
        return base.SendAsync(request, token);
    }
}
