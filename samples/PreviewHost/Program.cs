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

app.MapGet("/api/viewer/entities/{agentId}/{entityId:long}/scene", () =>
    Results.NotFound("Preview host does not provide live Quasar scene data."));

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
