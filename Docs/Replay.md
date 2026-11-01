# Contextual replay

Recording is **disabled by default**, including upgrades from automatic recording.
An administrator can opt in per server using **Enable recording for this server (all cluster nodes)**
in the Entity Viewer history sidebar. The setting persists in Quasar. Once enabled,
the recorder runs in the Entity Viewer Magnetar companion without an open browser. Quasar's viewer plugin collects and archives data through the existing
companion channel. Magnetar core requires no new runtime service or physics patch.
The Quasar abstraction adds connected-server discovery for background companion
consumers. Standalone history uses the stable server name. A cluster appears once,
under its configured display name, and combines history from all its nodes. Discovery
supplies the explicit cluster ID and stable slot; display names are never used to
infer membership. Node and agent deep links select the containing cluster.
Collection still uses separate telemetry archives (node/slot/epoch) and addresses
each exact live agent. Existing archive files are not moved. Cluster membership
is saved with each archive so offline history remains grouped across Quasar restarts.
Older archives without cluster metadata join the group when that node reconnects;
an offline legacy archive cannot be assigned to a cluster from its hashed ID alone.
Matching Quasar and viewer revisions must be installed.

Recording controls apply to the whole selected cluster. A durable cluster policy
covers connected nodes, offline members when they reconnect, and newly discovered
slots or replacement incarnations. Existing per-node settings remain until a cluster
setting is saved; conflicting settings appear as a mixed checkbox. Standalone
settings stay independent. Incidents and evidence keep their original node sessions;
this grouping does not infer cross-node incident ownership or merge incident IDs.

Disabled companions do not construct a recorder, discover entities, attach recording
hooks, start recording workers or sample the world. Quasar sends no collection
requests to disabled servers. Disabling detaches all recording hooks immediately
on the simulation thread, cancels terrain work and drains already captured data
in the background. An in-flight terrain read can finish; disabling never blocks
the simulation thread waiting for it. Existing history remains readable until its
24-hour expiry. A 30-second renewable capture lease stops recording if Quasar loses
contact, including when a disable request cannot reach the companion. Reconnecting
resumes capture only for servers with a persisted enabled setting. The game plugin's
normal update callback retains only a null/disabled check while recording is off.

## Capture and escalation

- Target baseline: one observation per second for loaded grids and online players.
- Target incident cadence: five observations per second inside an active incident's
  recorded context. Meaningful activity renews a 60-second quiet deadline; ordinary
  samples, movement and nearby high-rate scheduling do not prolong it.
- Incidents start with a 500 m radius and can grow through overlapping 500 m
  activity contexts (up to 1 km between trigger points). Connectivity uses the
  actual activity points, not overlap of aggregate bounds. The resulting spherical
  capture envelope is fixed to the original seed, with a configurable radius cap
  of 500–10,000 m (default 10,000 m).
- The maximum incident duration is configurable from 1–60 minutes (default 15).
  Each incident snapshots both limits when it starts. Configuration changes and
  lease renewals never reset existing deadlines. Continued activity at the cap
  starts a new incident. Overlapping incidents merge when their combined context
  fits the earliest seed's cap; the earliest hard deadline survives.
- Capture retains the 32-active-incident bound and at most 256 distinct activity
  anchors per incident. Established anchors are preserved at capacity and context
  expansion is marked incomplete. Capture, writer, terrain and query budgets still
  apply; reaching them must not silently imply complete coverage.
- Physics detection requires two consecutive observations of angular speed above
  5 rad/s or acceleration above 100 m/s². A grid produces at most one such event
  per five seconds. These are **suspected instability** markers with recorded
  velocities, not proof of clang or responsibility. Brief motion between coarse
  observations can be missed. No additional profiler or Havok contact patch runs.
- Position is the world-space grid bounds center; rotation and size describe the
  oriented bounds. Positions/velocities are rounded to two decimal places,
  quaternions to four. IDs remain strings. Player source identifies their
  controlled entity, while player position/orientation comes from the character when
  available (including seated or remotely controlling players). Character model
  path, mod ID and suit color are recorded; without a character, the controlled
  entity location is explicitly marked approximate. Natural gravity matches the
  live viewer and is cached for one second per track. These small fields use the
  existing suppression/heartbeat and retention limits. Damage source is the attacker entity reported by the game;
  zero means unknown, and ownership/player attribution is not inferred.
- Unchanged observations are suppressed with a 30-second heartbeat. Baseline
  changes under 25 cm and small rotation/velocity changes are suppressed relative
  to the last written sample; incident position tolerance is 1 cm. Cadence
  transitions are written even if the entity is stationary. Continuous low-rate
  history supplies pre-incident context; high-rate detail begins after detection.
- A removable postfix observes `MyDamageSystem.RaiseAfterDamageApplied` for slim blocks and characters.
  Both handheld and ship grinders use this path with damage type `Grind` and the
  grinder entity as source; grinding triggers incident detail. No duplicate grinder
  patch is needed. Welding is captured as build/integrity changes and character
  tool-use state, not damage or a claim of who welded a block.
  Amount is the callback's damage amount, **not a claimed integrity delta**. The
  game can accumulate damage and apply it later. Matching target block, source,
  damage type/deformation flag and 250 ms bucket are coalesced into a total/count.
  Other game damage paths and exact block destruction attribution still require
  live scenario coverage. Entity disappearance is recorded separately.
- Every five seconds a health record includes simulation speed and tracked-grid
  count and sun direction/intensity. Compact block layouts record definition/model path, cell extents,
  orientation, color, build progress and integrity. Character world scale is retained.
  Inventories, texture bytes, fine deformation, debris and animated subparts are omitted.

## Historical grids and destruction

Block additions, removals and integrity changes use the game's grid events. The
capture loop observes affected cells within its work budget; the next scheduled
sample contains their updated layout. Block-add/remove journal events preserve the original callback timestamp and cell; visual layout changes arrive at the next complete captured sample (1 Hz baseline, 5 Hz during incidents). The event position for these block changes is resolved when the bounded queue is drained, so it is contextual rather than an exact impact point. A background
walk on the simulation thread also revisits blocks every 30 seconds to observe
paint and other changes without reliable events. Initial layout capture is spread
across updates. Structural changes restart an invalidated iterator; incomplete or
over-budget grids remain visibly bounds-only until a complete capture succeeds.
Splits produce separately discovered entities and explicit original-to-fragment links; merges record the grid pair, invalidate and recapture rebased layouts. This is observed geometry, not a deterministic replay of physics.

The worker partitions blocks into stable cell-hash pages, roughly 64 blocks per
page initially, and hashes compressed page contents. A changed block rewrites
only its page. Unchanged pages are stored once and shared by every referring
segment; each recorded layout is a complete list of page hashes, itself stored once as a
manifest. Samples carry only its hash. There is no
chain of historical deltas to chase. Segment references keep necessary pages alive
across the 24-hour cutoff, and the final reference's deletion removes the page.
Movement samples also record the grid origin separately from its bounds center,
so asymmetric grids and changing bounds do not displace the historical blocks.

The replay renderer shares the live viewer's MWM parser, UVs, material groups,
normal maps, glass and HSV paint shader, with instanced rendering per grid/model.
Storage pages are hash buckets rather than spatial chunks, so matching models
across all active pages share a batch. Material groups remain separate draws;
paint, UV patterns and block picking remain per instance. Transform-only playback
reuses these meshes. Layout changes rebuild only batches referenced by changed
pages, including backward seeks and replacement of unavailable assets.
Configured server asset streaming and local Content/Mods folders supply models
and textures, never a current grid snapshot. It actively preloads assets from all
loaded historical pages and character appearances, including later observations,
with four concurrent model jobs. The previous 128-model, 64 MiB model and
256-texture admission limits are removed: all assets in the retained incident context
are prepared, including future layouts. Playback waits behind a progress overlay
while assets, terrain meshes, texture uploads and initial shaders are prepared.
Missing assets remain explicitly reported.
Loading another range releases its scene instances while retaining reusable assets
for the history window's lifetime.

Generated armor records the same authored cube-part models, transforms and UV
pattern offsets used by the main scene. A worker derives these from immutable
block-definition/value snapshots; it never loads an MWM on the simulation thread.
Content-addressed templates deduplicate the surfaces across blocks; per-block
pattern offsets remain compact. Earlier recordings retain their topology fallback.
Replay shares the main scene's theme color parsing, HSV paint conversion, model
materials and voxel mesher. Recorded sun direction and intensity drive sunlight
and time-of-day shadows as the timeline moves or seeks. Raw recorded intensity
uses the main viewer's brightness cap before conversion to renderer intensity,
so high-intensity game environments do not wash out history's models and terrain.
The recorded values remain unchanged, and recorded zero intensity stays dark.
The `Recorded sun and shadows` control is enabled by default; disabling it uses
uniform ambient light.
Missing sun observations also use ambient light and are identified in the lighting
status. Integrity is a diagnostic rather than a permanent red paint overlay.

Missing models use wireframe proxies or armor topology; missing historical layouts
use bounds only. Characters retain their authored metre scale and any recorded
world scale, with nametags and reconstructed GPU skinning. Their material clones
share loaded textures directly; they do not JSON-serialize texture uniforms.
Equipment meshes, skin overrides, LCD contents, runtime lights, emissive changes,
debris, fine deformation and animated subparts are not reconstructed.

Live and history modes share the persistent streamed-byte cache. Fresh authorized
resolves now hash actual asset contents on Quasar, including archive entries;
cache keys change even when updates preserve size and timestamps. The browser
verifies SHA-256 before accepting downloaded or persisted bytes. On insecure HTTP
origins without Web Crypto, streaming remains available but persistent reuse is
bypassed. Use HTTPS for verified cache reuse. Historical asset
binaries are not archived: an installed game/mod update can change the appearance
of older recorded geometry, although stale cached bytes will not masquerade as
that updated revision. Local folders have their existing file metadata semantics.

## Historical terrain

Terrain requests are gathered from already-visited grids/players every ten seconds,
covering a 500 m context (up to 1,024 m half-extent for large grids). Discovery uses
entity lifecycle events and at most 64 voxel roots. A single background worker
reads one fixed shared-border chunk at most every 100 ms, pins storage during reads,
and adapts LOD to at most 27 tiles per request. Pending requests are capped at 64,
tracked chunks at 128, and each chunk at 33³ samples. Resolution is reported in
the viewer; this is bounded sampled terrain, not lossless planet storage.

Storage-change callbacks only increment a revision and enqueue dirty bounds.
Affected chunks become unknown at the edit notification timestamp; replacement
snapshots use read-completion timestamps, never backdated edit times. Reads
concurrent with an edit are discarded. Failed chunks back off without starving
others. Unchanged terrain is read once, with deduplicated ten-second reference
checkpoints; a region expires after 60 seconds without a nearby recording request.
Chunk content, materials, transforms and texture definitions share the existing
content-addressed archive, quotas and 24-hour reference cleanup. Each segment
carries its own dependencies, so retention cannot leave dangling references.

The browser preloads every distinct terrain revision and material texture in the
chosen history, reuses the main viewer's meshing/material pipeline, and switches
cached meshes while scrubbing. Edits, missing observations and capture gaps hide
unknown coverage. Old recordings without terrain stay explicitly terrain-less;
current terrain is never substituted. Background voxel reads still contend with
storage writes; live server overhead must be measured before claiming a cost bound.

## Performance and limits

Capture yields between entities and blocks after a 0.5 ms elapsed-work budget per update.
It examines at most 128 grid slots per update, tracks at most 8192 grids, and
bounds all pending damage/action/explosion/lineage events to a shared 1024 callbacks and pending output to 4096 records. Player
capture examines at most 128 online players and shares the update budget.
Block capture processes at most four cells per visited grid, caps pending cells
at 1024 per grid / 4096 total, and holds at most 50,000 blocks per grid / 262,144
blocks total. Grids exceeding those bounds fall back to bounds-only coverage.
Queue overflow invalidates layout state and requires a new complete capture;
a broken mod block is isolated with a 30-second retry delay.
Session initialization performs one entity-discovery pass; later grids arrive
through lifecycle events. Initial discovery and an individual engine API call
are not preemptible. The budget is not a guarantee about worst-case engine calls.
The actual cadence can fall under server load; timestamps describe observations,
not a promise that every scheduled sample was obtained. Queue/track loss produces
explicit gap records. Filesystem work, compression and transport run off the
simulation thread.

The writer consumes at most 512 queued items per batch, flushing within five
seconds and splitting timeline segments at 128 records or a 2 MiB page budget.
JSON keys compress within each segment; samples are independent records, avoiding
cross-segment delta chains and baseline dependencies at retention boundaries.
Timeline compressed segments are capped at 512 KiB and expansion at 4 MiB;
individual block pages are capped at 256 KiB expanded, and collection responses
at 4 MiB including geometry. Queries cap unique expanded data at 32 MiB and
40,000 records across the whole selected server or cluster, sharing repeated page data within each response. The recording
limit remains 50,000 blocks per grid; the browser renders all recorded grids within
the retained context without a separate 50,000-block total limit.
Archive file metadata is indexed in memory rather than rescanning the directory
for every collection. Allocation-accounted storage limits (4 KiB minimum/file):

| Storage | Limit | Location |
|---|---|---|
| Companion unacknowledged spool | 64 MiB | `<world>/EntityViewerReplay` |
| Quasar retained archive | 256 MiB/server | `<Quasar>/Data/EntityViewerReplay/<server hash>` |

Both stores expire segments after 24 hours while running; expired data is excluded
from all reads. Quasar also expires data for disconnected servers. A stopped
process cannot delete files: expired spool files are pruned when recording next opens that world’s spool. Storage pressure can shorten history; the archive reports lost segments
and persists its loss counter. These are protective caps, not expected daily
volume. Detailed live overhead and active-server bytes/day remain unmeasured.

For enabled servers, Quasar polls independently of browser sessions, with bounded concurrency and
backoff for unavailable companions. Acknowledgements follow durable atomic segment
writes; retries are idempotent. Unacknowledged segments survive reconnects and
server restarts. Quasar records survive its restart. A process crash can lose
unflushed queue data (normally the last five seconds, longer under I/O pressure).
Unexpected shutdown leaves missing health coverage; the UI does not extrapolate
through it. Recording failures cannot propagate from the update loop into game
simulation. Disk failure is reported by collection status.

## Viewing

Recording history is a mode of the main Entity Viewer: its query, focus, and
incident controls live in the existing sidebar, with playback beneath the 3D
viewport. Switch between **Scene** and **Recording history** without leaving the
viewer dialog. Mode navigation retains the original server/entity context and
unloads the previous renderer; only one WebGL context and asset session runs at a
time. The tools shortcut opens the same viewer in history mode. Legacy
`replay.html` links redirect there while preserving query parameters.

**Entity history** is administrator-only, including API requests and player
positions. Responses use `private, no-store`. Select a server and a window of
up to 60 minutes anywhere within the last 24 hours. The incident selector uses a
small persisted navigation index of the latest 4,096 incidents, with segment
activity windows as the legacy fallback. It loads the recorded incident boundaries,
including up to 10 seconds of baseline pre-roll (trimmed at the one-hour limit).
Unfocused cluster history includes every node in the loaded range. Selecting an
entity focuses its recorded surroundings and motion trail, including nearby
observations on other nodes. Connected
incidents expand the context of the entity that opened history; recordings without
incident metadata retain the 500 m context. Unrelated assets are pruned before
preparation. Frame visibility follows the context recorded at that moment, including
reverse seeks, rather than the incident's final size. Camera focus does not enlarge
it. Partial clips keep their original boundaries and can load the full incident.
The existing 512-segment, 8 MiB compressed, 32 MiB expanded/geometry and 40,000-record
query budgets remain in force: dense recordings may require a shorter loaded range,
even when the incident itself lasts longer.

Damage events mark world
positions and can be selected to inspect their recorded evidence. The timeline
aligns damage, suspected physics incidents and simulation speed.

Models, materials, textures (including GPU uploads), and prepared terrain pages
stay loaded while the history window is open. Loading another range or incident
adds only assets not already prepared; scene instances and inactive terrain are
detached without disposing shared assets. The cache grows with the unique assets
viewed and is released on window close or navigation out of history. Changing
servers, selecting different local asset folders, or switching between streaming
and local fallback resets it to avoid mixing asset sources. Already loaded assets
remain a snapshot of those sources until that reset or the window is reopened.

Paused history renders on changes and stops once camera damping settles. Playback,
camera movement, seeks, asset completion, theme changes and resizing wake rendering;
player labels update with rendered frames. Resuming playback does not advance by
the time spent idle. Camera movement reuses the shadow map; scene, asset and
recorded sunlight changes refresh it. Batching reduces submission overhead during
panning and playback; it does not reduce the models' triangle counts. Shadow maps
are released with the window's other GPU resources when history closes.

Playback optionally interpolates translation and quaternion rotation between
observations at most 1.5 seconds apart. It never interpolates geometry or action
state, crosses a control/character change, or bridges explicit gaps, removals,
sessions or motion inconsistent with recorded velocity. Longer suppression
intervals hold the previous observation. **Observations only** disables motion
interpolation and reconstructed character animation. It hides data after removal, expired heartbeats, recording gaps, or a
new recording session on that node slot. Concurrent cluster nodes remain visible;
one node's gap or restart does not replace another node's session. The unfocused
simulation-speed readout shows the slowest currently observed node. Incident clips,
terrain validity and evidence attribution retain their node/session boundaries.
Sessions are separate even if game entity IDs are reused.
Queries include short pre-roll so unchanged entities are visible at the window
start. Seeking backward restores the recorded layout directly, including blocks
that were subsequently destroyed. Missing geometry is explicitly reported.
The selected observation anchors the same natural-gravity reference frame as the
live viewer; old recordings without gravity retain grid-relative orientation.
Hover over a grid/block or player for observation age, capture cadence, motion,
gravity, model availability, integrity, and source entity. Names/details are plain
text. The event list describes damage, suspected instability, departures, cadence
transitions and layout changes. Download the selected entity's event log (or all
entities) as timestamped text. Layout changes are observations, not proof of damage
or player responsibility.

## Incident playback and compact actions

New recordings carry authoritative incident IDs, boundaries and context checkpoints.
Damage, explosions, deaths, block additions/removals, splits/merges, physics warnings
and primary/secondary tool or firing activity trigger them. Held character actions
refresh activity once per second; ordinary motion does not. Active metadata is
checkpointed every five seconds and on deadline/context changes, so mid-incident
queries can recover the original start and current extent. Stopping capture records
a terminal boundary. Explicit gaps and exhausted expansion budgets are flagged.

Old recordings keep their original browser-derived clips (10 seconds of baseline
pre-roll and a 15-second tail). Clip grouping is contextual, not a claim of causation.
Clips reference retained history: no additional archive copy is created. Large
battle context includes actor-local recorded terrain rather than requesting a solid
10 km terrain volume; unavailable coverage remains explicit.

Use 0.1×, 0.25× or 0.5× playback, step to the previous/next recorded observation,
or use Space / arrow shortcuts. The six clickable tracks show damage/deaths/
explosions, actions, grid changes, suspected physics instability, simulation
speed and gaps. Buttons jump to first damage, largest recorded damage amount or
lowest recorded simulation speed within the chosen clip/range. Orbit, follow-grid,
follow-player and free camera are available. Free camera uses WASD/QE, Shift and
drag. The player-perspective camera uses recorded body orientation and sampled
head angles; it is reconstructed, not the player's recorded screen or aim ray.
Older recordings without head angles retain an orbit camera.

Seven action bits represent aim, primary fire/tool, secondary fire/tool, crouch,
sprint, using/seated and death. Four transition-only Harmony hooks observe
`OnBeginShoot`, `StopShooting`, `ZoomMode` and `UsingEntity`; no per-shot, physics
step or per-frame patch runs. Hook failures produce a gap and budgeted player
samples remain available. Native character-death and explosion events enqueue
compact observations. Native `MovementStateChanged` events and baseline samples
record `MyCharacterMovementEnum` as a small optional integer. Flight, falling,
jumping, walking/running/sprinting, crouching, seating and ladder movement remain
distinct. Movement-only events do not escalate recording. Character subscriptions
are removed on replacement, departure or recorder shutdown.
Brief firing pulses between transform samples survive as timestamped transitions.
All new event types share the existing bounded event queue and capture drain.
Head angles and weapon/character IDs are small optional player fields; old data
continues to decode. Empty new identifiers are omitted from recorded JSON.

The browser reconstructs poses deterministically from time, movement and action
state using the MWM bind skeleton and Three.js GPU skinning. World velocity is never
used to infer walking: flying and standing aboard moving ships have no walking
gait. Old recordings without movement state show it as unknown. Animation is illustrative,
including firing recoil, crouch, seating and death; it does not reconstruct exact
server hitboxes or projectile trajectories. Real recorded layouts remain authoritative.
Recent damaged cells have a bounded wireframe highlight and recorded explosion
radii have translucent markers. Split/merge links are included in evidence.

Select a timeline/list event to inspect its reported source and any unique player
observed controlling that character, weapon, seat or grid at the event time.
Missing, ambiguous, stale or gapped control evidence remains **unknown**. The control
observation timestamp is shown; control between samples is not guaranteed. Ownership
and proximity never establish responsibility. Double-click a hovered grid/block/player
to select it and inspect its relevant recent evidence; action events follow the
selected player's historical character ID. Hover distinguishes held from interpolated
motion and names reconstructed animation. All reconstruction stays in the browser.

## Recorder performance

The simulation thread reuses each actor's suppressed sample and vector buffers.
Emitting a sample transfers ownership permanently, even if the bounded queue drops
it. Physics evidence explicitly detaches its buffer; separate action events own
their position. Acceleration detection retains the previous observation's scalar
velocity, including suppressed observations. Gravity arrays shared with queued
samples are replaced, never overwritten.

Periodic block audits compare compact value snapshots. They create replay objects,
arrays, definition strings and logical model paths only when something changes.
Audits still inspect the current model and generated-part state; damage, rebuilding
and topology callbacks retain their existing behavior. Player membership sets and
voxel discovery/expiry scans reuse bounded collections.

The worker caches each unchanged layout's complete immutable dependency set and
attaches it once per segment. Layout edits replace that set. Terrain dependencies
belong to the segment, so they cannot modify a shared layout cache. Queue entries
are value types, batches reuse collections, and segment span/byte totals are
maintained incrementally. Retention examines the oldest indexed segment without
copying the archive. These changes preserve the 0.5 ms update budget checked between
entities, cadence targets, queue limits, segment limits and explicit coverage gaps.

`tests/RecorderBenchmarks` measures production scheduling, incident tracking, layout
attachment and archive pruning. It reports median time and thread allocations over
five runs after two warm-ups, with setup excluded and tiered compilation disabled
to avoid JIT transitions during short measurements. It asserts unchanged heartbeat,
incident and dependency counts. Run the baseline and current versions sequentially:

```bash
recorder_baseline=$(mktemp -d)
git archive a87994756aef1842b1809e9787c597ad312a35ee \
  src/Replay src/CometWorks.EntityViewer.Magnetar/Replay | tar -x -C "$recorder_baseline"
dotnet run -c Release --project tests/RecorderBenchmarks \
  -p:RecorderSourceRoot="$recorder_baseline" -p:DefineConstants=BASELINE
dotnet run -c Release --project tests/RecorderBenchmarks
rm -rf "$recorder_baseline"
```

Reference results (Linux x64, .NET 10, 2026-10-20; MB is decimal):

| Workload | Before ms | After ms | Before allocated MB | After allocated MB |
| --- | ---: | ---: | ---: | ---: |
| 1,000 idle grids × 60 observations; 2,000 emitted | 6.48 | 3.10 | 40.32 | 2.02 |
| 100,000 stationary activity triggers; all metadata retained | 40.84 | 8.12 | 127.20 | 76.00 |
| 100,000 context lookups across 32 incidents | 10.86 | 8.04 | 0 | 0 |
| 10,000 unchanged layout attachments; 50,000 blocks | 171.61 | 0.15 | 287.44 | 0 |
| 1,000 retention checks; 5,000 unexpired segments | 62.12 | 0.14 | 40.42 | 0.30 |

These are isolated steady-state measurements, not whole-server CPU or allocation
claims. The layout test excludes initial encoding and edits; retention excludes
expiry/deletion I/O. Game API calls, block audits, serialization, disk writes,
transport, runtime differences and live battle load require dedicated-server
profiling. The same record/dependency counts are retained; savings do not come from
reducing recorded detail.

## Checks

```bash
dotnet build src/CometWorks.EntityViewer.Magnetar/CometWorks.EntityViewer.Magnetar.csproj
dotnet build src/CometWorks.EntityViewer.Quasar/CometWorks.EntityViewer.Quasar.csproj
dotnet run --project tests/ReplayChecks/ReplayChecks.csproj
dotnet test tests/UiChecks/UiChecks.csproj
npm test
npm run check
```

ReplayChecks covers idle suppression/heartbeats, cadence transitions and quiet
tails, sustained-motion detection, compression, idempotent writes, spool restart,
acknowledgements, storage quota/retention, default-disabled silence, admin recording
control, persistent opt-out, collection without a browser, admin-only
HTTP access, lossless IDs and offline querying after Quasar restart. Cluster checks
cover explicit membership, node aliases, shared recording control, new-node policy
inheritance, offline restart, combined incidents and queries, and isolation from
other clusters and standalone servers. Geometry
checks cover partial captures, destruction/rebuilding, integrity changes, page
deduplication, corrupt hashes, retention dependency survival and real writer
materialization. Context checks cover terrain checkpoints, malformed data, dependency
validation and shared armor templates. Sample-buffer checks cover immutable emitted
and detached evidence, suppressed acceleration and appearance thresholds. Writer
checks cover cached layouts mixed with terrain across record-count, out-of-order
timestamp and geometry-byte segment boundaries. Incident checks cover timing, merging, caps,
settings persistence and transport validation. Browser checks include texture-safe
material cloning, uncapped model preparation, spatial growth and reverse filtering,
reverse terrain seeks and stale
checkpoint rejection. Streaming checks include same-size/same-timestamp replacements. Its synthetic
size/timing sample measures scheduling and compression only, not live server cost.
Browser tests cover replay seeking, destruction/rebuilding in both directions,
missing layout pages, removal, missing coverage, session changes and trail gaps. Live acceptance must still compare recorder enabled/disabled during
normal movement, explosions and sustained physics incidents, including capture
cost, GC allocation, spool growth and recovery after an actual server crash.
