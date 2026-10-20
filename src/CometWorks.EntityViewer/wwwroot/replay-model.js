import { Quaternion } from "three";
import { movementState } from "./replay-movement.js";

export const eventKinds = new Set(["damage", "physics", "gap", "action", "movement", "death", "explosion", "block-added", "block-removed", "split", "merge", "voxel-invalidated", "voxel-coverage"]);
export function buildReplay(batches) {
    const records = batches.flatMap(batch => (batch.records || []).map(record => ({ ...record, session: batch.session, world: batch.world })))
        .sort((a, b) => a.at - b.at || (a.elapsed || 0) - (b.elapsed || 0));
    const incidents = new Map();
    for (const record of records) if (record.kind === "incident" && record.incident) {
        if (!incidents.has(record.incident.id)) incidents.set(record.incident.id, []);
        incidents.get(record.incident.id).push(record);
    }
    const tracks = new Map();
    for (const record of records) {
        if (!record.entity || !["sample", "removed"].includes(record.kind)) continue;
        const key = `${record.session}/${record.entity}`;
        if (!tracks.has(key)) tracks.set(key, []);
        tracks.get(key).push(record);
    }
    const terrainTracks = new Map();
    for (const record of records) {
        if (!["voxel-sample", "voxel-invalidated"].includes(record.kind)) continue;
        const key = `${record.session}/${record.entity}`;
        if (!terrainTracks.has(key)) terrainTracks.set(key, []);
        terrainTracks.get(key).push(record);
    }
    const modelParts = new Map(batches.flatMap(batch => Object.entries(batch.modelParts || {})));
    const voxels = new Map(batches.flatMap(batch => Object.entries(batch.voxels || {})));
    const geometry = new Map(batches.flatMap(batch => Object.entries(batch.geometry || {})));
    const movements = new Map();
    const actions = new Map(), gaps = new Map(), discontinuities = new Map();
    for (const record of records) {
        if (["split", "merge"].includes(record.kind)) for (const entity of [record.entity, record.related].filter(Boolean)) {
            const key = `${record.session}/${entity}`;
            if (!discontinuities.has(key)) discontinuities.set(key, []);
            discontinuities.get(key).push(record);
        }
        const map = record.kind === "movement" ? movements : record.kind === "gap" ? gaps : ["action", "death"].includes(record.kind) ? actions : null;
        if (!map) continue;
        const key = record.kind === "gap" ? record.session : `${record.session}/${record.entity}`;
        if (!map.has(key)) map.set(key, []);
        map.get(key).push(record);
    }
    return { incidents, modelParts, voxels, terrainTracks, geometry, records, tracks, actions, movements, gaps, discontinuities, events: records.filter(r => eventKinds.has(r.kind)), health: records.filter(r => r.kind === "health") };
}

export function lastAt(records, at) {
    let lo = 0, hi = records.length;
    while (lo < hi) { const mid = (lo + hi) >>> 1; if (records[mid].at <= at) lo = mid + 1; else hi = mid; }
    return lo ? records[lo - 1] : null;
}

export function frameAt(replay, at, entity = "", interpolate = false) {
    const health = lastAt(replay.health, at);
    const session = health && at - health.at <= 10000 ? health.session : null;
    if (!session) return { samples: [], health, gap: true };
    const samples = [];
    const gap = lastAt(replay.gaps.get(session) || [], at);
    for (const track of replay.tracks.values()) {
        let sample = lastAt(track, at);
        if (!sample || sample.session !== session || sample.kind === "removed" || !sample.position || at - sample.at > 35000) continue;
        if (gap && gap.at >= sample.at) continue;
        if (interpolate) sample = interpolateSample(replay, track, sample, at);
        if (sample.type === "player") {
            const movement = lastAt(replay.movements.get(`${session}/${sample.character}`) || [], at);
            if (movement && movement.at >= sample.at && (!gap || movement.at > gap.at)) sample = { ...sample, movement: movement.movement, movementAt: movement.at };
            const action = lastAt(replay.actions.get(`${session}/${sample.character}`) || [], at);
            if (action && action.at >= sample.at && (!gap || action.at > gap.at)) sample = { ...sample, actions: action.actions, weapon: action.weapon, actionAt: action.at };
        }
        samples.push(sample);
    }
    const contexts = incidentContextsAt(replay, at, session);
    if (contexts.length) return { samples: samples.filter(s => inContexts(s.position, contexts)), contexts, health, gap: !!gap && at - gap.at < 35000 };
    const anchor = replay.contextEntity || entity;
    const selected = anchor ? samples.find(s => s.entity === anchor) : null;
    return { samples: selected ? samples.filter(s => distance(s.position, selected.position) <= 500) : anchor ? [] : samples, health, gap: !!gap && at - gap.at < 35000 };
}

export function distance(a, b) { return Math.hypot(...a.map((v, i) => v - b[i])); }
export function speed(record) { return Math.hypot(...(record.velocity || [0, 0, 0])); }

export function trailAt(replay, sample, at) {
    const track = replay.tracks.get(`${sample.session}/${sample.entity}`) || [];
    const points = [];
    let previous;
    for (const record of track) {
        if (record.at < at - 30000 || record.at > at) continue;
        if (record.kind === "removed" || !record.position) { points.length = 0; previous = null; continue; }
        if (previous && (record.at - previous.at > 35000 || replay.events.some(e => e.kind === "gap" && e.session === sample.session && e.at >= previous.at && e.at <= record.at))) points.length = 0;
        points.push(record.position); previous = record;
    }
    return points;
}

// A layout is self-contained. Never fill missing historical pages from another time or the live server.
export function blocksAt(replay, sample) {
    if (!Array.isArray(sample.layout)) return null;
    const blocks = [];
    for (const id of sample.layout) {
        const page = replay.geometry.get(id);
        if (!page) return null;
        blocks.push(...page);
    }
    return blocks;
}

export function blockCountAt(replay, sample) {
    if (!Array.isArray(sample.layout)) return null;
    let count = 0;
    for (const id of sample.layout) {
        const page = replay.geometry.get(id);
        if (!page) return null;
        count += page.length;
    }
    return count;
}

export function describeEvent(record) {
    const subject = record.name || record.entity || "server";
    const detail = record.detail ? ` · ${record.detail}` : "";
    if (record.kind === "damage") return `${subject}: ${Number(record.value || 0).toFixed(1)} damage in ${record.count || 1} hit(s) · source ${record.source || "unknown"}${detail}`;
    if (record.kind === "physics") return `${subject}: suspected physics instability · speed ${speed(record).toFixed(2)} m/s${detail}`;
    if (record.kind === "movement") return `${subject}: ${movementState(record.movement)} (state ${record.movement})${detail}`;
    if (record.kind === "action") return `${subject}: ${actionLabels(record.actions)}${detail}`;
    if (["split", "merge"].includes(record.kind)) return `${subject}: ${record.kind} → ${record.related || "unknown"}${detail}`;
    if (record.kind === "removed") return `${subject}: no longer observed (removal or departure; cause not inferred)`;
    return `${subject}: ${record.kind}${detail}`;
}

export function replayJournal(replay) {
    const entries = replay.records.filter(r => (eventKinds.has(r.kind) || r.kind === "removed"));
    for (const track of replay.tracks.values()) {
        let previous;
        for (const record of track) {
            if (record.kind !== "sample") { previous = null; continue; }
            const details = [];
            if (!previous) details.push("First observation in loaded history");
            else {
                if (record.movement !== previous.movement) details.push(`Movement: ${movementState(record.movement)}`);
                if (!!record.high !== !!previous.high) details.push(record.high ? "Incident detail started" : "Baseline recording resumed");
                if (record.layoutId !== previous.layoutId || JSON.stringify(record.layout) !== JSON.stringify(previous.layout)) {
                    const before = blockCountAt(replay, previous), after = blockCountAt(replay, record);
                    details.push(`Block layout changed${before !== null && after !== null ? ` (${before} → ${after} blocks)` : " (incomplete layout)"}; damage, construction or edits may be involved`);
                }
            }
            if (details.length) entries.push({ ...record, kind: "observation", detail: details.join(". ") });
            previous = record;
        }
    }
    return entries.sort((a, b) => a.at - b.at);
}

// Geometry and action state remain stepped observations. Only transforms are reconstructed.
function interpolateSample(replay, track, sample, at) {
    let lo = 0, hi = track.length;
    while (lo < hi) { const mid = (lo + hi) >>> 1; if (track[mid].at <= at) lo = mid + 1; else hi = mid; }
    const next = track[lo];
    if (!next || next.kind !== "sample" || !next.position || next.session !== sample.session || next.source !== sample.source
        || next.character !== sample.character || next.at - sample.at > 1500 || at === sample.at) return sample;
    const gap = lastAt(replay.gaps.get(sample.session) || [], next.at);
    if (gap && gap.at >= sample.at) return sample;
    const topology = lastAt(replay.discontinuities.get(`${sample.session}/${sample.entity}`) || [], next.at);
    if (topology && topology.at > sample.at) return sample;
    // Conservative teleport guard: allow motion supported by measured endpoint velocities, plus quantization slack.
    const dt = (next.at - sample.at) / 1000;
    if (dt <= 0 || distance(sample.position, next.position) > 1 + Math.max(speed(sample), speed(next)) * dt * 1.5) return sample;
    const t = (at - sample.at) / (next.at - sample.at);
    const lerp = (a, b) => a && b ? a.map((v, i) => v + (b[i] - v) * t) : a;
    const rotation = sample.rotation && next.rotation ? new Quaternion().fromArray(sample.rotation).normalize()
        .slerp(new Quaternion().fromArray(next.rotation).normalize(), t).toArray() : sample.rotation;
    return { ...sample, position: lerp(sample.position, next.position), origin: lerp(sample.origin, next.origin), rotation, interpolated: true, nextAt: next.at };
}

export function eventMatches(replay, event, entity) {
    if (!entity || !event.entity || [event.entity, event.related, event.source].includes(entity)) return true;
    const sample = lastAt(replay.tracks.get(`${event.session}/${entity}`) || [], event.at);
    return sample?.kind === "sample" && event.at - sample.at <= 35000 && [sample.character, sample.source, sample.related].filter(Boolean).includes(event.entity);
}

export function actionLabels(flags) {
    if (flags == null) return "actions not recorded";
    const labels = ["aiming", "firing / tool primary", "tool secondary", "crouching", "sprinting", "seated / using", "dead"];
    return labels.filter((_, i) => flags & (1 << i)).join(", ") || "no active action";
}

export function eventLane(record) {
    if (["damage", "explosion", "death"].includes(record.kind)) return 0;
    if (["action", "movement"].includes(record.kind)) return 1;
    if (["block-added", "block-removed", "split", "merge", "removed", "observation"].includes(record.kind)) return 2;
    if (record.kind === "physics") return 3;
    if (record.kind === "health") return 4;
    return 5;
}

export function observationStep(replay, at, direction, entity = "") {
    const observations = replay.records.filter(r => r.kind === "sample" && (!entity || r.entity === entity));
    return direction < 0 ? observations.findLast(r => r.at < at)?.at ?? at : observations.find(r => r.at > at)?.at ?? at;
}

export function incidentContextsAt(replay, at, session) {
    const contexts = [];
    const contextIds = replay.activeIncidentIds || replay.contextIncidentIds;
    const firstStart = Math.min(...[...replay.incidents].filter(([id]) => contextIds?.has(id)).map(([, track]) => track[0].incident.startedAt));
    for (const [id, track] of replay.incidents) {
        if (!contextIds?.has(id)) continue;
        let record = lastAt(track, at);
        // Only the initial snapshot can describe pre-roll, never a later expanded checkpoint.
        if (!record && at < firstStart && track[0].incident.startedAt === track[0].at && at >= track[0].at - 10000) record = track[0];
        if (record?.session === session && at <= record.incident.until) contexts.push(record.incident);
    }
    return contexts;
}
function inContexts(position, contexts, margin = 0) {
    return position && contexts.some(c => distance(position, c.seed) <= c.radius + margin);
}
function incidentGroups(replay) {
    const parents = new Map();
    for (const [id, track] of replay.incidents) {
        const i = track.at(-1).incident;
        if (i.mergedInto) parents.set(id, i.mergedInto);
        for (const child of i.mergedIds || []) parents.set(child, id);
    }
    const root = id => { const seen = new Set(); while (parents.has(id) && !seen.has(id)) { seen.add(id); id = parents.get(id); } return id; };
    const groups = new Map();
    for (const id of new Set([...replay.incidents.keys(), ...parents.keys(), ...parents.values()])) {
        const key = root(id);
        if (!groups.has(key)) groups.set(key, new Set());
        groups.get(key).add(id);
    }
    return groups;
}

// Derived clips use existing baseline pre-roll. No extra high-rate server buffer or archival copy.
export function incidentClips(replay, from, to) {
    const clips = [], active = [], sessionStarts = new Map();
    for (const [id, ids] of incidentGroups(replay)) {
        const track = replay.incidents.get(id);
        if (!track) continue;
        const record = track.at(-1), i = record.incident;
        const events = replay.events.filter(e => ids.has(e.incidentId));
        const start = Math.max(i.startedAt - 10000, i.until - 3600000), end = i.until;
        if (end < from || start > to) continue;
        clips.push({ id, ids, session: record.session, startedAt: i.startedAt, actualStart: start, actualEnd: end,
            start: Math.max(from, start), end: Math.min(to, end), position: i.seed, events,
            entities: new Set(events.flatMap(e => [e.entity, e.source, e.related]).filter(Boolean)),
            incomplete: i.incomplete, partial: start < from || end > to, reason: i.reason });
    }
    for (const record of replay.records) if (!sessionStarts.has(record.session)) sessionStarts.set(record.session, record.at);
    const authoritativeSessions = new Set([...replay.incidents.values()].map(track => track[0].session));
    for (const event of replay.events) {
        if (event.at > to) break;
        if (event.incidentId || authoritativeSessions.has(event.session)) continue;
        if (!["damage", "physics", "death", "explosion", "split", "merge", "block-removed"].includes(event.kind)) continue;
        for (let i = active.length - 1; i >= 0; i--) if (active[i].end < event.at || active[i].session !== event.session) active.splice(i, 1);
        const ids = [event.entity, event.source, event.related].filter(Boolean);
        let clip = active.find(c => ids.some(id => c.entities.has(id)) || event.position && c.position && distance(event.position, c.position) <= 1000);
        if (!clip) {
            const sessionStart = sessionStarts.get(event.session) ?? event.at;
            clip = { start: Math.max(from, sessionStart, event.at - 10000), end: Math.min(to, event.at + 15000), session: event.session, events: [], entities: new Set(), position: event.position };
            clips.push(clip); active.push(clip);
            if (active.length > 32) active.shift();
        }
        clip.end = Math.min(to, event.at + 15000); clip.events.push(event);
        ids.forEach(id => clip.entities.add(id));
    }
    const sessionOrder = [...sessionStarts.entries()];
    const sessionEnds = new Map(sessionOrder.map(([session], i) => [session, i + 1 < sessionOrder.length ? sessionOrder[i + 1][1] - 1 : to]));
    return clips.filter(c => c.end >= from && c.start <= to).map(c => ({ ...c, end: Math.min(c.end, sessionEnds.get(c.session)),
        incomplete: c.incomplete || (lastAt(replay.gaps.get(c.session) || [], c.end)?.at ?? -Infinity) >= c.start,
        focus: c.events.find(e => replay.tracks.has(`${c.session}/${e.entity}`))?.entity || frameAt(replay, (c.events[0]?.at ?? c.start)).samples.find(s => s.character === c.events[0]?.entity)?.entity || "" }));
}

export function evidenceAt(replay, event) {
    const frame = frameAt(replay, event.at);
    const candidates = frame.samples.filter(s => s.session === event.session && s.type === "player" && event.source
        && [s.source, s.character, s.weapon, s.related].includes(event.source));
    const controller = !frame.gap && candidates.length === 1 ? candidates[0] : null;
    return { ...event, reportedSource: event.source || "unknown", historicalController: controller ? { entity: controller.entity, name: controller.name, observedAt: controller.at } : "unknown",
        actionState: event.actions == null ? undefined : actionLabels(event.actions),
        lineage: replay.events.filter(e => e.session === event.session && e.at <= event.at && ["split", "merge"].includes(e.kind) && [e.entity, e.related].includes(event.entity)).slice(-16),
        attribution: "Reported source and observed control only. Ownership, proximity and correlation do not establish responsibility.",
        timing: "Event timestamps are observations; layouts change at the next captured layout sample. Animation and interpolated motion are reconstructed." };
}

export function terrainAt(replay, at, session) {
    if (!session) return [];
    const gap = lastAt(replay.gaps.get(session) || [], at);
    return [...replay.terrainTracks.values()].map(track => {
        const record = lastAt(track, at);
        const invalid = lastAt(track.filter(r => r.kind === "voxel-invalidated"), at);
        return invalid && record?.value <= invalid.at ? null : record;
    }).filter(record =>
        record?.session === session && record.kind === "voxel-sample" && at - record.at <= 15000
        && (!gap || record.value > gap.at) && replay.voxels.has(record.terrainId)).filter(record => {
        const contexts = incidentContextsAt(replay, at, session);
        const page = replay.voxels.get(record.terrainId);
        return !contexts.length || inContexts(record.position, contexts, Math.hypot(...page.size) * 2 ** page.lod / 2);
    });
}

// Keep the context anchored to the entity that opened history. Selecting another focus
// must not progressively drag unrelated entities/assets into the replay.
export function pruneReplayContext(replay, entity, incidentId = "") {
    const anchorEntity = entity || replay.records.find(r => r.kind === "sample")?.entity;
    if (!anchorEntity && !incidentId) return replay;
    const anchors = new Map([...replay.tracks].filter(([, track]) => track[0].entity === anchorEntity).map(([key, track]) => [track[0].session, track]));
    const contextIds = new Set();
    for (const [id, ids] of incidentGroups(replay)) {
        const relevant = incidentId ? ids.has(incidentId) : replay.events.some(e => ids.has(e.incidentId) && (e.entity && eventMatches(replay, e, anchorEntity) || [e.source, e.related].includes(anchorEntity)))
            || [...ids].some(key => (replay.incidents.get(key) || []).some(r => {
                const a = lastAt(anchors.get(r.session) || [], r.at);
                return a?.position && r.at - a.at <= 35000 && inContexts(a.position, [r.incident]);
            }));
        if (relevant) ids.forEach(key => contextIds.add(key));
    }
    const envelopes = [...contextIds].map(id => replay.incidents.get(id)?.at(-1)).filter(Boolean);
    const withinIncident = (r, margin = 0) => envelopes.some(e => e.session === r.session && r.at >= e.incident.startedAt - 35000
        && r.at <= e.incident.until && inContexts(r.position, [e.incident], margin));
    const near = (record, anchor) => record?.kind === "sample" && anchor?.kind === "sample" && record.position && anchor.position && distance(record.position, anchor.position) <= 500;
    const retained = new Set();
    for (const [key, track] of replay.tracks) {
        const anchor = anchors.get(track[0].session);
        if (track.some(r => withinIncident(r))) retained.add(key);
        if (!anchor || incidentId) continue;
        if (track[0].entity === anchorEntity || track.some(r => { const a = lastAt(anchor, r.at); return a && r.at - a.at <= 35000 && near(r, a); })
            || anchor.some(a => { const r = lastAt(track, a.at); return r && a.at - r.at <= 35000 && near(r, a); })) retained.add(key);
    }
    const nearbyTerrain = new Set();
    for (const record of replay.records) if (record.kind === "voxel-sample") {
        const page = replay.voxels.get(record.terrainId), anchor = lastAt(anchors.get(record.session) || [], record.at);
        const radius = page ? Math.hypot(...page.size) * 2 ** page.lod / 2 : 0;
        if (withinIncident(record, radius) || !incidentId && anchor?.position && record.position && distance(record.position, anchor.position) <= 500 + radius) nearbyTerrain.add(`${record.session}/${record.entity}`);
    }
    const contextReplay = { ...replay, contextIncidentIds: contextIds };
    const records = replay.records.filter(r => {
        if (["health", "gap"].includes(r.kind)) return true;
        if (r.kind === "incident") return contextIds.has(r.incidentId);
        if (contextIds.has(r.incidentId)) return true;
        if (eventKinds.has(r.kind)) {
            const contexts = incidentContextsAt(contextReplay, r.at, r.session);
            if (contexts.length) return inContexts(r.position, contexts) || !r.position && [r.entity, r.source, r.related].some(id => {
                const sample = lastAt(replay.tracks.get(`${r.session}/${id}`) || [], r.at);
                return sample?.kind === "sample" && r.at - sample.at <= 35000 && inContexts(sample.position, contexts);
            });
            if (incidentId) return false;
        }
        if (retained.has(`${r.session}/${r.entity}`) || retained.has(`${r.session}/${r.source}`) || nearbyTerrain.has(`${r.session}/${r.entity}`)) return true;
        const a = lastAt(anchors.get(r.session) || [], r.at);
        return !incidentId && r.kind !== "sample" && a?.position && r.position && distance(r.position, a.position) <= 500;
    });
    const pageIds = new Set(records.flatMap(r => r.layout || []));
    const terrainIds = new Set(records.map(r => r.terrainId).filter(Boolean));
    const partIds = new Set([...pageIds].flatMap(id => (replay.geometry.get(id) || []).map(block => block.partsId).filter(Boolean)));
    const result = buildReplay([...new Set(records.map(r => r.session))].map(session => ({ session,
        world: records.find(r => r.session === session)?.world, records: records.filter(r => r.session === session),
        geometry: Object.fromEntries([...replay.geometry].filter(([id]) => pageIds.has(id))),
        modelParts: Object.fromEntries([...replay.modelParts].filter(([id]) => partIds.has(id))),
        voxels: Object.fromEntries([...replay.voxels].filter(([id]) => terrainIds.has(id))) })));
    result.contextEntity = anchorEntity;
    result.contextIncidentIds = contextIds;
    return result;
}
