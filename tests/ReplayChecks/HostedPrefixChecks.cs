using CometWorks.EntityViewer.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

internal static class HostedPrefixChecks
{
    public static async Task RunAsync()
    {
        foreach (var prefix in new[] { "/", "/my-server-panel/" })
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<NavigationManager>(new PanelNavigation("https://dash.test" + prefix));
            services.AddSingleton<IJSRuntime>(new NoScriptRuntime());
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = await renderer.RenderComponentAsync<EntityViewerDialog>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(EntityViewerDialog.ViewerUrl)] = "/_quasar/plugins/cometworks.entityviewer/index.html?agentId=fixture&entityId=42",
                    [nameof(EntityViewerDialog.AutoOpenAssetSetup)] = false
                }));
                return result.ToHtmlString();
            });
            if (!html.Contains("src=\"https://dash.test" + prefix + "_quasar/plugins/cometworks.entityviewer/index.html?", StringComparison.Ordinal))
                throw new InvalidOperationException("Viewer iframe escaped its Quasar application prefix.");
        }
        Console.WriteLine("Root and hosted-prefix viewer iframe rendering passed.");
    }
    private sealed class PanelNavigation : NavigationManager
    {
        public PanelNavigation(string origin) => Initialize(origin, origin);
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class NoScriptRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static rendering cannot run JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
