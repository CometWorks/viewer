# Browser SDK integration

Entity Viewer requires Quasar browser SDK v1 (`browserSdkVersion: 1` in
`quasar-plugin.json`). Publish the adapter and browser assets together, after
Quasar 1.6.0 or later. This candidate is Entity Viewer 0.1.13; publish its catalog
pin only after the compatible Quasar release and canary checks succeed.
New Quasar rejects unsupported SDK versions before loading the plugin assembly;
the viewer also reports a clear update error if its browser host is incompatible.

`viewer-sdk.js` imports the Quasar-generated `./_sdk/client.js` bridge relative to
the plugin asset URL. Quasar supplies the exact panel base, including hosted
`/<panel-id>/` prefixes. The viewer never discovers network addresses, manages
credentials, checks reachability or selects a fallback transport.

The 15 registered operations are:

| Operation | Default permission |
| --- | --- |
| `scene.read` | `CanView` |
| `assets.status` | `CanView` |
| `assets.roots.read`, `assets.roots.update`, `assets.consent.update` | `CanManageSecurity` |
| `assets.installer.status`, `.start`, `.input`, `.cancel` | `CanManageSecurity` |
| `assets.resolve`, `assets.file.read` | `CanView` |
| `replay.servers`, `replay.overview`, `replay.query`, `replay.recording.update` | `CanManageSecurity` |

Every call passes plugin ID `cometworks.entityviewer`. Asset resolution takes
`{ sessionId, asset: { logicalPath, rootId, sourceKind } }`; file reads take
`{ assetToken }` and return an SDK read handle. The renderer consumes the blob
and disposes the handle in `finally`. Existing feature caches remain keyed by
user and content revision; a fresh authorized resolve is required before reuse.
Scene entity IDs remain decimal strings in JavaScript to preserve 64-bit IDs.

Quasar Security settings may disable operations or change their required
policies. Legacy routes under `/_quasar/plugins/cometworks.entityviewer/` and
`/_content/CometWorks.EntityViewer/` share those exact handlers and rules. Writes
also require Quasar's antiforgery token. Consent and user ownership remain
feature checks regardless of administrator policy settings. Active reads
recheck consent, token expiry and SDK authorization; revocation cancels them.
Archive entries remain non-seekable streams; plain files retain ranges.

Browser callers use only `request`/`openRead`; Quasar queues at most four active
transfers per panel across same-origin plugin frames and limits pending work.
The viewer additionally queues remote resolves and downloads together, with at
most two active asset operations per frame. Model/LOD, texture and font loads
share this queue; local folder loads keep their existing parallelism. This
matches the feature's resolve limit and leaves room for other direct SDK calls.
Explicit `429` rejection of an asset resolve or read opening retries with bounded
backoff for about one minute, allowing shared user rate limits to reset. Exhausted
throttling remains an error and is never cached as an absent asset. An opened
download body is consumed once and its handle is disposed before releasing capacity.
Interrupted downloads and other transport failures are never replayed. Scene,
recording, consent, installer and upload mutations are never replayed.

PreviewHost explicitly serves the bridge to `preview-sdk.js`, a UI-only adapter
that reports local/off state and rejects live scenes, server writes and streams.
Tests can supply `globalThis.quasarPluginHost.sdk` explicitly. Production has no
silent fallback to the former HTTP endpoints.

No first-party viewer module retains raw `fetch` calls. Browser-native module,
stylesheet, font/image loads and renderer blob URLs remain ordinary resources;
local folder access, IndexedDB/CacheStorage and downloads of locally generated
replay evidence remain viewer features. Companion background recording does not
use the browser SDK and continues with all browser views closed.

Focused integration uses the real Quasar SDK runtime in isolated test apps:
`StreamingChecks` compares exact SDK/legacy bytes, ranges, shared policy changes,
CSRF, consent and user separation; `ReplayChecks` covers admin-only recording,
background collection and offline archives. Node checks cover SDK calls, cache
reauthorization, version errors and preview behavior. UI checks cover both URL
prefixes and the setup dialog. Real Steam and hosted browser acceptance remains
a release gate; these checks never start the user's Quasar service.

## Rollout security integration

This candidate includes upstream viewer security fix `4eb0fd0`. The unsafe
`assets.session.create` operation and legacy POST session-creation route are
removed: browsers cannot select trusted mod roots. `scene.read` and `replay.query`
return `{ data, assetSession }`, where the opaque grant is created from the actual
companion scene or authorized retained history. Browser callers unpack `data`
and attach the grant as a nonenumerable `assetSession` property. Rendering data
and the two URL prefixes remain the same. The aliases return this same envelope;
clients must update their viewer adapter and assets together.

Asset-size/archive caps, bounded session/token counts, resolve concurrency/rate,
stream concurrency and session byte budgets remain enforced for SDK and aliases.
Quota leases belong to the opened stream and release on SDK cancellation/disposal.
Non-administrators receive no host filesystem paths in status diagnostics.
These are feature checks in addition to the SDK authorization/lifetime boundary.

## Direct transport development proof

The `t3/webrtc-rewrite` branch adds `tests/DirectChecks`, which uses the actual
Viewer browser modules and 15 safe handlers, real Quasar cookie/session/SDK
runtime and native peer, and a real isolated Caddy boundary. Nebula-hosted SDK
clients automatically select RTC; no viewer setting or transport branch is added.
The shipped SDK-first 1.6.0/0.1.13 metadata stays unchanged.

Chromium/Firefox checks cover a large scene envelope, nonenumerable trusted grant,
consent/setup, archive integrity, range reads, iframe initialization, user-bound
resolve/file tokens, history/settings denial, removed session-creation refusal,
feature consent revocation and unavailable-direct failure without HTTP replay.
For a 16 MiB archive asset, each browser records 16,785,936 direct received bytes
including framing. Caddy records 290 bytes of SDK session metadata and **zero asset
body bytes** during the transfer. Server native counters independently record
more than 16 MiB sent, with buffers below 256 KiB. Scene grant metadata remains
HTTPS-free after setup; session/signaling/module loads still use HTTPS.

Run from the Viewer checkout with the RTC Quasar sibling and built peer:

```bash
MagnetarBin=/path/to/released/MagnetarInterim \
QUASAR_RTC_TEST_PEER=/path/to/quasar-peer-race \
QUASAR_RTC_TEST_WEBROOT=/path/to/quasar/Quasar/wwwroot \
NEBULA_CADDY=/path/to/verified/caddy \
PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs \
CHROME_PATH=/opt/google/chrome/chrome \
dotnet run --project tests/DirectChecks
```

This fixture does not start the user's Quasar service, SteamCMD or game servers.
Its scene comes from an isolated companion fixture; its hosting-lease provider
is fake. It proves actual feature/SDK transport and proxy byte accounting, not a
live Steam login, Internet NAT, real game rendering, retained-history RTC replay
or the full Nebula manager/frp chain. Those remain completion/acceptance gates.
Local content-folder and background recording behavior remain covered by the
existing Node, streaming and replay checks independently of Nebula.
