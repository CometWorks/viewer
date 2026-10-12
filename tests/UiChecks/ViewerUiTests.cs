using System.Text.Json;
using Bunit;
using CometWorks.EntityViewer.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Viewer.UiChecks;

public sealed class ViewerUiTests
{
    [Fact]
    public async Task ColumnOpensNativeViewerWithContextAndReturnsFocusThroughClose()
    {
        await using var context = new BunitContext();
        var js = new ViewerJs();
        context.Services.AddSingleton<IJSRuntime>(js);
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
        Assert.Contains("agentId=agent%2Fone&entityId=42&voxels=1&context=1", cell.Find("iframe").GetAttribute("src"));
        Assert.Contains("entity-viewer-fullscreen", cell.Find("dialog").ClassName);
        Assert.Contains(js.Calls, x => x.Name == "import" && (string)x.Arguments![0]! == "/_quasar/plugins/cometworks.entityviewer/viewer-dialog.js");
        Assert.Contains(js.Calls, x => x.Name == "open");
        if (Environment.GetEnvironmentVariable("VIEWER_UI_PREVIEW_OUTPUT") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview, cell.Markup);
        await cell.Find("button[aria-label='Close viewer']").ClickAsync();
        Assert.Empty(cell.FindAll("dialog"));
        Assert.Contains(js.Calls, x => x.Name == "close");
        cell.Render(p => p.Add(x => x.CanViewEntity, false));
        Assert.True(cell.Find("button").HasAttribute("disabled"));
    }

    [Fact]
    public async Task AssetSetupPreservesConsentRootsAndChangedResult()
    {
        await using var context = new BunitContext();
        var js = new ViewerJs { CanManage = true, ConsentRequired = true };
        context.Services.AddSingleton<IJSRuntime>(js);
        bool? changed = null;
        var setup = context.Render<EntityViewerAssetSetupDialog>(p => p.Add(x => x.Closed, value => changed = value));
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
        var request = JsonSerializer.SerializeToElement(saved.Arguments![1], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("ExternalInstall", request.GetProperty("baseGameSourceMode").GetString());
        Assert.Equal("/games/SpaceEngineers/Content", request.GetProperty("baseGameContentPath").GetString());
        await setup.FindAll("button").Single(x => x.TextContent.Trim() == "Close").ClickAsync();
        Assert.True(changed);
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
