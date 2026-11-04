using System.Reflection;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;

// Real Quasar SDK in an isolated HTTP test app; never starts the Quasar service.
internal sealed class QuasarSdkFixture : IDisposable
{
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previous;
    public QuasarSdkFixture(IServiceCollection services, string root)
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, root);
        services.AddSingleton<RbacConfigCatalog>();
        services.AddSingleton(new QuasarAuthOptions());
        services.AddSingleton<QuasarRoleMapper>();
        services.AddScoped<QuasarOperationService>();
        services.AddScoped<IQuasarOperationDispatcher>(provider => provider.GetRequiredService<QuasarOperationService>());
        services.AddScoped<QuasarHttpOperations>();
        services.AddScoped<IQuasarHttpOperations>(provider => provider.GetRequiredService<QuasarHttpOperations>());
        services.AddSingleton<QuasarBrowserSessions>();
        services.AddSingleton<QuasarTransferRegistry>();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery();
    }
    public static void Map(WebApplication app) => app.MapQuasarSdk();
    public static async Task Bind(HttpClient client)
    {
        using var response = await client.GetAsync(QuasarSdkEndpoints.Prefix + "/session");
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<JsonElement>();
        var name = tokens.GetProperty("headerName").GetString()!;
        client.DefaultRequestHeaders.Remove(name);
        client.DefaultRequestHeaders.Add(name, tokens.GetProperty("requestToken").GetString());
    }
    public void Dispose() => _cache.SetValue(null, _previous);
}
