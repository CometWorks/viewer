import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { buildReplay, frameAt, speed, trailAt, blockCountAt, describeEvent, replayJournal } from "./replay-model.js";
import { state } from "./state.js";
import { replayViewRotation, diagnosticText } from "./replay-scene.js";
import { startQuasarThemeSync } from "./theme.js";

import { fetchAssetStreamingStatus, prepareRemoteAssetSession } from "./asset-streaming.js";
import { ReplayGrids } from "./replay-grids.js";
import { restoreContentFolder, restoreModsFolder, pickContentFolder, pickModsFolder, clearAssetFolderCaches, setSceneModRoots } from "./content-folder.js";

startQuasarThemeSync();
const el = id => document.getElementById(id);
const params = new URLSearchParams(location.search);
const initialServer = params.get("serverId") || params.get("agentId");
let selected = params.get("entityId") || "";
let replay = buildReplay([]), from = 0, to = 0, at = 0, playing = false, previous = 0;
let abort, generation = 0;
let origin = new THREE.Vector3();
const meshes = new Map();
const grids = new Map();
const players = new Map();
let journal = [];
let redrawPending = false;
const gridRenderer = new ReplayGrids(() => {
    if (redrawPending) return;
    redrawPending = true; requestAnimationFrame(() => { redrawPending = false; draw(); });
});
const eventGeometry = new THREE.SphereGeometry(1, 8, 6);
const eventMaterial = new THREE.MeshBasicMaterial({ color: 0xff6b78 });
const markers = Array.from({ length: 32 }, () => { const marker = new THREE.Mesh(eventGeometry, eventMaterial); marker.visible = false; return marker; });
const trailGeometry = new THREE.BufferGeometry();
const trailMaterial = new THREE.LineBasicMaterial({ color: 0xffb454 });
const trail = new THREE.Line(trailGeometry, trailMaterial);
const viewport = el("replayViewport");
const scene = new THREE.Scene();
const world = new THREE.Group(); scene.add(world);
scene.background = new THREE.Color(getComputedStyle(document.documentElement).getPropertyValue("--bg").trim() || "#0c1320");
world.add(...markers, trail);
scene.add(new THREE.AmbientLight(0xffffff, 1.4));
const sunlight = new THREE.DirectionalLight(0xffffff, 2); sunlight.position.set(1, 2, 3); scene.add(sunlight);
const camera = new THREE.PerspectiveCamera(55, 1, 0.1, 1000000);
const renderer = new THREE.WebGLRenderer({ antialias: true });
state.renderer = renderer; state.viewerDisposed = false;
renderer.setPixelRatio(Math.min(devicePixelRatio, 1.5));
viewport.appendChild(renderer.domElement);
const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
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
            const head = new THREE.Mesh(headGeometry, bodyMaterial); head.position.y = 1.65; group.add(head);
            for (const [x, y, sx, sy, sz] of [[0, 1.15, .5, .65, .3], [-.34, 1.05, .16, .7, .18], [.34, 1.05, .16, .7, .18], [-.14, .4, .2, .8, .23], [.14, .4, .2, .8, .23]]) {
                const part = new THREE.Mesh(geometry, bodyMaterial); part.position.set(x, y, 0); part.scale.set(sx, sy, sz); group.add(part);
            }
        }
        const label = document.createElement("span"); label.className = "replay-player-label"; el("playerLabels").append(label);
        entry = { group, label, appearance, model: !!model }; players.set(key, entry); world.add(group);
    }
    entry.label.textContent = `${sample.name || sample.entity}${entry.model ? "" : " · approximate"}`;
    entry.group.userData.sample = sample; entry.group.userData.assetKind = entry.model ? "character model (static pose)" : "approximate character; asset not recorded or unavailable";
    entry.group.visible = true; entry.group.position.fromArray(sample.origin || sample.position).sub(origin);
    entry.group.quaternion.fromArray(sample.rotation || [0, 0, 0, 1]).normalize();
}

const materials = Object.fromEntries([["player", 0x63e5a1], ["high", 0xffb454], ["low", 0x68b7ff]].map(([key, color]) => [key, new THREE.MeshBasicMaterial({ color, wireframe: true })]));
const resize = new ResizeObserver(() => {
    renderer.setSize(viewport.clientWidth, viewport.clientHeight);
    camera.aspect = viewport.clientWidth / viewport.clientHeight; camera.updateProjectionMatrix(); plot();
});
resize.observe(viewport);

async function request(path, signal) {
    const response = await fetch(new URL(`./api/replay/${path}`, import.meta.url), { credentials: "same-origin", signal });
    if (!response.ok) {
        if (response.status === 403 || response.status === 401) throw new Error("Administrator access is required to view recording history.");
        const body = await response.json().catch(() => ({}));
        throw new Error(body.error || `History request failed (${response.status}).`);
    }
    return response.json();
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
        if (!servers.length) { el("status").textContent = "No recordings yet. The server companion records automatically while a world is running."; return; }
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
    abort?.abort(); abort = new AbortController(); const current = ++generation;
    playing = false; el("play").textContent = "Play"; el("load").disabled = true;
    const requestedFrom = new Date(el("from").value).getTime();
    const requestedTo = Math.min(Date.now(), requestedFrom + Number(el("minutes").value) * 60000);
    try {
        if (!Number.isFinite(requestedFrom) || requestedTo <= requestedFrom) throw new Error("Choose a start time in the last 24 hours.");
        el("status").textContent = "Loading recorded observations…";
        const server = encodeURIComponent(el("server").value);
        const [batches, overview] = await Promise.all([
            request(`${server}?from=${requestedFrom}&to=${requestedTo}`, abort.signal), request(`${server}/overview`, abort.signal)
        ]);
        if (current !== generation) return;
        for (const mesh of meshes.values()) world.remove(mesh); meshes.clear();
        for (const group of grids.values()) { gridRenderer.release(group); world.remove(group); } grids.clear();
        for (const entry of players.values()) releasePlayer(entry); players.clear();
        gridRenderer.clear(); clearAssetFolderCaches();
        const loaded = buildReplay(batches);
        const mods = [...new Set([...loaded.geometry.values()].flatMap(page => page.map(block => block.mod)).concat(loaded.records.map(record => record.mod)).filter(Boolean))]
            .map(name => ({ rootId: `mods-direct:${name}`, name, publishedFileId: /^\d+(?:\.sbm)?$/.test(name) ? Number(name.replace(/\.sbm$/, "")) : 0 }));
        await setSceneModRoots(mods);
        try { await prepareRemoteAssetSession({ mods }, { agentId: el("server").selectedOptions[0]?.dataset.agentId || "", entityId: selected }); } catch { /* Local assets and explicit proxies remain available. */ }
        if (current !== generation) return;
        replay = loaded; journal = replayJournal(replay); from = requestedFrom; to = requestedTo; at = from;
        gridRenderer.preload(replay);
        const entities = new Map(replay.records.filter(r => r.kind === "sample").map(r => [r.entity, r]));
        el("entity").replaceChildren(new Option("All entities", ""));
        for (const record of entities.values()) el("entity").append(new Option(`${record.name || record.entity} (${record.type})`, record.entity));
        el("entity").value = entities.has(selected) ? selected : "";
        selected = el("entity").value;
        el("scrub").min = String(from); el("scrub").max = String(to); el("scrub").disabled = !replay.records.length;
        el("play").disabled = !replay.records.length;
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
        el("status").textContent = `${replay.records.length.toLocaleString()} observations loaded. ${overview.lostChunks || 0} segments lost to storage limits.${unavailable}`;
        events(); plot(); draw();
    } catch (error) {
        if (error.name !== "AbortError") { el("status").textContent = error.message; replay = buildReplay([]); draw(); }
    } finally { if (current === generation) el("load").disabled = false; }
}
function events() {
    el("events").replaceChildren();
    const type = el("eventFilter").value;
    const matches = journal.filter(r => r.at >= from && (!type || r.kind === type) && (!selected || !r.entity || r.entity === selected));
    for (const record of matches.slice(0, 500)) {
        const button = document.createElement("button"); button.type = "button";
        button.textContent = `${new Date(record.at).toLocaleTimeString()} · ${describeEvent(record)}`;
        button.addEventListener("click", () => {
            at = record.at; playing = false; el("play").textContent = "Play";
            el("evidence").textContent = JSON.stringify({ ...record, observedSpeed: speed(record), sourceEntity: record.source || "unknown", attribution: "Source entity only; player responsibility is not inferred." }, null, 2);
            draw();
        });
        el("events").append(button);
    }
    if (matches.length > 500) el("events").append(document.createTextNode("Showing 500 events. Narrow the time range for more detail."));
}
function draw() {
    const frame = frameAt(replay, at, selected);
    for (const mesh of meshes.values()) mesh.visible = false;
    for (const group of grids.values()) group.visible = false;
    for (const entry of players.values()) entry.group.visible = false;
    el("replayHover").hidden = true;
    let drawnBlocks = 0, proxies = 0, missingLayouts = 0;
    for (const sample of frame.samples) {
        const key = `${sample.session}/${sample.entity}`;
        if (sample.type === "player") { playerFor(key, sample); continue; }
        const blockCount = sample.type !== "player" ? blockCountAt(replay, sample) : null;
        if (blockCount !== null && sample.origin && drawnBlocks + blockCount <= 50000) {
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
    const focused = frame.samples.find(s => s.entity === selected);
    trail.visible = !!focused;
    if (focused) {
        const points = trailAt(replay, focused, at).map(p => new THREE.Vector3().fromArray(p).sub(origin));
        trail.geometry.dispose(); trail.geometry = new THREE.BufferGeometry().setFromPoints(points);
        trail.visible = points.length > 1;
    }
    const recent = replay.events.filter(e => e.kind === "damage" && e.position && e.at <= at && e.at >= at - 1000 && e.session === frame.health?.session).slice(-32);
    markers.forEach((marker, i) => { marker.visible = i < recent.length; if (recent[i]) { marker.position.fromArray(recent[i].position).sub(origin); marker.scale.setScalar(2); } });
    el("scrub").value = String(at);
    el("clock").textContent = new Date(at || Date.now()).toLocaleString();
    const health = frame.health;
    el("quality").textContent = frame.gap ? "Missing or incomplete coverage" : `${frame.samples.length} entities · sim speed ${Number(health?.value || 0).toFixed(2)} · ${frame.samples.filter(s => s.high).length} in incident detail`;
    el("geometryQuality").textContent = `${drawnBlocks.toLocaleString()} recorded blocks · ${proxies.toLocaleString()} block proxies · ${missingLayouts} grids with bounds only · ${gridRenderer.status} · ${frame.samples.some(s => s.gravity) ? "recorded natural gravity" : "gravity not recorded"}`;
}
function plot() {
    const canvas = el("tracks"); canvas.width = Math.max(1, canvas.clientWidth);
    const ctx = canvas.getContext("2d"); ctx.clearRect(0, 0, canvas.width, canvas.height);
    if (to <= from) return;
    const x = t => (t - from) / (to - from) * canvas.width;
    for (const event of replay.events) {
        if (event.at < from || event.at > to) continue;
        ctx.fillStyle = { damage: "#ff6b78", physics: "#ffb454", gap: "#999" }[event.kind];
        ctx.fillRect(x(event.at), { damage: 4, physics: 22, gap: 40 }[event.kind], 2, 14);
    }
    ctx.strokeStyle = "#63e5a1"; ctx.beginPath(); let previousHealth;
    for (const health of replay.health) {
        const px = x(health.at), y = 82 - Math.min(1, Math.max(0, health.value || 0)) * 22;
        if (!previousHealth || health.session !== previousHealth.session || health.at - previousHealth.at > 10000) ctx.moveTo(px, y); else ctx.lineTo(px, y);
        previousHealth = health;
    }
    ctx.stroke();
}
el("query").addEventListener("submit", event => { event.preventDefault(); load(); });
el("incidents").addEventListener("change", () => {
    if (!el("incidents").value) return;
    el("from").value = localTime(new Date(Math.max(Date.now() - 24 * 3600000 + 1000, Number(el("incidents").value) - 30000)));
    el("minutes").value = "5"; load();
});
el("scrub").addEventListener("input", () => { at = Number(el("scrub").value); draw(); });
el("play").addEventListener("click", () => { playing = !playing; if (at >= to) at = from; el("play").textContent = playing ? "Pause" : "Play"; });
el("entity").addEventListener("change", () => { selected = el("entity").value; const sample = frameAt(replay, at).samples.find(r => r.entity === selected); if (sample) { origin.fromArray(sample.position); world.quaternion.copy(replayViewRotation(sample)); controls.target.set(0, 0, 0); } events(); draw(); });
el("eventFilter").addEventListener("change", events);
renderer.setAnimationLoop(time => {
    if (playing && previous) { at = Math.min(to, at + Math.min(time - previous, 200) * Number(el("speed").value)); draw(); if (at >= to) { playing = false; el("play").textContent = "Play"; } }
    previous = time; controls.update(); renderer.render(scene, camera);
    for (const entry of players.values()) {
        const point = new THREE.Vector3(0, 2.15, 0).applyMatrix4(entry.group.matrixWorld).project(camera);
        entry.label.hidden = !entry.group.visible || point.z < -1 || point.z > 1;
        entry.label.style.transform = `translate(${(point.x + 1) * viewport.clientWidth / 2}px, ${(1 - point.y) * viewport.clientHeight / 2}px) translate(-50%, -100%)`;
    }
});
for (const [id, pick] of [["contentFolder", pickContentFolder], ["modsFolder", pickModsFolder]]) el(id).addEventListener("click", async () => {
    try { await pick(); gridRenderer.clear(); gridRenderer.preload(replay); draw(); } catch (error) { if (error.name !== "AbortError") el("status").textContent = error.message; }
});

const raycaster = new THREE.Raycaster();
let hoverAt = 0;
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
    el("replayHover").hidden = !owner;
    if (owner) el("replayHover").textContent = diagnosticText(owner.userData.sample, hit.object.userData.blocks?.[hit.instanceId], at, hit.object.userData.assetKind || owner.userData.assetKind);
});
renderer.domElement.addEventListener("pointerleave", () => { el("replayHover").hidden = true; });
el("downloadHistory").addEventListener("click", () => {
    const lines = journal.filter(record => record.at >= from && (!selected || !record.entity || record.entity === selected))
        .map(record => `${new Date(record.at).toISOString()} [${record.session}] ${describeEvent(record).replaceAll("\r", "\\r").replaceAll("\n", "\\n")}`);
    const url = URL.createObjectURL(new Blob([lines.join("\n")], { type: "text/plain;charset=utf-8" }));
    const link = document.createElement("a"); link.href = url; link.download = "recording-events.txt"; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
});

window.addEventListener("pagehide", () => { abort?.abort(); resize.disconnect(); renderer.setAnimationLoop(null); controls.dispose(); for (const group of grids.values()) gridRenderer.release(group); for (const entry of players.values()) releasePlayer(entry); gridRenderer.dispose(); geometry.dispose(); headGeometry.dispose(); bodyMaterial.dispose(); eventGeometry.dispose(); eventMaterial.dispose(); trail.geometry.dispose(); trailMaterial.dispose(); Object.values(materials).forEach(m => m.dispose()); renderer.dispose(); state.viewerDisposed = true; state.renderer = null; }, { once: true });
window.addEventListener("pageshow", event => { if (event.persisted) location.reload(); });
start();
