using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Security;
using CometWorks.GridViewer.QuasarPlugin.Components;

namespace CometWorks.GridViewer.QuasarPlugin;

public sealed class GridViewerQuasarPlugin : IQuasarPlugin
{
    public string Id => "cometworks.gridviewer";

    public string DisplayName => "Grid Viewer";

    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context)
    {
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context)
    {
    }

    public IEnumerable<Assembly> GetRazorAssemblies()
    {
        yield return typeof(GridViewerColumnCell).Assembly;
    }

    public IEnumerable<QuasarNavItem> GetNavItems()
    {
        yield break;
    }

    public IEnumerable<QuasarExtensionContribution> GetExtensions()
    {
        yield return new QuasarExtensionContribution(
            QuasarExtensionTargets.EntityViewerColumnHeader,
            typeof(GridViewerColumnHeader),
            QuasarPatchMode.Replace,
            100,
            Id,
            QuasarPolicyNames.CanView);

        yield return new QuasarExtensionContribution(
            QuasarExtensionTargets.EntityViewerColumnCell,
            typeof(GridViewerColumnCell),
            QuasarPatchMode.Replace,
            100,
            Id,
            QuasarPolicyNames.CanView);
    }
}
