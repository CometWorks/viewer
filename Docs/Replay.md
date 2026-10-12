# Contextual replay — initial implementation

The recorder runs in the Entity Viewer Magnetar companion, without an open
browser. Quasar's viewer plugin collects and archives data through the existing
companion channel. Magnetar core requires no new runtime service or physics patch.
The Quasar abstraction adds connected-server discovery for background companion
consumers. Standalone history uses the stable server name; clustered nodes use
the existing telemetry identity (node/slot/epoch), and requests address the exact
live agent. Cross-node incident stitching is not implemented. Matching Quasar and viewer revisions must be installed.

## Capture and escalation

- Target baseline: one observation per second for loaded grids and online players.
- Target incident cadence: five observations per second for affected grids and
  entities within 1 km of an incident, with a 15-second quiet tail on each track.
  A nearby track's quiet tail can continue after its incident region expires.
- Damage triggers immediately schedule the target grid. Concurrent regions are
  bounded to 32. Repeated damage extends its region; overlapping regions reuse
  the same per-entity recording schedule.
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
- Damage hooks observe `MyDamageSystem` after-damage callbacks for slim blocks.
  Amount is the callback's damage amount, **not a claimed integrity delta**. The
  game can accumulate damage and apply it later. Matching target block, source,
  damage type/deformation flag and 250 ms bucket are coalesced into a total/count.
  Other game damage paths and exact block destruction attribution still require
  live scenario coverage. Entity disappearance is recorded separately.
- Every five seconds a health record includes simulation speed and tracked-grid
  count. Compact block layouts record definition/model path, cell extents,
  orientation, color, build progress and integrity. Inventories, voxel/terrain
  changes, textures, fine deformation, debris and animated subparts are omitted.

## Historical grids and destruction

Block additions, removals and integrity changes use the game's grid events. The
capture loop observes affected cells within its work budget; the next scheduled
sample contains their updated layout. Destruction timing therefore has the same
sampling limits as movement (1 Hz baseline, 5 Hz during incidents). A background
walk on the simulation thread also revisits blocks every 30 seconds to observe
paint and other changes without reliable events. Initial layout capture is spread
across updates. Structural changes restart an invalidated iterator; incomplete or
over-budget grids remain visibly bounds-only until a complete capture succeeds.
Splits produce separately discovered entities; merges invalidate and recapture
rebased layouts. This is observed geometry, not a deterministic replay of physics.

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
normal maps, glass and HSV paint shader, with instanced rendering per page/model.
Configured server asset streaming and local Content/Mods folders supply models
and textures, never a current grid snapshot. It actively preloads assets from all
loaded historical pages and character appearances, including later observations,
with four concurrent model jobs, 128 model admissions, 64 MiB of admitted model
geometry, and 256 texture selections. The shared texture loader bounds file reads
and GPU uploads. These browser limits do not add server recording work; unavailable
assets or assets beyond the limits are reported. Only changed pages or pages whose
missing models became available rebuild while scrubbing. Reloading cancels old
asset generations and disposes their GPU resources.

Missing models use wireframe block proxies (or generated armor topology); missing
historical pages use bounds only. Characters use recorded model/paint and nametags
in their asset's static pose. Missing character assets use a labelled approximate
humanoid. Animation bones, equipment/skin overrides, LCD contents, runtime lights,
emissive changes, terrain, debris and fine deformation are not reconstructed.
Assets are not version-pinned: a changed game/mod asset can alter the appearance of
recorded geometry. Integrity is available in block diagnostics; the fallback armor
also uses a red integrity tint.

## Performance and limits

Capture yields between entities and blocks after a 0.5 ms elapsed-work budget per update.
It examines at most 128 grid slots per update, tracks at most 8192 grids, and
bounds pending damage to 1024 callbacks and pending output to 4096 records. Player
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
40,000 records, sharing repeated page data within each response. The browser
renders at most 50,000 blocks and 128 different external models at once.
Archive file metadata is indexed in memory rather than rescanning the directory
for every collection. Allocation-accounted storage limits (4 KiB minimum/file):

| Storage | Limit | Location |
|---|---|---|
| Companion unacknowledged spool | 64 MiB | `<world>/EntityViewerReplay` |
| Quasar retained archive | 256 MiB/server | `<Quasar>/Data/EntityViewerReplay/<server hash>` |

Both stores expire segments after 24 hours while running; expired data is excluded
from all reads. Quasar also expires data for disconnected servers. A stopped
process cannot delete files: expired spool files are pruned when that world next
runs. Storage pressure can shorten history; the archive reports lost segments
and persists its loss counter. These are protective caps, not expected daily
volume. Detailed live overhead and active-server bytes/day remain unmeasured.

Quasar polls independently of browser sessions, with bounded concurrency and
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
up to 15 minutes anywhere within the last 24 hours; the incident-window selector
uses small segment metadata rather than loading a day's samples. Select an entity
to focus its recorded surroundings and motion trail. Damage events mark world
positions and can be selected to inspect their recorded evidence. The timeline
aligns damage, suspected physics incidents and simulation speed.

Playback holds the most recent observation, without inventing intermediate
positions. It hides data after removal, expired heartbeats, recording gaps, or a
new recording session. Sessions are separate even if game entity IDs are reused.
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
acknowledgements, storage quota/retention, collection without a browser, admin-only
HTTP access, lossless IDs and offline querying after Quasar restart. Geometry
checks cover partial captures, destruction/rebuilding, integrity changes, page
deduplication, corrupt hashes, retention dependency survival and real writer
materialization. Its synthetic
size/timing sample measures scheduling and compression only, not live server cost.
Browser tests cover replay seeking, destruction/rebuilding in both directions,
missing layout pages, removal, missing coverage, session changes and trail gaps. Live acceptance must still compare recorder enabled/disabled during
normal movement, explosions and sustained physics incidents, including capture
cost, GC allocation, spool growth and recovery after an actual server crash.
