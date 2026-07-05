using CometWorks.EntityViewer.Components;
using CometWorks.EntityViewer.Services;
using Microsoft.Extensions.FileProviders;
using MudBlazor.Services;
using PreviewHost.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddEntityViewerUi();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
UseViewerVendorAssets(app);
app.UseAntiforgery();

app.MapGet("/_content/CometWorks.EntityViewer/api/entities/{agentId}/{entityId:long}/scene", () =>
    Results.NotFound("Preview host does not provide live Quasar scene data."));
app.MapGet("/_content/CometWorks.EntityViewer/api/assets/status", () => Results.Json(new
{
    mode = "local",
    streamingEnabled = false,
    consentAccepted = false,
    consentRequired = false,
    consentVersion = "server-asset-streaming-v1",
    canManageStreaming = false,
    fileStreamingReady = false,
    baseGameSourceMode = "ManagedSteamCmd",
    baseGameContentConfigured = false,
    managedGameContentExists = false,
    lastInstallStatus = "NotStarted",
    message = "Preview host uses local asset folders.",
}));
app.MapGet("/_content/CometWorks.EntityViewer/api/assets/installer/status", () => Results.Json(new
{
    state = "Idle",
    isRunning = false,
    message = "Preview host does not run SteamCMD.",
    steamCmdPath = "",
    installDirectory = "",
    contentDirectory = "",
    loginName = "",
    validate = true,
    exitCode = (int?)null,
    startedAtUtc = (DateTimeOffset?)null,
    completedAtUtc = (DateTimeOffset?)null,
    lastSequence = 0,
    log = Array.Empty<object>(),
}));
app.MapPost("/_content/CometWorks.EntityViewer/api/assets/installer/start", () =>
    Results.BadRequest(new { error = "Preview host does not run SteamCMD." }));
app.MapPost("/_content/CometWorks.EntityViewer/api/assets/installer/input", () =>
    Results.BadRequest(new { error = "Preview host does not run SteamCMD." }));
app.MapPost("/_content/CometWorks.EntityViewer/api/assets/installer/cancel", () =>
    Results.Json(new { state = "Idle", isRunning = false, message = "Preview host does not run SteamCMD." }));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(EntityViewerColumnCell).Assembly);

app.Run();

static void UseViewerVendorAssets(WebApplication app)
{
    var repositoryRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", ".."));
    UseStaticDirectory(app, Path.Combine(repositoryRoot, "node_modules", "three"), "/vendor/three");
    UseStaticDirectory(app, Path.Combine(repositoryRoot, "node_modules", "@zip.js", "zip.js"), "/vendor/zip.js");
}

static void UseStaticDirectory(WebApplication app, string path, string requestPath)
{
    if (!Directory.Exists(path))
        return;

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(path),
        RequestPath = requestPath,
    });
}
