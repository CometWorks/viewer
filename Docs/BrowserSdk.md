# Browser SDK integration

Entity Viewer requires Quasar browser SDK v1 (`browserSdkVersion: 1` in
`quasar-plugin.json`). Publish the adapter and browser assets together, after
setting `quasarVersion` to the first released Quasar version containing SDK v1.
The current development version constraint alone is not a release guarantee.
New Quasar rejects unsupported SDK versions before loading the plugin assembly;
the viewer also reports a clear update error if its browser host is incompatible.

`viewer-sdk.js` imports the Quasar-generated `./_sdk/client.js` bridge relative to
the plugin asset URL. Quasar supplies the exact panel base, including hosted
`/<panel-id>/` prefixes. The viewer never discovers network addresses, manages
credentials, checks reachability or selects a fallback transport.

The 16 registered operations are:

| Operation | Default permission |
| --- | --- |
| `scene.read` | `CanView` |
| `assets.status` | `CanView` |
| `assets.roots.read`, `assets.roots.update`, `assets.consent.update` | `CanManageSecurity` |
| `assets.installer.status`, `.start`, `.input`, `.cancel` | `CanManageSecurity` |
| `assets.session.create`, `assets.resolve`, `assets.file.read` | `CanView` |
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
Only explicit capacity rejection during resource reservation may retry. Scene,
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
