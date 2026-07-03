using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Security;
using CometWorks.EntityViewer.Components;
using CometWorks.EntityViewer.Services;

namespace CometWorks.EntityViewer.Quasar;

public sealed class EntityViewerQuasarPlugin : IQuasarPlugin
{
    public string Id => "cometworks.entityviewer";

    public string DisplayName => "Entity Viewer";

    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context)
    {
        services.AddEntityViewerUi();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context)
    {
    }

    public IEnumerable<Assembly> GetRazorAssemblies()
    {
        yield return typeof(EntityViewerColumnCell).Assembly;
    }

    public IEnumerable<QuasarNavItem> GetNavItems()
    {
        yield break;
    }

    public IEnumerable<QuasarExtensionContribution> GetExtensions()
    {
        yield return new QuasarExtensionContribution(
            QuasarExtensionTargets.EntityViewerColumnHeader,
            typeof(EntityViewerColumnHeader),
            QuasarPatchMode.Replace,
            100,
            Id,
            QuasarPolicyNames.CanView);

        yield return new QuasarExtensionContribution(
            QuasarExtensionTargets.EntityViewerColumnCell,
            typeof(EntityViewerColumnCell),
            QuasarPatchMode.Replace,
            100,
            Id,
            QuasarPolicyNames.CanView);
    }
}
