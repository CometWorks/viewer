using Microsoft.Extensions.DependencyInjection;

namespace CometWorks.GridViewer.Services;

public static class GridViewerServiceCollectionExtensions
{
    public static IServiceCollection AddGridViewerUi(this IServiceCollection services)
    {
        return services;
    }
}
