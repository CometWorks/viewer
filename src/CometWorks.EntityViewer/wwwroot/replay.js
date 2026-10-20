import * as THREE from "three";
import { movementState } from "./replay-movement.js";
import { animateReplayCharacter } from "./replay-character.js";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { buildReplay, pruneReplayContext, frameAt, speed, trailAt, blockCountAt, describeEvent, replayJournal, incidentClips, observationStep, eventLane, evidenceAt, actionLabels, eventMatches } from "./replay-model.js";
import { state } from "./state.js";
import { replayViewRotation, diagnosticText, updateReplaySun } from "./replay-scene.js";
import { SUN_SHADOW_MAP_SIZE } from "./scene.js";
import { startQuasarThemeSync, themeColor } from "./theme.js";

import { fetchAssetStreamingStatus, prepareRemoteAssetSession, getRemoteAssetSessionKey } from "./asset-streaming.js";
import { ReplayTerrain } from "./replay-terrain.js";
import { ReplayGrids } from "./replay-grids.js";
import { restoreContentFolder, restoreModsFolder, pickContentFolder, pickModsFolder, clearAssetFolderCaches, getAssetFolderCacheGeneration, setSceneModRoots } from "./content-folder.js";

startQuasarThemeSync();
const el = id => document.getElementById(id);
const params = new URLSearchParams(location.search);
const initialServer = params.get("serverId") || params.get("agentId");
let selected = params.get("entityId") || "";
let replay = buildReplay([]), from = 0, to = 0, at = 0, playing = false, previous = null;
let renderFrame = null;
let abort, generation = 0, preparing = false;
let assetServer = "", assetFolderGeneration = -1, remoteAssetsActive = false;
const assetMods = new Map(), uploadedTextures = new WeakSet();
let origin = new THREE.Vector3();
const meshes = new Map();
const grids = new Map();
const players = new Map();
let journal = [], clips = [], activeClip = null, inspectedEvent = null;
let followPosition = null, trailKey = "";
const keys = new Set();
let redrawPending = false;
const gridRenderer = new ReplayGrids(() => {
    if (redrawPending) return;
    redrawPending = true; requestAnimationFrame(() => { redrawPending = false; draw(); });
});
const damageGeometry = new THREE.BoxGeometry(1, 1, 1);
const damageMaterial = new THREE.MeshBasicMaterial({ color: 0xff6b78, wireframe: true, depthTest: false });
const eventGeometry = new THREE.SphereGeometry(1, 8, 6);
const eventMaterial = new THREE.MeshBasicMaterial({ color: 0xffb454, transparent: true, opacity: .22, depthWrite: false });
const markers = Array.from({ length: 32 }, () => { const marker = new THREE.Mesh(eventGeometry, eventMaterial); marker.visible = false; return marker; });
const trailGeometry = new THREE.BufferGeometry();
const trailMaterial = new THREE.LineBasicMaterial({ color: 0xffb454 });
const trail = new THREE.Line(trailGeometry, trailMaterial);
const viewport = el("replayViewport");
const scene = new THREE.Scene();
const world = new THREE.Group(); scene.add(world);
const terrainRenderer = new ReplayTerrain(world);
function applyTheme() { scene.background = new THREE.Color(themeColor("--bg", 0x070b12)); requestRender(); }
applyTheme();
window.addEventListener("quasar-entity-viewer-theme-changed", applyTheme);
world.add(...markers, trail);
const ambient = new THREE.AmbientLight(0xffffff, 1);
const sunlight = new THREE.DirectionalLight();
sunlight.castShadow = true; sunlight.visible = false;
sunlight.shadow.mapSize.set(SUN_SHADOW_MAP_SIZE, SUN_SHADOW_MAP_SIZE);
scene.add(ambient, sunlight, sunlight.target);
const camera = new THREE.PerspectiveCamera(55, 1, 0.1, 1000000);
const renderer = new THREE.WebGLRenderer({ antialias: true });
state.renderer = renderer; state.viewerDisposed = false;
renderer.setPixelRatio(Math.min(devicePixelRatio, 1.75));
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.shadowMap.enabled = true; renderer.shadowMap.type = THREE.PCFSoftShadowMap;
renderer.shadowMap.autoUpdate = false;
viewport.appendChild(renderer.domElement);
renderer.domElement.addEventListener("webglcontextrestored", invalidateReplayShadows);
const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
controls.addEventListener("change", requestRender);
camera.position.set(200, 200, 200);
const geometry = new THREE.BoxGeometry(1, 1, 1);
const headGeometry = new THREE.SphereGeometry(0.16, 12, 8);
const bodyMaterial = new THREE.MeshStandardMaterial({ color: 0x63e5a1, roughness: 0.8 });
function releasePlayer(entry) {
    gridRenderer.release(entry.group); world.remove(entry.group); entry.label.remove();
}
function playerFor(key, sample) {
    let entry = players.get(key);
    const appearance = `${sample.model}/${sample.mod}/${sample.color}/${gridRenderer.revision}`;
    if (entry && entry.appearance !== appearance) { releasePlayer(entry); players.delete(key); entry = null; }
    if (!entry) {
        const group = new THREE.Group(), model = gridRenderer.character(sample);
        if (model) group.add(model);
        else {
            const head = new THREE.Mesh(headGeometry, bodyMaterial); head.position.y = 1.65; head.castShadow = true; head.receiveShadow = true; group.add(head);
            for (const [x, y, sx, sy, sz] of [[0, 1.15, .5, .65, .3], [-.34, 1.05, .16, .7, .18], [.34, 1.05, .16, .7, .18], [-.14, .4, .2, .8, .23], [.14, .4, .2, .8, .23]]) {
                const part = new THREE.Mesh(geometry, bodyMaterial); part.position.set(x, y, 0); part.scale.set(sx, sy, sz); part.castShadow = true; part.receiveShadow = true; group.add(part);
            }
        }
        const label = document.createElement("span"); label.className = "replay-player-label"; el("playerLabels").append(label);
        entry = { group, label, appearance, model: !!model, character: model }; players.set(key, entry); world.add(group);
    }
    entry.label.textContent = `${sample.name || sample.entity}${entry.model ? "" : " · approximate"} · ${movementState(sample.movement)} · ${actionLabels(sample.actions)}`;
    entry.group.userData.sample = sample; entry.group.userData.assetKind = entry.character?.isSkinnedMesh ? "character model (GPU skinning; reconstructed animation)" : entry.model ? "character model (static pose; skeleton unavailable)" : "approximate character; asset not recorded or unavailable";
    entry.group.visible = true; entry.group.position.fromArray(sample.origin || sample.position).sub(origin);
    entry.group.scale.setScalar(sample.modelScale || 1);
    entry.group.quaternion.fromArray(sample.rotation || [0, 0, 0, 1]).normalize();
    animateReplayCharacter(entry.character, sample, at, !el("observedOnly").checked);
    if (!entry.character?.isSkinnedMesh && sample.actions & 64 && !el("observedOnly").checked) entry.group.rotateZ(Math.PI / 2);
}

const materials = Object.fromEntries([["player", 0x63e5a1], ["high", 0xffb454], ["low", 0x68b7ff]].map(([key, color]) => [key, new THREE.MeshBasicMaterial({ color, wireframe: true })]));
const resize = new ResizeObserver(() => {
    renderer.setSize(viewport.clientWidth, viewport.clientHeight);
    camera.aspect = viewport.clientWidth / viewport.clientHeight; camera.updateProjectionMatrix(); plot(); requestRender();
});
resize.observe(viewport);

async function request(path, signal, options = {}) {
    const response = await fetch(new URL(`./api/replay/${path}`, import.meta.url), { credentials: "same-origin", signal, ...options });
    if (!response.ok) {
        if (response.status === 403 || response.status === 401) throw new Error("Administrator access is required to view recording history.");
        const body = await response.json().catch(() => ({}));
        throw new Error(body.error || `History request failed (${response.status}).`);
    }
    return response.json();
}
function recordingStatus(status) {
    el("recordingEnabled").checked = !!status.recordingEnabled;
    el("recordingEnabled").disabled = !el("server").value;
    el("recordingStatus").textContent = status.recordingEnabled
        ? `Recording enabled: 1 Hz baseline, up to 5 Hz during incidents.${status.error ? ` Server status: ${status.error}` : ""}`
        : `Recording disabled. Existing history remains available until expiry.${status.error ? ` Stop pending: ${status.error}. Capture stops within 30 seconds without renewal.` : ""}`;
}
function localTime(date) { return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 19); }
el("from").value = localTime(new Date(Date.now() - 5 * 60000));
el("from").min = localTime(new Date(Date.now() - 24 * 3600000));
el("from").max = localTime(new Date());

async function start() {
    try {
        await Promise.allSettled([restoreContentFolder(), restoreModsFolder(), fetchAssetStreamingStatus()]);
        const servers = await request("servers");
        for (const server of servers) {
            const option = new Option(server.displayName || server.serverId, server.serverId);
            option.dataset.agentId = server.agentId || "";
            option.selected = [server.serverId, server.agentId].includes(initialServer);
            el("server").append(option);
        }
        if (!servers.length) { el("status").textContent = "No servers available yet. Connect a server to enable recording here."; return; }
        if (initialServer && !servers.some(server => [server.serverId, server.agentId].includes(initialServer))) {
            const missing = new Option("No recording for the selected server", "", true, true);
            missing.disabled = true; el("server").prepend(missing); el("server").value = "";
            el("status").textContent = "No recording is available for this server. Choose another server to inspect its history.";
            return;
        }
        await load();
    } catch (error) { el("status").textContent = error.message; }
}
async function load() {
    if (state.viewerDisposed) return;
    abort?.abort(); abort = new AbortController(); const current = ++generation;
    preparing = true; el("assetLoading").hidden = false;
    el("assetLoadingText").textContent = "Loading recorded history…";
    playing = false; el("play").disabled = true; el("scrub").disabled = true; el("play").textContent = "Play"; el("load").disabled = true;
    const requestedFrom = new Date(el("from").value).getTime();
    const requestedTo = Math.min(Date.now(), requestedFrom + Number(el("minutes").value) * 60000);
    try {
        if (!Number.isFinite(requestedFrom) || requestedTo <= requestedFrom) throw new Error("Choose a start time in the last 24 hours.");
        el("status").textContent = "Loading recorded observations…";
        const server = encodeURIComponent(el("server").value);
        const overview = await request(`${server}/overview`, abort.signal);
        if (current !== generation) return;
        recordingStatus(overview);
        const batches = await request(`${server}?from=${requestedFrom}&to=${requestedTo}`, abort.signal);
        if (current !== generation) return;
        for (const mesh of meshes.values()) world.remove(mesh); meshes.clear();
        for (const group of grids.values()) { gridRenderer.release(group); world.remove(group); } grids.clear();
        for (const entry of players.values()) releasePlayer(entry); players.clear();
        terrainRenderer.reset();
        // Range changes replace instances, while the window owns reusable assets.
        // A different server or local folder must never reuse the old source's assets.
        if (assetServer !== server || assetFolderGeneration !== getAssetFolderCacheGeneration()) {
            terrainRenderer.clear(); gridRenderer.clear(); clearAssetFolderCaches();
            assetMods.clear(); assetServer = server; assetFolderGeneration = getAssetFolderCacheGeneration();
        }
        const originalServer = [el("server").value, el("server").selectedOptions[0]?.dataset.agentId].includes(initialServer);
        const loaded = pruneReplayContext(buildReplay(batches), (originalServer ? params.get("entityId") : "") || selected);
        const mods = [...new Set([...loaded.geometry.values()].flatMap(page => page.map(block => block.mod)).concat(loaded.records.map(record => record.mod), [...loaded.voxels.values()].flatMap(page => page.palette.map(material => material.mod))).filter(Boolean))]
            .map(name => ({ rootId: `mods-direct:${name}`, name, publishedFileId: /^\d+(?:\.sbm)?$/.test(name) ? Number(name.replace(/\.sbm$/, "")) : 0 }));
        for (const mod of mods) assetMods.set(mod.rootId, mod);
        const retainedMods = [...assetMods.values()];
        await setSceneModRoots(retainedMods);
        if (current !== generation) return;
        try { await prepareRemoteAssetSession({ mods: retainedMods }, { agentId: el("server").selectedOptions[0]?.dataset.agentId || "", entityId: selected }); } catch { /* Local assets and explicit proxies remain available. */ }
        if (current !== generation) return;
        const remoteActive = !!getRemoteAssetSessionKey();
        if (remoteActive !== remoteAssetsActive) {
            terrainRenderer.clear(); gridRenderer.clear(); clearAssetFolderCaches();
            assetFolderGeneration = getAssetFolderCacheGeneration(); remoteAssetsActive = remoteActive;
        }
        replay = loaded; journal = replayJournal(replay); from = requestedFrom; to = requestedTo; at = from;
        clips = incidentClips(replay, from, to); activeClip = null; inspectedEvent = null; followPosition = null;
        el("clip").replaceChildren(new Option("Entire loaded range", ""));
        clips.forEach((clip, i) => el("clip").append(new Option(`${new Date(clip.events[0].at).toLocaleTimeString()} · ${clip.events.length} events${clip.incomplete ? " · incomplete" : ""}`, String(i))));
        const progress = () => { if (current === generation) el("assetLoadingText").textContent = `${gridRenderer.status} · ${terrainRenderer.ready}/${replay.voxels.size} terrain chunks prepared`; };
        const progressTimer = setInterval(progress, 150);
        try { await Promise.all([gridRenderer.preload(replay), terrainRenderer.preload(replay, gridRenderer.context, progress)]); }
        finally { clearInterval(progressTimer); }
        if (current !== generation) return;
        el("assetLoadingText").textContent = "Uploading replay textures to the GPU…";
        let uploaded = 0;
        for (const texture of new Set(gridRenderer.context.preloadedTextures.values())) {
            if (current !== generation) return;
            if (!texture || uploadedTextures.has(texture)) continue;
            renderer.initTexture(texture); uploadedTextures.add(texture);
            if (++uploaded % 8 === 0) await new Promise(resolve => requestAnimationFrame(resolve));
        }
        preparing = false;
        const entities = new Map(replay.records.filter(r => r.kind === "sample").map(r => [r.entity, r]));
        el("entity").replaceChildren(new Option("All entities", ""));
        for (const record of entities.values()) el("entity").append(new Option(`${record.name || record.entity} (${record.type})`, record.entity));
        el("entity").value = entities.has(selected) ? selected : "";
        selected = el("entity").value;
        el("scrub").min = String(from); el("scrub").max = String(to); el("scrub").disabled = !replay.records.length;
        el("play").disabled = true;
        const first = frameAt(replay, at, selected).samples.find(r => !selected || r.entity === selected);
        origin.fromArray(first?.position || [0, 0, 0]);
        world.quaternion.copy(replayViewRotation(first));
        controls.target.set(0, 0, 0);
        const distance = Math.max(6, Math.max(...(first?.size || [40])) * 2);
        camera.position.set(distance, distance * 0.7, distance);
        const windows = new Map();
        for (const segment of overview.segments || []) if (segment.events > 0) {
            const minute = Math.floor(segment.at / 60000) * 60000;
            windows.set(minute, (windows.get(minute) || 0) + segment.events);
        }
        el("incidents").replaceChildren(new Option("Choose an incident window", ""));
        for (const [minute, count] of [...windows].sort((a, b) => b[0] - a[0])) el("incidents").append(new Option(`${new Date(minute).toLocaleString()} · ${count} events`, String(minute)));
        const unavailable = overview.error ? ` Collection status: ${overview.error}` : "";
        el("status").textContent = `${replay.records.length.toLocaleString()} observations loaded within 500 m of ${replay.contextEntity || "the context anchor"}. ${replay.events.some(e => e.kind === "voxel-coverage") ? "Terrain capture budget reached in this window; context is incomplete. " : ""} ${overview.lostChunks || 0} segments lost to storage limits.${unavailable}`;
        events(); plot(); draw();
        el("assetLoadingText").textContent = "Preparing replay shaders…";
        await renderer.compileAsync(scene, camera);
        if (current === generation) el("play").disabled = !replay.records.length;
    } catch (error) {
        if (current === generation && error.name !== "AbortError") { el("status").textContent = error.message; replay = buildReplay([]); draw(); }
    } finally { if (current === generation) { el("load").disabled = false; preparing = false; el("assetLoading").hidden = true; } }
}
function events() {
    el("events").replaceChildren();
    const type = el("eventFilter").value;
    const matches = journal.filter(r => r.at >= (activeClip?.start ?? from) && r.at <= (activeClip?.end ?? to) && (!type || r.kind === type) && eventMatches(replay, r, selected));
    for (const record of matches.slice(0, 500)) {
        const button = document.createElement("button"); button.type = "button";
        button.textContent = `${new Date(record.at).toLocaleTimeString()} · ${describeEvent(record)}`;
        button.addEventListener("click", () => {
            inspect(record);
        });
        el("events").append(button);
    }
    if (matches.length > 500) el("events").append(document.createTextNode("Showing 500 events. Narrow the time range for more detail."));
}
function draw() {
    if (preparing || state.viewerDisposed) return;
    const frame = frameAt(replay, at, selected, !el("observedOnly").checked);
    for (const mesh of meshes.values()) mesh.visible = false;
    for (const group of grids.values()) group.visible = false;
    for (const entry of players.values()) entry.group.visible = false;
    el("replayHover").hidden = true; hovered = null;
    let drawnBlocks = 0, proxies = 0, missingLayouts = 0;
    for (const sample of frame.samples) {
        const key = `${sample.session}/${sample.entity}`;
        if (sample.type === "player") { playerFor(key, sample); continue; }
        const blockCount = sample.type !== "player" ? blockCountAt(replay, sample) : null;
        if (blockCount !== null && sample.origin) {
            let group = grids.get(key);
            if (!group) { group = new THREE.Group(); grids.set(key, group); world.add(group); }
            group.userData.sample = sample;
            group.visible = true; group.position.fromArray(sample.origin).sub(origin);
            group.quaternion.fromArray(sample.rotation || [0, 0, 0, 1]).normalize();
            const detail = gridRenderer.update(group, sample, replay.geometry);
            drawnBlocks += detail.blocks; proxies += detail.proxies;
            continue;
        }
        if (sample.type !== "player") missingLayouts++;
        let mesh = meshes.get(key);
        if (!mesh) { mesh = new THREE.Mesh(geometry, materials.low); meshes.set(key, mesh); world.add(mesh); }
        mesh.userData.sample = sample;
        mesh.visible = true;
        mesh.material = materials[sample.type === "player" ? "player" : sample.high ? "high" : "low"];
        mesh.position.fromArray(sample.position).sub(origin);
        mesh.scale.fromArray(sample.size || [2, 2, 2]).max(new THREE.Vector3(0.5, 0.5, 0.5));
        mesh.quaternion.fromArray(sample.rotation || [0, 0, 0, 1]).normalize();
    }
    // Hidden historical entities must not retain an unbounded number of GPU instances while scrubbing.
    for (const [key, group] of grids) if (!group.visible) { gridRenderer.release(group); world.remove(group); grids.delete(key); }
    for (const [key, entry] of players) if (!entry.group.visible) { releasePlayer(entry); players.delete(key); }
    updateCamera(frame);
    const focused = frame.samples.find(s => s.entity === selected);
    trail.visible = !!focused;
    const nextTrailKey = focused ? `${focused.session}/${focused.entity}/${focused.at}/${origin.toArray()}` : "";
    if (focused && nextTrailKey !== trailKey) {
        const points = trailAt(replay, focused, at).map(p => new THREE.Vector3().fromArray(p).sub(origin));
        trail.geometry.dispose(); trail.geometry = new THREE.BufferGeometry().setFromPoints(points);
        trail.visible = points.length > 1;
    }
    trailKey = nextTrailKey;
    const recent = replay.events.filter(e => ["damage", "explosion", "block-removed"].includes(e.kind) && e.position && e.at <= at && e.at >= at - 1000 && e.session === frame.health?.session).slice(-32);
    markers.forEach((marker, i) => {
        const event = recent[i]; marker.visible = !!event; if (!event) return;
        const target = event.cell && frame.samples.find(s => s.entity === event.entity && s.origin && s.gridSize);
        marker.geometry = target ? damageGeometry : eventGeometry;
        marker.material = target ? damageMaterial : eventMaterial;
        marker.quaternion.identity();
        if (target) {
            marker.quaternion.fromArray(target.rotation || [0, 0, 0, 1]).normalize();
            marker.position.fromArray(event.cell).multiplyScalar(target.gridSize).applyQuaternion(marker.quaternion).add(new THREE.Vector3().fromArray(target.origin)).sub(origin);
            marker.scale.setScalar(target.gridSize * 1.03);
        } else { marker.position.fromArray(event.position).sub(origin); marker.scale.setScalar(event.kind === "explosion" ? Math.min(1000, Math.max(1, event.value || 1)) : 2); }
    });
    el("scrub").value = String(at);
    el("tracks").setAttribute("aria-valuenow", String(Math.round((at - from) / Math.max(1, to - from) * 100)));
    el("tracks").setAttribute("aria-valuetext", new Date(at || Date.now()).toLocaleString());
    el("clock").textContent = new Date(at || Date.now()).toLocaleString();
    const health = frame.health;
    el("quality").textContent = frame.gap ? "Missing or incomplete coverage" : `${frame.samples.length} entities · sim speed ${Number(health?.value || 0).toFixed(2)} · ${frame.samples.filter(s => s.high).length} in incident detail · ${el("observedOnly").checked ? "observations only" : "reconstructed motion"}`;
    const terrainQuality = terrainRenderer.draw(replay, at, frame.samples[0]?.session, origin, frame.samples.find(s => s.entity === replay.contextEntity)?.position || focused?.position);
    el("lightingQuality").textContent = updateReplaySun(sunlight, ambient, frame, origin, world.quaternion, selected, el("replayLighting").checked);
    el("geometryQuality").textContent = `${drawnBlocks.toLocaleString()} recorded blocks · ${proxies.toLocaleString()} block proxies · ${missingLayouts} grids with bounds only · ${gridRenderer.status} · ${terrainQuality} · ${frame.samples.some(s => s.gravity) ? "recorded natural gravity" : "gravity not recorded"}`;
    invalidateReplayShadows();
}
const laneNames = ["Damage", "Actions", "Grid changes", "Physics", "Sim speed", "Gaps"];
const laneColors = ["#ff6b78", "#c6a0ff", "#68b7ff", "#ffb454", "#63e5a1", "#999"];
function plot() {
    const canvas = el("tracks"); canvas.width = Math.max(1, canvas.clientWidth);
    const ctx = canvas.getContext("2d"); ctx.clearRect(0, 0, canvas.width, canvas.height);
    if (to <= from) return;
    const x = t => 84 + (t - from) / (to - from) * (canvas.width - 84);
    ctx.font = "11px sans-serif";
    laneNames.forEach((name, lane) => { ctx.fillStyle = laneColors[lane]; ctx.fillText(name, 2, lane * 22 + 15); });
    for (const event of journal) {
        if (event.at < from || event.at > to) continue;
        const lane = eventLane(event); ctx.fillStyle = laneColors[lane];
        ctx.fillRect(x(event.at), lane * 22 + 3, event === inspectedEvent ? 5 : 2, 15);
    }
    ctx.strokeStyle = laneColors[4]; ctx.beginPath(); let previousHealth;
    for (const health of replay.health) {
        if (health.at < from || health.at > to) continue;
        const px = x(health.at), y = 108 - Math.min(1, Math.max(0, health.value || 0)) * 17;
        if (!previousHealth || health.session !== previousHealth.session || health.at - previousHealth.at > 10000) ctx.moveTo(px, y); else ctx.lineTo(px, y);
        previousHealth = health;
    }
    ctx.stroke();
}
function pause() { playing = false; el("play").textContent = "Play"; }
function seek(time) {
    pause(); at = Math.max(from, Math.min(to, time)); followPosition = null;
    if (activeClip && (at < activeClip.start || at > activeClip.end)) { activeClip = null; el("clip").value = ""; events(); }
    draw();
}
function inspect(record) {
    inspectedEvent = record;
    el("evidence").textContent = JSON.stringify(evidenceAt(replay, record), null, 2);
    const entity = replay.tracks.has(`${record.session}/${record.entity}`) ? record.entity : frameAt(replay, record.at).samples.find(s => s.character === record.entity)?.entity;
    if (entity) { selected = entity; el("entity").value = selected; }
    seek(record.at); focusCamera(frameAt(replay, at, selected).samples.find(s => !selected || s.entity === selected)); draw(); events(); plot();
}
function step(direction) { seek(observationStep(replay, at, direction, selected)); }
function focusCamera(sample) {
    if (!sample) return;
    origin.fromArray(sample.position); world.quaternion.copy(replayViewRotation(sample));
    controls.target.set(0, 0, 0); followPosition = null;
    const distance = Math.max(6, Math.max(...(sample.size || [40])) * 2);
    camera.position.set(distance, distance * .7, distance);
}
function updateCamera(frame) {
    const mode = el("cameraMode").value;
    controls.enabled = mode !== "perspective";
    if (mode === "free" || mode === "orbit") { followPosition = null; return; }
    const targetType = mode === "grid" ? "grid" : "player";
    const sample = frame.samples.find(s => s.entity === selected && s.type === targetType) || frame.samples.find(s => s.type === targetType);
    if (!sample) { controls.enabled = true; followPosition = null; return; }
    const position = new THREE.Vector3().fromArray(sample.position).sub(origin).applyQuaternion(world.quaternion);
    if (mode === "perspective") {
        if (!sample.rotation || !sample.view) { controls.enabled = true; return; }
        const rotation = world.quaternion.clone().multiply(new THREE.Quaternion().fromArray(sample.rotation).normalize());
        camera.position.copy(position).add(new THREE.Vector3(0, 1.65, 0).applyQuaternion(rotation));
        camera.quaternion.copy(rotation).multiply(new THREE.Quaternion().setFromEuler(new THREE.Euler(THREE.MathUtils.degToRad(sample.view[0]), THREE.MathUtils.degToRad(sample.view[1]), 0, "YXZ")));
        controls.target.copy(camera.position).add(new THREE.Vector3(0, 0, -5).applyQuaternion(camera.quaternion));
        const entry = players.get(`${sample.session}/${sample.entity}`); if (entry) entry.group.visible = false;
    } else {
        if (followPosition) camera.position.add(position.clone().sub(followPosition));
        else camera.position.add(position.clone().sub(controls.target));
        controls.target.copy(position);
    }
    followPosition = position;
}
function moveFreeCamera(dt) {
    if (el("cameraMode").value !== "free") return;
    const movement = new THREE.Vector3(Number(keys.has("KeyD")) - Number(keys.has("KeyA")), Number(keys.has("KeyE")) - Number(keys.has("KeyQ")), Number(keys.has("KeyS")) - Number(keys.has("KeyW")));
    if (!movement.lengthSq()) return;
    movement.normalize().applyQuaternion(camera.quaternion).multiplyScalar(dt / 1000 * ((keys.has("ShiftLeft") || keys.has("ShiftRight")) ? 200 : 20));
    camera.position.add(movement); controls.target.add(movement);
    return true;
}
el("clip").addEventListener("change", () => {
    activeClip = el("clip").value === "" ? null : clips[Number(el("clip").value)];
    if (activeClip) {
        selected = activeClip.focus; el("entity").value = selected;
        const sample = frameAt(replay, activeClip.events[0].at, selected).samples.find(s => !selected || s.entity === selected);
        if (sample) el("cameraMode").value = sample.type === "player" ? "player" : "grid";
        seek(activeClip.start); focusCamera(sample || (activeClip.position ? { position: activeClip.position } : null)); draw();
    }
    events();
});
el("cameraMode").addEventListener("change", () => {
    followPosition = null;
    if (el("cameraMode").value === "free") controls.target.copy(camera.position).add(new THREE.Vector3(0, 0, -.01).applyQuaternion(camera.quaternion));
    draw();
});
el("observedOnly").addEventListener("change", draw);
el("replayLighting").addEventListener("change", draw);
el("stepBack").addEventListener("click", () => step(-1));
el("stepForward").addEventListener("click", () => step(1));
for (const [id, choose] of [["firstDamage", records => records.find(e => e.kind === "damage")], ["largestImpact", records => records.filter(e => e.kind === "damage").sort((a,b) => b.value - a.value)[0]], ["slowdown", records => records.filter(e => e.kind === "health").sort((a,b) => a.value - b.value)[0]]]) {
    el(id).addEventListener("click", () => { const record = choose(replay.records.filter(e => e.at >= (activeClip?.start ?? from) && e.at <= (activeClip?.end ?? to))); if (record) inspect(record); });
}
el("tracks").addEventListener("click", event => {
    const bounds = el("tracks").getBoundingClientRect(), lane = Math.floor((event.clientY - bounds.top) / 22);
    const time = from + Math.max(0, (event.clientX - bounds.left - 84) / Math.max(1, bounds.width - 84)) * (to - from);
    const records = lane === 4 ? replay.health : journal.filter(e => eventLane(e) === lane);
    const record = records.filter(e => e.at >= from && e.at <= to).reduce((best, e) => !best || Math.abs(e.at - time) < Math.abs(best.at - time) ? e : best, null);
    if (record) inspect(record); else seek(time);
});
window.addEventListener("keydown", event => {
    if (event.target.matches("input, select, textarea, button") || event.target.isContentEditable) return;
    keys.add(event.code);
    if (el("cameraMode").value === "free") requestRender();
    if (event.code === "Space") { event.preventDefault(); el("play").click(); }
    if (event.code === "ArrowLeft" || event.code === "ArrowRight") { event.preventDefault(); step(event.code === "ArrowLeft" ? -1 : 1); }
});
window.addEventListener("keyup", event => keys.delete(event.code));
window.addEventListener("blur", () => keys.clear());
el("query").addEventListener("submit", event => { event.preventDefault(); load(); });
el("server").addEventListener("change", () => { el("recordingEnabled").disabled = true; selected = ""; load(); });
el("recordingEnabled").addEventListener("change", async () => {
    const server = el("server").value, enabled = el("recordingEnabled").checked;
    el("recordingEnabled").disabled = true;
    el("recordingStatus").textContent = enabled ? "Enabling recording…" : "Stopping recording…";
    try {
        const result = await request(`${encodeURIComponent(server)}/recording`, undefined, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled }) });
        if (el("server").value === server) recordingStatus(result);
    } catch (error) {
        if (el("server").value === server) {
            el("recordingStatus").textContent = `Could not confirm recording setting: ${error.message}. Reload history to check.`;
            el("recordingEnabled").disabled = false;
        }
    }
});
el("incidents").addEventListener("change", () => {
    if (!el("incidents").value) return;
    el("from").value = localTime(new Date(Math.max(Date.now() - 24 * 3600000 + 1000, Number(el("incidents").value) - 30000)));
    el("minutes").value = "5"; load();
});
el("scrub").addEventListener("input", () => { seek(Number(el("scrub").value)); });
el("play").addEventListener("click", () => { playing = !playing; if (at >= (activeClip?.end ?? to) || at < (activeClip?.start ?? from)) at = activeClip?.start ?? from; el("play").textContent = playing ? "Pause" : "Play"; requestRender(); });
el("entity").addEventListener("change", () => { selected = el("entity").value; const sample = frameAt(replay, at).samples.find(r => r.entity === selected); focusCamera(sample); events(); draw(); });
el("eventFilter").addEventListener("change", events);
function requestRender() {
    if (!state.viewerDisposed && renderFrame === null) renderFrame = requestAnimationFrame(render);
}
function invalidateReplayShadows() {
    renderer.shadowMap.needsUpdate = true;
    requestRender();
}
function render(time) {
    renderFrame = null;
    if (state.viewerDisposed) return;
    const dt = previous === null ? 0 : time - previous;
    previous = time;
    if (playing) { at = Math.min(activeClip?.end ?? to, at + Math.min(dt, 200) * Number(el("speed").value)); draw(); if (at >= (activeClip?.end ?? to)) pause(); }
    const moving = moveFreeCamera(Math.min(dt, 100));
    // OrbitControls emits change while damping settles, scheduling the next frame.
    if (controls.enabled) controls.update();
    renderer.render(scene, camera);
    for (const entry of players.values()) {
        const point = new THREE.Vector3(0, 2.15, 0).applyMatrix4(entry.group.matrixWorld).project(camera);
        entry.label.hidden = !entry.group.visible || point.z < -1 || point.z > 1;
        entry.label.style.transform = `translate(${(point.x + 1) * viewport.clientWidth / 2}px, ${(1 - point.y) * viewport.clientHeight / 2}px) translate(-50%, -100%)`;
    }
    if (playing || moving) requestRender();
    if (renderFrame === null) previous = null; // Resuming after idle must not advance playback by idle time.
}
for (const [id, pick] of [["contentFolder", pickContentFolder], ["modsFolder", pickModsFolder]]) el(id).addEventListener("click", async () => {
    try { await pick(); await load(); } catch (error) { if (error.name !== "AbortError") el("status").textContent = error.message; }
});

const raycaster = new THREE.Raycaster();
let hoverAt = 0, hovered = null;
renderer.domElement.addEventListener("pointermove", event => {
    if (event.buttons || performance.now() - hoverAt < 100) return;
    hoverAt = performance.now();
    const bounds = renderer.domElement.getBoundingClientRect();
    raycaster.setFromCamera(new THREE.Vector2((event.clientX - bounds.left) / bounds.width * 2 - 1, 1 - (event.clientY - bounds.top) / bounds.height * 2), camera);
    const objects = [];
    world.traverseVisible(object => { if (object.isMesh && !markers.includes(object)) objects.push(object); });
    const hit = raycaster.intersectObjects(objects, false)[0];
    let owner = hit?.object;
    while (owner && !owner.userData.sample) owner = owner.parent;
    hovered = owner ? { sample: owner.userData.sample, block: hit.object.userData.blocks?.[hit.instanceId] } : null;
    el("replayHover").hidden = !owner;
    if (owner) el("replayHover").textContent = diagnosticText(owner.userData.sample, hit.object.userData.blocks?.[hit.instanceId], at, hit.object.userData.assetKind || owner.userData.assetKind);
});
renderer.domElement.addEventListener("pointerleave", () => { hovered = null; el("replayHover").hidden = true; });
renderer.domElement.addEventListener("dblclick", () => {
    if (!hovered) return;
    const { sample, block } = hovered;
    if (sample.type !== "voxel") { selected = sample.entity; el("entity").value = selected; }
    const relevant = replay.events.filter(e => e.at <= at && e.session === sample.session && eventMatches(replay, e, selected)
        && (!block || !e.cell || e.cell.every((value, axis) => value === block.cell[axis]))).slice(-12);
    el("evidence").textContent = JSON.stringify({ selectedEntity: sample.entity, observationAt: sample.at, block, events: relevant.map(e => evidenceAt(replay, e)) }, null, 2);
    focusCamera(sample); events(); draw();
});
el("downloadHistory").addEventListener("click", () => {
    const lines = journal.filter(record => record.at >= from && eventMatches(replay, record, selected))
        .map(record => `${new Date(record.at).toISOString()} [${record.session}] ${describeEvent(record).replaceAll("\r", "\\r").replaceAll("\n", "\\n")}`);
    const url = URL.createObjectURL(new Blob([lines.join("\n")], { type: "text/plain;charset=utf-8" }));
    const link = document.createElement("a"); link.href = url; link.download = "recording-events.txt"; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
});

window.addEventListener("pagehide", () => {
    state.viewerDisposed = true; generation++; abort?.abort();
    resize.disconnect(); cancelAnimationFrame(renderFrame); controls.dispose();
    for (const group of grids.values()) gridRenderer.release(group);
    for (const entry of players.values()) releasePlayer(entry);
    terrainRenderer.clear(); gridRenderer.dispose(); clearAssetFolderCaches(); assetMods.clear();
    sunlight.shadow.dispose();
    geometry.dispose(); headGeometry.dispose(); bodyMaterial.dispose(); eventGeometry.dispose(); eventMaterial.dispose();
    damageGeometry.dispose(); damageMaterial.dispose(); trail.geometry.dispose(); trailMaterial.dispose();
    Object.values(materials).forEach(m => m.dispose()); renderer.dispose(); state.renderer = null;
}, { once: true });
window.addEventListener("pageshow", event => { if (event.persisted) location.reload(); });
start();
