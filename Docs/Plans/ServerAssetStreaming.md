# Server Asset Streaming Implementation Plan

This plan covers opt-in streaming of Space Engineers model, texture, SBC, and
mod asset bytes from server-side roots to the browser viewer. It deliberately
keeps the current local-folder mode as a fallback.

## Current State

- The Quasar UI plugin serves the static viewer from
  `/_quasar/plugins/cometworks.entityviewer/`.
- The scene endpoint is already gated by Quasar `CanView` authorization.
- The Magnetar companion returns scene metadata only.
- Browser asset loading is local-only. `content-folder.js` resolves model,
  texture, and mod assets from File System Access API or folder-input handles.
- Mod metadata already includes `RootId`, `PublishedFileId`, names, and
  aliases, which is enough to map active scene mods to server-side mod roots.

## Goal

Add an optional server asset streaming mode:

1. User opens viewer through the Quasar control plane.
2. The plugin relies on Quasar's existing control-plane access and `CanView`
   authorization. It does not add a separate login, Steam ID, or ownership
   check.
3. If streaming is enabled and consent/setup is complete, the browser resolves
   asset files from plugin-owned streaming endpoints.
4. Base game assets come from a server-side Space Engineers game client install.
5. Mod assets come from the corresponding dedicated server mods/workshop folder.
6. If streaming is unavailable, disabled, or denied, the viewer falls back to the
   existing local Content/Mods folder flow.

## Ownership Responsibility

Do not implement an extra login, Steam ID, or game ownership check for asset
streaming. Quasar already gates the control plane behind login for non-localhost
use, and the plugin should rely on Quasar's existing `CanView` authorization and
the server owner's explicit consent.

The consent text must state that CometWorks does not verify whether viewer users
own Space Engineers. The server owner is responsible for ensuring users are
allowed to receive and use streamed Space Engineers assets.

## Consent And Setup Policy

Use one operator/admin consent gate:

- **Operator/admin consent:** enables server-side asset streaming for this
  Quasar install and allows managed SteamCMD setup. This should require a Quasar
  admin/manage permission, not ordinary `CanView`. The implementation uses
  Quasar `CanManageSecurity` for consent changes.

Draft consent text for operator setup:

> Server asset streaming sends Space Engineers game and mod asset files from
> this server to viewer users with Quasar access. CometWorks does not verify
> whether those users own Space Engineers. This is a legal grey area. As the
> server owner, you are responsible for ensuring every enabled user is allowed
> to receive and use these assets under the relevant licenses, server rules, and
> access policies. CometWorks is not responsible for how this feature is used.
> Enable only if you understand and accept this risk.

Persist:

- `StreamingEnabled`
- `ConsentAccepted`
- `ConsentVersion`
- `ConsentAcceptedByUserId`
- `ConsentAcceptedAtUtc`
- `BaseGameSourceMode`: `ExternalInstall` or `ManagedSteamCmd`
- `BaseGameContentPath`
- `DedicatedServerModsPath` or agent-reported mods-root binding
- `LastInstallStatus`

Consent must be changeable later through a plugin settings dialog. Disabling it
must immediately reject new asset sessions and streaming requests.

## Installer UX

"Installer window" should be a Quasar/MudBlazor modal, not an OS desktop window:

- Shows install root:
  `<QuasarInstallDir>/ManagedRuntime/Tools/SpaceEngineersClient`
- Shows SteamCMD executable path and command state.
- Streams stdout/stderr lines from the server-side SteamCMD process.
- Shows progress states: missing SteamCMD, waiting for login, installing,
  validating, complete, failed, update check running.
- If SteamCMD is not found in `QUASAR_STEAMCMD_PATH`, `STEAMCMD_PATH`,
  `<QuasarInstallDir>/ManagedRuntime/Tools/SteamCMD`, or `PATH`, the installer
  downloads the official SteamCMD package into
  `<QuasarInstallDir>/ManagedRuntime/Tools/SteamCMD` before launching it.
- Allows cancel while process is active.
- Never stores Steam password in Quasar config.

Managed install command shape:

```text
steamcmd +force_install_dir "<QuasarInstallDir>/ManagedRuntime/Tools/SpaceEngineersClient" +login <account> +app_update 244850 validate +quit
```

Valve's SteamCMD documentation recommends setting `force_install_dir` before
login and uses `app_update` to install/update apps:
https://developer.valvesoftware.com/wiki/SteamCMD

Notes:

- Space Engineers client install likely requires a Steam account that owns the
  game. Anonymous login should not be assumed.
- Steam Guard/2FA must be handled interactively through the installer log/input
  flow or by running SteamCMD once out-of-band.
- SteamCMD's own login/session files live under the managed tools area. Quasar
  should protect that directory permissions and never expose it through asset
  streaming.
- Add hourly update checks with a background service using a single-flight lock.
  Skip if an install/update is already running, back off on failure, and expose
  status in the setup modal.

## Asset Origin Model

Support two base-game source modes:

- **External install:** admin supplies an existing Space Engineers client
  install path. Validate it contains `Content/Data`, `Content/Models`, and
  `Content/Textures`.
- **Managed install:** SteamCMD installs the client into
  `<QuasarInstallDir>/ManagedRuntime/Tools/SpaceEngineersClient`, then base
  assets resolve from its `Content` folder.

Mod source options:

- If Quasar and the dedicated server mods folder are on the same filesystem,
  configure/read that folder directly.
- If mods live only on a remote agent/DS host, the current companion JSON channel
  is not suitable for large binary streaming. Add a Quasar/agent binary stream
  capability first, or limit phase 1 to same-host/readable mods roots.

## Server Components

Add these services to `CometWorks.EntityViewer.Quasar`:

- `EntityViewerStreamingOptions`
- `IEntityViewerStreamingSettingsStore`
- `SteamCmdInstallService`
- `AssetRootRegistry`
- `ServerAssetResolver`
- `ViewerAssetSessionStore`
- `AssetStreamTokenService`

The resolver must:

- Normalize logical paths the same way `content-folder.js` does.
- Resolve base game paths under `Content` only.
- Resolve mod paths only for active scene mods in the current scene snapshot.
- Support unpacked mod directories, `.sbm` archives, and `*_legacy.bin` archives.
- Read individual archive entries server-side; do not stream whole archives for
  a single requested model or texture.
- Reject path traversal, absolute client-supplied paths, symlink escapes, and
  any path outside registered roots.

## HTTP API Shape

All endpoints live under
`/_quasar/plugins/cometworks.entityviewer/api/assets`.

Suggested endpoints:

- `GET /status`
  - Returns enabled/disabled, consent state, install/update status, source-root
    validation state, and whether streaming is available in the current Quasar
    authorization context.
- `POST /settings/consent`
  - Admin only. Accept/revoke consent and enable/disable streaming.
- `POST /installer/start`
  - Admin only. Starts managed SteamCMD install/update.
- `GET /installer/status`
  - Admin only. Returns installer state and a bounded SteamCMD log tail.
- `POST /installer/input`
  - Admin only. Sends one line to SteamCMD stdin for password or Steam Guard
    prompts. The value is not stored.
- `POST /installer/cancel`
  - Admin only. Stops the running SteamCMD process tree.
- `POST /sessions`
  - Existing Quasar `CanView` authorization required. Creates a short-lived
    asset session for `{ agentId, entityId, sceneCaptureId, mods[] }`.
- `POST /sessions/{sessionId}/resolve`
  - Resolves `{ logicalPath, rootId, sourceKind }` to an opaque asset token and
    metadata.
- `POST /sessions/{sessionId}/resolve-batch`
  - Same, batched for model/texture bursts.
- `GET /files/{assetToken}`
  - Streams bytes for one resolved asset. Supports `Range`, `ETag`, cancellation,
    and per-user/session expiry.

Never expose raw filesystem paths in responses. Use opaque ids/tokens only.

## Browser Runtime Changes

Add `remote-assets.js`:

- Calls `/api/assets/status` on startup.
- Creates asset session after scene snapshot succeeds.
- Resolves logical paths through `/resolve` or `/resolve-batch`.
- Returns handle-like objects with `getFile()` that fetch bytes from
  `/files/{assetToken}` and creates a `File`/`Blob`.
- Caches resolved metadata and in-flight byte fetches with the existing cache
  generation model.

Update `content-folder.js`:

- Add provider ordering: remote streaming first when enabled/authorized, local
  folder second, null fallback last.
- Keep current local Content/Mods selection UI for fallback and for users without
  streaming access.
- Avoid zip.js archive reads for remote mod archives; remote resolver should
  serve individual entries.

Update `index.html` / controls:

- Replace "Local Content" wording with an "Assets" section.
- Show current mode: `Server streaming`, `Local folders`, or `Missing assets`.
- Keep local folder buttons visible when remote streaming is disabled or denied.

## Security Requirements

- All asset endpoints stay under Quasar plugin routes and rely on Quasar's
  normal control-plane access model.
- Streaming file endpoints require:
  - Quasar `CanView`
  - streaming consent enabled
  - valid asset session
  - asset token bound to same user/session
- Add rate limits and concurrency limits per user/session.
- Add max file size and response byte budget knobs.
- Audit consent changes, installer runs, and denied asset requests.
- Use `Cache-Control: private` or `no-store` depending on legal decision. Do not
  let shared proxies cache game assets.

## Implementation Phases

1. **Decision/doc phase**
   - Confirm admin consent behavior.
   - Confirm same-host mods path or required agent binary streaming support.

2. **Settings and UX skeleton**
   - Add settings store and status endpoint.
   - Add MudBlazor consent/setup modal.
   - No asset bytes served yet.

3. **Managed install**
   - Add SteamCMD process runner.
   - Add installer log/status UI.
   - Add hourly update service.
   - Validate managed `Content` folder after install.

4. **Asset resolver**
   - Implement safe root registry and canonical path checks.
   - Implement base Content lookup.
   - Implement active-mod lookup and archive-entry extraction.
   - Unit test path normalization, traversal rejection, case-insensitive lookup,
     archives, and active-mod filtering.

5. **Streaming endpoints**
   - Add asset sessions, opaque tokens, resolve/batch resolve, and file streaming.
   - Add byte/rate/concurrency limits.
   - Add integration tests for authorization failures and token expiry.

6. **Browser integration**
   - Add remote provider and mode status UI.
   - Reuse existing model/texture parsers from fetched `File`/`Blob` objects.
   - Keep local folder fallback intact.

7. **Verification**
   - Build plugin projects.
   - Verify no endpoint streams outside Quasar access, without `CanView`,
     without consent, or without a valid asset token.
   - Verify vanilla model/texture loading from managed client install.
   - Verify modded assets from DS mods folder, including `.sbm` and
     `*_legacy.bin`.
   - Verify fallback to local folders when streaming is off/denied.
   - Verify hourly update does not run concurrently with active install/update.

## Open Decisions Before Code

- Are Quasar and DS mods folders guaranteed same-host/readable, or do we need a
  binary asset channel through the agent first?
- Which Steam account owns the managed client install, and how should Steam Guard
  be handled operationally?
