# Quasar Viewer

Browser-side Space Engineers grid and asteroid viewer used by Quasar.

## Layout

- `src/` contains the static viewer page, ES modules, CSS, and local tooling.
- `Docs/` contains viewer user/developer documentation.
- `package.json` and `package-lock.json` pin browser runtime packages that
  Quasar stages into `/vendor`.

## Quasar Integration

Quasar vendors this repository as the `Viewer/` subtree. During
`Quasar/Quasar.csproj` build, `Viewer/src` is copied to
`Quasar/wwwroot/viewer` and npm packages are restored from `Viewer/package-lock.json`.

Future subtree updates should land under `Viewer/**` in Quasar. The Quasar
release workflow watches that path, so a merge to `main` that updates the
viewer subtree triggers the release build.
