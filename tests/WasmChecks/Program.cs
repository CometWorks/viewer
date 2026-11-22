using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WasmChecks;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<ViewerTable>("#app");
await builder.Build().RunAsync();
