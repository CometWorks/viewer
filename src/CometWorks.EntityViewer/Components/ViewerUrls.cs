using Microsoft.AspNetCore.Components;

namespace CometWorks.EntityViewer.Components;

internal static class ViewerUrls
{
    // Standalone RCL: resolve app-root inputs before they reach iframe/import/fetch.
    public static string ApplicationUrl(this NavigationManager navigation, string value) =>
        value.StartsWith("/", StringComparison.Ordinal) && !value.StartsWith("//", StringComparison.Ordinal)
            ? new Uri(new Uri(navigation.BaseUri), value.TrimStart('/')).AbsoluteUri : value;
}
