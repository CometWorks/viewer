# Quasar Viewer

Browser-side Space Engineers grid and asteroid viewer used by Quasar.

## Layout

- `src/CometWorks.GridViewer/` contains reusable MudBlazor/Razor UI,
  services, and the static viewer runtime under `wwwroot/`.
- `src/CometWorks.GridViewer.Quasar/` contains the thin Quasar UI plugin
  adapter.
- `samples/PreviewHost/` contains a standalone MudBlazor preview host for
  checking plugin components without running Quasar.
- `Docs/` contains viewer user/developer documentation.
- `package.json` and `package-lock.json` pin browser runtime packages that
  Quasar stages into `/vendor`.

## Quasar Integration

Quasar vendors this repository as the `Viewer/` submodule. During
`Quasar/Quasar.csproj` build, `Viewer/src/CometWorks.GridViewer/wwwroot` is
copied to `Quasar/wwwroot/viewer` and npm packages are restored from
`Viewer/package-lock.json`.

The repository follows the Quasar UI plugin template split:

- `CometWorks.GridViewer` owns Razor components, MudBlazor dialog UI, shared UI
  service registration, and static assets.
- `CometWorks.GridViewer.Quasar` implements `IQuasarPlugin` and contributes the
  Entities page viewer column extension targets.

`quasar-plugin.json` points Quasar at the adapter project and exposes
`src/CometWorks.GridViewer/wwwroot` as the plugin static asset directory. The
adapter opens a fullscreen MudBlazor dialog that serves the viewer from:
`/_quasar/plugins/cometworks.gridviewer/`.

## Preview Workflow

Build or run the standalone preview host while developing viewer plugin UI:

```bash
dotnet run --project samples/PreviewHost/PreviewHost.csproj
```

The preview host uses Quasar-like MudBlazor theming and references only the
shared `CometWorks.GridViewer` UI project. It can render the replacement
Entities page column, the fullscreen dialog, and the static viewer runtime
without loading Quasar.

Future submodule updates should move the `Viewer` gitlink in Quasar. The Quasar
release workflow watches that path, so a merge to `main` that updates the
viewer submodule pointer triggers the release build.

When Quasar embeds the viewer in its fullscreen entity dialog, the viewer reads
MudBlazor CSS palette, typography, and border-radius variables from the parent
document and maps them onto its own CSS/Three.js theme tokens. Standalone direct
viewer URLs keep the built-in fallback palette.
