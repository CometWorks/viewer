using Microsoft.Extensions.DependencyInjection;

namespace CometWorks.EntityViewer.Services;

public static class EntityViewerServiceCollectionExtensions
{
    public static IServiceCollection AddEntityViewerUi(this IServiceCollection services)
    {
        return services;
    }
}
