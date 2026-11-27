using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using CometWorks.EntityViewer.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Quasar.Ui;
using Xunit;

namespace Viewer.UiChecks;

public sealed class ViewerUiTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/my-server-panel/")]
    public async Task ColumnOpensViewerThroughQuasarDialogHost(string prefix)
    {
        await using var context = new BunitContext();
        context.Services.AddSingleton<NavigationManager>(new PanelNavigation(prefix));
        var js = new ViewerJs();
        context.Services.AddSingleton<IJSRuntime>(js);
        var dialogs = new FakeDialogs();
        context.Services.AddSingleton<IQuasarDialogService>(dialogs);
        var cell = context.Render<EntityViewerColumnCell>(p => p.Add(x => x.AgentId, "agent/one")
            .Add(x => x.EntityId, 42).Add(x => x.EntityType, "Grid").Add(x => x.EntityDisplayName, "Freighter")
            .Add(x => x.CanViewEntity, true).Add(x => x.AutoOpenAssetSetup, false));
        Assert.Contains("mud-table-cell", cell.Find("td").ClassList);
        Assert.Equal("", cell.Find("td").GetAttribute("data-label"));
        Assert.Equal("0 0 24 24", cell.Find("button svg").GetAttribute("viewBox"));
        Assert.NotEmpty(cell.FindAll("button svg path"));
        var header = context.Render<EntityViewerColumnHeader>();
        Assert.Contains("mud-table-cell", header.Find("th").ClassList);
        await cell.Find("button").ClickAsync();

        // The dialog belongs to Quasar's shell host, never to the table cell.
        Assert.Empty(cell.FindAll("dialog"));
        var shown = Assert.Single(dialogs.Shown);
        Assert.Equal(typeof(EntityViewerDialog), shown.Component);
        Assert.Equal("Freighter", shown.Title);
        Assert.Contains("entity-viewer-fullscreen", shown.Options!.Class);
        Assert.Equal(QuasarDialogWidth.ExtraLarge, shown.Options.MaxWidth);
        Assert.True(shown.Options.FullWidth);

        var viewer = context.Render<EntityViewerDialog>(p => p.AddCascadingValue<IQuasarDialogInstance>(shown.Instance)
            .Add(x => x.ViewerUrl, (string)shown.Parameters!["ViewerUrl"]!)
            .Add(x => x.Subtitle, (string)shown.Parameters["Subtitle"]!)
            .Add(x => x.AssetSetupModuleUrl, (string)shown.Parameters["AssetSetupModuleUrl"]!)
            .Add(x => x.AssetSetupApiBasePath, (string)shown.Parameters["AssetSetupApiBasePath"]!)
            .Add(x => x.AutoOpenAssetSetup, (bool)shown.Parameters["AutoOpenAssetSetup"]!));
        Assert.Equal("Freighter", viewer.Find("h2.quasar-dialog-title").TextContent);
        Assert.Contains("agentId=agent%2Fone&entityId=42&voxels=1&context=1", viewer.Find("iframe").GetAttribute("src"));
        Assert.StartsWith("https://dash.test" + prefix + "_quasar/plugins/", viewer.Find("iframe").GetAttribute("src"));
        if (Environment.GetEnvironmentVariable("VIEWER_UI_PREVIEW_OUTPUT") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview, viewer.Markup);

        // Asset setup stacks as a second host dialog; a change made there reloads the viewer once it closes.
        var revision = viewer.Find("iframe");
        // The handler stays pending until the setup dialog closes, like the real host.
        var opening = viewer.Find("button[aria-label='Server asset streaming']").ClickAsync();
        var setup = dialogs.Shown[1];
        Assert.Equal(typeof(EntityViewerAssetSetupDialog), setup.Component);
        Assert.Equal(EntityViewerAssetSetupDialog.DefaultTitle, setup.Title);
        Assert.False(setup.Options!.FullWidth && setup.Options.Class!.Contains("entity-viewer-fullscreen"));
        await viewer.InvokeAsync(() => ((EventCallback)setup.Parameters!["Changed"]!).InvokeAsync());
        await viewer.InvokeAsync(() => setup.Instance.Cancel());
        await opening.WaitAsync(TimeSpan.FromSeconds(10));
        viewer.WaitForAssertion(() => Assert.Contains("Reloading viewer asset session.", viewer.Markup));
        Assert.NotSame(revision, viewer.Find("iframe"));

        await viewer.Find("button[aria-label='Close viewer']").ClickAsync();
        Assert.True(shown.Instance.Result!.Canceled);
        cell.Render(p => p.Add(x => x.CanViewEntity, false));
        Assert.True(cell.Find("button").HasAttribute("disabled"));
        Assert.Single(cell.FindAll("button svg path[stroke='currentColor']"));
        cell.Render(p => p.Add(x => x.CanViewEntity, true));
        Assert.False(cell.Find("button").HasAttribute("disabled"));
        Assert.Empty(cell.FindAll("button svg path[stroke='currentColor']"));
        Assert.Equal("Open viewer", cell.Find("button").GetAttribute("aria-label"));
    }

    [Theory]
    [InlineData("Grid", null, true, "agent", null, "Open viewer")]
    [InlineData("Voxel", "Asteroid", true, "agent", null, "Open viewer")]
    [InlineData("Grid", null, true, null, "/viewer", "Open viewer")]
    [InlineData("Voxel", "Planet", false, "agent", null, "Viewer unavailable: planets are not supported.")]
    [InlineData("voxel", "planet", false, "agent", null, "Viewer unavailable: planets are not supported.")]
    [InlineData("Character", null, false, "agent", null, "Viewer unavailable for this entity type.")]
    [InlineData("Grid", null, true, null, null, "Viewer unavailable: no connected server or viewer link.")]
    public void ColumnExplainsAvailabilityWithoutOpeningUnavailableEntities(
        string entityType, string? subType, bool canView, string? agentId, string? viewerUrl, string tooltip)
    {
        using var context = new BunitContext();
        var dialogs = new FakeDialogs();
        context.Services.AddSingleton<IQuasarDialogService>(dialogs);
        var cell = context.Render<EntityViewerColumnCell>(p => p.Add(x => x.EntityId, 42)
            .Add(x => x.EntityType, entityType).Add(x => x.EntitySubType, subType)
            .Add(x => x.CanViewEntity, canView).Add(x => x.AgentId, agentId).Add(x => x.ViewerUrl, viewerUrl));
        var button = cell.Find("button");
        var disabled = tooltip != "Open viewer";
        Assert.Equal(disabled, button.HasAttribute("disabled"));
        Assert.Equal(tooltip, button.GetAttribute("aria-label"));
        Assert.Equal(tooltip, button.ParentElement!.GetAttribute("title"));
        Assert.Equal(disabled ? 1 : 0, button.QuerySelectorAll("svg path[stroke='currentColor']").Length);
        if (disabled)
        {
            button.Click();
            Assert.Empty(dialogs.Shown);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/my-server-panel/")]
    public async Task AssetSetupPreservesConsentRootsAndChangedResult(string prefix)
    {
        await using var context = new BunitContext();
        context.Services.AddSingleton<NavigationManager>(new PanelNavigation(prefix));
        var js = new ViewerJs { CanManage = true, ConsentRequired = true };
        context.Services.AddSingleton<IJSRuntime>(js);
        var changes = 0;
        var instance = new FakeInstance(EntityViewerAssetSetupDialog.DefaultTitle);
        var setup = context.Render<EntityViewerAssetSetupDialog>(p => p.AddCascadingValue<IQuasarDialogInstance>(instance)
            .Add(x => x.Changed, () => changes++));
        Assert.Equal(EntityViewerAssetSetupDialog.DefaultTitle, setup.Find("h2.quasar-dialog-title").TextContent);
        Assert.Contains("Consent Required", setup.Markup);
        if (Environment.GetEnvironmentVariable("VIEWER_UI_PREVIEW_OUTPUT") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview + ".assets", setup.Markup);
        await setup.FindAll("button").Single(x => x.TextContent.Contains("Accept and Enable")).ClickAsync();
        Assert.Contains(js.Calls, x => x.Name == "acceptConsent");
        Assert.DoesNotContain("Consent Required", setup.Markup);
        setup.Find("select").Change("ExternalInstall");
        setup.FindAll("input")[0].Input("/games/SpaceEngineers/Content");
        await setup.FindAll("button").Single(x => x.TextContent.Contains("Save Asset Roots")).ClickAsync();
        var saved = js.Calls.Last(x => x.Name == "saveRootSettings");
        Assert.StartsWith("https://dash.test" + prefix + "_quasar/plugins/", (string)saved.Arguments![0]!);
        var request = JsonSerializer.SerializeToElement(saved.Arguments![1], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("ExternalInstall", request.GetProperty("baseGameSourceMode").GetString());
        Assert.Equal("/games/SpaceEngineers/Content", request.GetProperty("baseGameContentPath").GetString());
        await setup.FindAll("button").Single(x => x.TextContent.Trim() == "Close").ClickAsync();
        Assert.Equal(1, changes);
        Assert.True(instance.Result!.TryGetData(out bool changed) && changed);
    }

    [Fact]
    public void QueryOnlyAssetSetupShowsStatusAndNoManagementControls()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IJSRuntime>(new ViewerJs());
        var setup = context.Render<EntityViewerAssetSetupDialog>();
        Assert.Contains("management access", setup.Markup);
        Assert.Empty(setup.FindAll("input, select"));
        Assert.DoesNotContain("Accept and Enable", setup.Markup);
    }

    private sealed record ShownDialog(Type Component, string? Title, QuasarDialogParameters? Parameters,
        QuasarDialogOptions? Options, FakeInstance Instance);

    // Stands in for Quasar's shell host: records each request and lets the test close it.
    private sealed class FakeDialogs : IQuasarDialogService
    {
        public List<ShownDialog> Shown { get; } = [];

        public IQuasarDialogReference Show<TComponent>(string? title = null, QuasarDialogParameters? parameters = null,
            QuasarDialogOptions? options = null, CancellationToken cancellationToken = default) where TComponent : IComponent =>
            Show(typeof(TComponent), title, parameters, options, cancellationToken);

        public IQuasarDialogReference Show(Type componentType, string? title = null, QuasarDialogParameters? parameters = null,
            QuasarDialogOptions? options = null, CancellationToken cancellationToken = default)
        {
            var instance = new FakeInstance(title);
            Shown.Add(new ShownDialog(componentType, title, parameters, options, instance));
            return instance;
        }

        public Task<IQuasarDialogReference> ShowAsync<TComponent>(string? title = null, QuasarDialogParameters? parameters = null,
            QuasarDialogOptions? options = null, CancellationToken cancellationToken = default) where TComponent : IComponent =>
            Task.FromResult(Show(typeof(TComponent), title, parameters, options, cancellationToken));

        public Task<IQuasarDialogReference> ShowAsync(Type componentType, string? title = null, QuasarDialogParameters? parameters = null,
            QuasarDialogOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Show(componentType, title, parameters, options, cancellationToken));

        public Task<bool?> ShowMessageBoxAsync(string title, string message, string yesText = "OK", string? noText = null,
            string? cancelText = null, QuasarDialogOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeInstance(string? title) : IQuasarDialogInstance, IQuasarDialogReference
    {
        private readonly TaskCompletionSource<QuasarDialogResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid Id { get; } = Guid.NewGuid();
        public string? Title { get; } = title;
        public QuasarDialogResult? Result => _result.Task.IsCompleted ? _result.Task.Result : null;
        Task<QuasarDialogResult> IQuasarDialogReference.Result => _result.Task;
        public void Close(QuasarDialogResult? result = null) => _result.TrySetResult(result ?? QuasarDialogResult.Ok());
        public void Cancel() => _result.TrySetResult(QuasarDialogResult.Cancel());
    }

    private sealed class PanelNavigation : NavigationManager
    {
        public PanelNavigation(string prefix) => Initialize("https://dash.test" + prefix, "https://dash.test" + prefix);
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class ViewerJs : IJSRuntime, IJSObjectReference
    {
        public bool CanManage { get; set; }
        public bool ConsentRequired { get; set; }
        public List<(string Name, object?[]? Arguments)> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add((identifier, args));
            if (identifier == "import") return ValueTask.FromResult((TValue)(object)this);
            if (identifier == "acceptConsent") ConsentRequired = false;
            var json = identifier switch
            {
                "fetchAssetStatus" or "acceptConsent" => JsonSerializer.Serialize(new { canManageStreaming = CanManage,
                    consentRequired = ConsentRequired, consentAccepted = !ConsentRequired, streamingEnabled = !ConsentRequired, fileStreamingReady = true }),
                "fetchRootSettings" or "saveRootSettings" => "{\"baseGameSourceMode\":\"ManagedSteamCmd\",\"baseGameContentConfigured\":true}",
                "fetchInstallerStatus" => "{\"state\":\"Idle\",\"isRunning\":false}",
                _ => "null"
            };
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
