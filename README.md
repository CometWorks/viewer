# Quasar Entity Viewer

Browser-side Space Engineers entity viewer used by Quasar for live grids,
asteroid voxels, and future entity-focused views.

## Layout

- `src/CometWorks.EntityViewer/` contains reusable native HTML/Razor UI,
  services, and the static viewer runtime under `wwwroot/`.
- `src/CometWorks.EntityViewer.Quasar/` contains the thin Quasar UI plugin
  adapter.
- `src/CometWorks.EntityViewer.Magnetar/` contains the Magnetar companion
  plugin that captures live scene snapshots for the viewer.
- `samples/PreviewHost/` contains a standalone native preview host for
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

- `CometWorks.EntityViewer` owns Razor components, native browser dialog UI, shared UI
  service registration, and static assets.
- `CometWorks.EntityViewer.Quasar` implements `IQuasarPlugin` and contributes the
  Entities page viewer column extension targets. Its scene API endpoint calls
  Quasar's `IQuasarCompanionChannel`; Quasar core does not contain a viewer
  scene endpoint or viewer scene DTOs.
- `CometWorks.EntityViewer.Magnetar` implements the companion plugin handler
  for `cometworks.entityviewer` scene requests and owns the server-side Space
  Engineers scene-capture code.

During local development, the adapter references a sibling Quasar checkout when
`QuasarPluginAbstractionsProject` resolves. During QuasarHub installation,
Quasar passes `QuasarPluginAbstractionsAssembly` so the plugin builds against
the exact `Quasar.Plugin.Abstractions.dll` loaded by the running Quasar worker.

The Magnetar companion project references the sibling Quasar checkout's
`Magnetar.Protocol` project by default. It compiles against the protocol bridge
but does not copy `Magnetar.Protocol.dll` into its own output; the running server
must use the protocol assembly staged with Quasar.Agent so companion handler
interface identity stays shared.

`quasar-plugin.json` points Quasar at the adapter project and exposes
`src/CometWorks.EntityViewer/wwwroot` as the plugin static asset directory. It
also asks Quasar to inject scoped `quasar-plugin.css` from that static asset
directory so the viewer dialog and replacement column can share host page
styling without loading the iframe-global viewer runtime CSS into Quasar. The
manifest also declares the `cometworks.entityviewer` companion plugin id. The
adapter opens a fullscreen native browser dialog that serves the viewer from
`/_quasar/plugins/cometworks.entityviewer/`.

## Preview Workflow

Build or run the standalone preview host while developing viewer plugin UI:

```bash
dotnet run --project samples/PreviewHost/PreviewHost.csproj
```

The preview host uses Quasar appearance tokens and references only the
shared `CometWorks.EntityViewer` UI project. It can render the replacement
Entities page column, the fullscreen dialog, and the static viewer runtime
without loading Quasar.

The static viewer vendors the browser modules it needs under
`src/CometWorks.EntityViewer/wwwroot/vendor/` and resolves them with relative
import-map URLs. This keeps the viewer working when Quasar serves it from
`/_quasar/plugins/cometworks.entityviewer/` instead of from the web root.

Chrome, Edge, and other Chromium 122+ browsers can persist selected local
Content and Mods folder handles through the File System Access API and
IndexedDB. Other browsers use the backup folder-input viewer path and warn that
folder selections must be repeated after reload.

Viewer releases are installed from pinned QuasarHub revisions. Publish the
adapter and static assets together; no Quasar submodule update is required.

When Quasar embeds the viewer in its fullscreen entity dialog, the viewer reads
`--quasar-palette-*` variables from the enclosing Quasar shell and maps them onto
its CSS/Three.js theme tokens. It observes appearance changes and releases its
observer when the iframe is unloaded. Legacy host palette variables remain a
fallback during the core UI migration. Standalone direct viewer URLs keep the
built-in fallback palette. The UI, adapter, and preview host no longer reference
MudBlazor; scene rendering and companion APIs remain unchanged.

## UI regression checks

```bash
dotnet build src/CometWorks.EntityViewer.Quasar/CometWorks.EntityViewer.Quasar.csproj
dotnet build samples/PreviewHost/PreviewHost.csproj
dotnet test tests/UiChecks/UiChecks.csproj
npm test
npm run check
```

Component checks cover viewer URLs, native modal closure, asset consent and root
saves, and read-only management controls. Browser tests cover native/legacy theme
mapping, live appearance changes, standalone fallback, and observer cleanup.
Real Quasar circuits and authenticated asset streaming still need integration
acceptance with the matching installed plugin package.
