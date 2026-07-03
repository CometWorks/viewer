# Quasar Entity Viewer

Browser-side Space Engineers entity viewer used by Quasar for live grids,
asteroid voxels, and future entity-focused views.

## Layout

- `src/CometWorks.EntityViewer/` contains reusable MudBlazor/Razor UI,
  services, and the static viewer runtime under `wwwroot/`.
- `src/CometWorks.EntityViewer.Quasar/` contains the thin Quasar UI plugin
  adapter.
- `samples/PreviewHost/` contains a standalone MudBlazor preview host for
  checking plugin components without running Quasar.
- `Docs/` contains viewer user/developer documentation.
- `package.json` and `package-lock.json` pin browser runtime packages that
  the viewer repository uses for its static runtime.

## Quasar Integration

Quasar installs this repository as a Quasar UI plugin through QuasarHub. The hub
manifest pins a commit, Quasar clones that commit, builds the adapter project,
and mounts `src/CometWorks.EntityViewer/wwwroot` under
`/_quasar/plugins/cometworks.entityviewer/`.

The repository follows the Quasar UI plugin template split:

- `CometWorks.EntityViewer` owns Razor components, MudBlazor dialog UI, shared UI
  service registration, and static assets.
- `CometWorks.EntityViewer.Quasar` implements `IQuasarPlugin` and contributes the
  Entities page viewer column extension targets.

During local development, the adapter references a sibling Quasar checkout when
`QuasarPluginAbstractionsProject` resolves. During QuasarHub installation,
Quasar passes `QuasarPluginAbstractionsAssembly` so the plugin builds against
the exact `Quasar.Plugin.Abstractions.dll` loaded by the running Quasar worker.

`quasar-plugin.json` points Quasar at the adapter project and exposes
`src/CometWorks.EntityViewer/wwwroot` as the plugin static asset directory. It
also asks Quasar to inject scoped `quasar-plugin.css` from that static asset
directory so the viewer dialog and replacement column can share host page
styling without loading the iframe-global viewer runtime CSS into Quasar. The
adapter opens a fullscreen MudBlazor dialog that serves the viewer from:
`/_quasar/plugins/cometworks.entityviewer/`.

## Preview Workflow

Build or run the standalone preview host while developing viewer plugin UI:

```bash
dotnet run --project samples/PreviewHost/PreviewHost.csproj
```

The preview host uses Quasar-like MudBlazor theming and references only the
shared `CometWorks.EntityViewer` UI project. It can render the replacement
Entities page column, the fullscreen dialog, and the static viewer runtime
without loading Quasar.

The static viewer vendors the browser modules it needs under
`src/CometWorks.EntityViewer/wwwroot/vendor/` and resolves them with relative
import-map URLs. This keeps the viewer working when Quasar serves it from
`/_quasar/plugins/cometworks.entityviewer/` instead of from the web root.

Future submodule updates should move the `Viewer` gitlink in Quasar. The Quasar
release workflow watches that path, so a merge to `main` that updates the
viewer submodule pointer triggers the release build.

When Quasar embeds the viewer in its fullscreen entity dialog, the viewer reads
MudBlazor CSS palette, typography, and border-radius variables from the parent
document and maps them onto its own CSS/Three.js theme tokens. Standalone direct
viewer URLs keep the built-in fallback palette.
