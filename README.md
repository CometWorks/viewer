# Quasar Viewer

Browser-side Space Engineers grid and asteroid viewer used by Quasar.

## Layout

- `src/` contains the static viewer page, ES modules, CSS, and local tooling.
- `quasar-plugin/` contains the thin Quasar UI plugin adapter.
- `Docs/` contains viewer user/developer documentation.
- `package.json` and `package-lock.json` pin browser runtime packages that
  Quasar stages into `/vendor`.

## Quasar Integration

Quasar vendors this repository as the `Viewer/` submodule. During
`Quasar/Quasar.csproj` build, `Viewer/src` is copied to
`Quasar/wwwroot/viewer` and npm packages are restored from `Viewer/package-lock.json`.

The repository also contains `quasar-plugin.json` and a thin adapter project at
`quasar-plugin/CometWorks.GridViewer.QuasarPlugin`. The adapter contributes the
viewer column on Quasar's Entities page and opens a fullscreen MudBlazor dialog
that serves the viewer from Quasar's plugin static asset mount:
`/_quasar/plugins/cometworks.gridviewer/`.

Future submodule updates should move the `Viewer` gitlink in Quasar. The Quasar
release workflow watches that path, so a merge to `main` that updates the
viewer submodule pointer triggers the release build.

When Quasar embeds the viewer in its fullscreen entity dialog, the viewer reads
MudBlazor CSS palette, typography, and border-radius variables from the parent
document and maps them onto its own CSS/Three.js theme tokens. Standalone direct
viewer URLs keep the built-in fallback palette.
