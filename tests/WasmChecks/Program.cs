using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Quasar.Client.Ui;
using WasmChecks;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
// The same registration the Quasar dashboard uses for its native dialog and toast hosts.
builder.Services.AddQuasarUi();
builder.RootComponents.Add<ViewerTable>("#app");
await builder.Build().RunAsync();
