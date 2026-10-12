import { Quaternion } from "three";

export const eventKinds = new Set(["damage", "physics", "gap", "action", "death", "explosion", "block-added", "block-removed", "split", "merge"]);
export function buildReplay(batches) {
    const records = batches.flatMap(batch => (batch.records || []).map(record => ({ ...record, session: batch.session, world: batch.world })))
        .sort((a, b) => a.at - b.at || (a.elapsed || 0) - (b.elapsed || 0));
    const tracks = new Map();
    for (const record of records) {
        if (!record.entity || !["sample", "removed"].includes(record.kind)) continue;
        const key = `${record.session}/${record.entity}`;
        if (!tracks.has(key)) tracks.set(key, []);
        tracks.get(key).push(record);
    }
    const geometry = new Map(batches.flatMap(batch => Object.entries(batch.geometry || {})));
    const actions = new Map(), gaps = new Map(), discontinuities = new Map();
    for (const record of records) {
        if (["split", "merge"].includes(record.kind)) for (const entity of [record.entity, record.related].filter(Boolean)) {
            const key = `${record.session}/${entity}`;
            if (!discontinuities.has(key)) discontinuities.set(key, []);
            discontinuities.get(key).push(record);
        }
        const map = record.kind === "gap" ? gaps : ["action", "death"].includes(record.kind) ? actions : null;
        if (!map) continue;
        const key = record.kind === "gap" ? record.session : `${record.session}/${record.entity}`;
        if (!map.has(key)) map.set(key, []);
        map.get(key).push(record);
    }
    return { geometry, records, tracks, actions, gaps, discontinuities, events: records.filter(r => eventKinds.has(r.kind)), health: records.filter(r => r.kind === "health") };
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
            const action = lastAt(replay.actions.get(`${session}/${sample.character}`) || [], at);
            if (action && action.at >= sample.at && (!gap || action.at > gap.at)) sample = { ...sample, actions: action.actions, weapon: action.weapon, actionAt: action.at };
        }
        samples.push(sample);
    }
    const selected = entity ? samples.find(s => s.entity === entity) : null;
    return { samples: selected ? samples.filter(s => s.entity === entity || distance(s.position, selected.position) <= 1000) : entity ? [] : samples, health, gap: !!gap && at - gap.at < 35000 };
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
    return labels.filter((_, i) => flags & (1 << i)).join(", ") || "idle / moving";
}

export function eventLane(record) {
    if (["damage", "explosion", "death"].includes(record.kind)) return 0;
    if (record.kind === "action") return 1;
    if (["block-added", "block-removed", "split", "merge", "removed", "observation"].includes(record.kind)) return 2;
    if (record.kind === "physics") return 3;
    if (record.kind === "health") return 4;
    return 5;
}

export function observationStep(replay, at, direction, entity = "") {
    const observations = replay.records.filter(r => r.kind === "sample" && (!entity || r.entity === entity));
    return direction < 0 ? observations.findLast(r => r.at < at)?.at ?? at : observations.find(r => r.at > at)?.at ?? at;
}

// Derived clips use existing baseline pre-roll. No extra high-rate server buffer or archival copy.
export function incidentClips(replay, from, to) {
    const clips = [], active = [], sessionStarts = new Map();
    for (const record of replay.records) if (!sessionStarts.has(record.session)) sessionStarts.set(record.session, record.at);
    for (const event of replay.events) {
        if (event.at > to) break;
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
        incomplete: (lastAt(replay.gaps.get(c.session) || [], c.end)?.at ?? -Infinity) >= c.start,
        focus: c.events.find(e => replay.tracks.has(`${c.session}/${e.entity}`))?.entity || frameAt(replay, c.events[0].at).samples.find(s => s.character === c.events[0].entity)?.entity || "" }));
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
