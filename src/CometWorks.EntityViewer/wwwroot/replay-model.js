// Recorded observations only. No interpolation across gaps, removals, or recording sessions.
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
    return { geometry, records, tracks, events: records.filter(r => ["damage", "physics", "gap"].includes(r.kind)), health: records.filter(r => r.kind === "health") };
}

export function lastAt(records, at) {
    let lo = 0, hi = records.length;
    while (lo < hi) { const mid = (lo + hi) >>> 1; if (records[mid].at <= at) lo = mid + 1; else hi = mid; }
    return lo ? records[lo - 1] : null;
}

export function frameAt(replay, at, entity = "") {
    const health = lastAt(replay.health, at);
    const session = health && at - health.at <= 10000 ? health.session : null;
    if (!session) return { samples: [], health, gap: true };
    const samples = [];
    const gap = lastAt(replay.events.filter(e => e.kind === "gap" && e.session === session), at);
    for (const track of replay.tracks.values()) {
        const sample = lastAt(track, at);
        if (!sample || sample.session !== session || sample.kind === "removed" || !sample.position || at - sample.at > 35000) continue;
        if (gap && gap.at >= sample.at) continue;
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
