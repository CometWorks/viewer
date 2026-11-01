import test from "node:test";
import assert from "node:assert/strict";
import { buildReplay, frameAt, incidentClips, pruneReplayContext, terrainAt } from "../src/CometWorks.EntityViewer/wwwroot/replay-model.js";

const health = (at, value = 1) => ({ kind: "health", at, value });
const sample = (at, entity, x = 0) => ({ kind: "sample", at, entity, position: [x, 0, 0] });
const node = (slot, session, records, extra = {}) => ({ node: slot, session, records, ...extra });

test("a cluster frame shows concurrent nodes and keeps their health sessions independent", () => {
    const replay = buildReplay([node("one", "a", [health(1000, .5), sample(1000, "grid-a")]),
        node("two", "b", [health(1100), sample(1100, "grid-b", 1000)])]);
    assert.deepEqual(frameAt(replay, 1100).samples.map(s => s.entity), ["grid-a", "grid-b"]);
    assert.equal(frameAt(replay, 1100).health.value, .5);
    assert.equal(frameAt(replay, 1100, "grid-b").health.session, "b");
    assert.equal(frameAt(replay, 1100).gap, false);
    assert.equal(pruneReplayContext(replay, "").tracks.size, 2, "unfocused cluster history must retain distant nodes");
});

test("a restart supersedes only its slot, and reverse seeking restores the earlier node session", () => {
    const replay = buildReplay([node("one", "a", [health(1000), sample(1000, "old-grid"), health(3000)]),
        node("two", "b", [health(1000), sample(1000, "other-grid")]), node("one", "c", [health(2000)])]);
    assert.deepEqual(frameAt(replay, 2000).samples.map(s => s.entity), ["other-grid"]);
    assert.deepEqual(frameAt(replay, 1500).samples.map(s => s.entity), ["old-grid", "other-grid"]);
    assert.deepEqual([...frameAt(replay, 2000).sessions].sort(), ["b", "c"]);
    assert.deepEqual(frameAt(replay, 3000).samples.map(s => s.entity), ["other-grid"], "a late heartbeat cannot resurrect a superseded session");
});

test("a recording gap or stale heartbeat on one node never hides verified samples on another", () => {
    const replay = buildReplay([node("one", "a", [health(1000), sample(1000, "grid-a"), { kind: "gap", at: 1500 }]),
        node("two", "b", [health(1000), sample(1000, "grid-b"), health(11000), sample(11000, "grid-b")])]);
    for (const at of [1600, 12000]) {
        const frame = frameAt(replay, at);
        assert.deepEqual(frame.samples.map(s => s.entity), ["grid-b"]);
        assert.equal(frame.gap, true);
    }
});

test("focused context includes nearby nodes and preserves node identity after pruning", () => {
    const replay = buildReplay([node("one", "a", [health(1000), sample(1000, "focus")]),
        node("two", "b", [health(1000), sample(1000, "near", 100), sample(1000, "far", 2000)])]);
    const context = pruneReplayContext(replay, "focus");
    assert.deepEqual(frameAt(context, 1000).samples.map(s => s.entity), ["focus", "near"]);
    assert.equal(context.healthByNode.size, 2);
    assert.equal(context.tracks.has("b/far"), false);
});

test("interleaved node incidents keep independent clips and do not truncate each other", () => {
    const damage = (at, entity) => ({ kind: "damage", at, entity, position: [0, 0, 0] });
    const replay = buildReplay([node("one", "a", [health(1000), sample(1000, "grid-a"), damage(1100, "grid-a"), damage(2000, "grid-a")]),
        node("two", "b", [health(1200), sample(1200, "grid-b"), damage(1300, "grid-b")])]);
    const clips = incidentClips(replay, 1000, 10000);
    assert.equal(clips.length, 2);
    assert.equal(clips.find(c => c.session === "a").events.length, 2);
    assert.ok(clips.every(c => c.end === 10000));
});

test("terrain uses every active node while gaps and invalidations stay local to their session", () => {
    const terrain = (at, entity, terrainId) => ({ kind: "voxel-sample", at, value: at, entity, terrainId, position: [0, 0, 0] });
    const replay = buildReplay([node("one", "a", [health(1000), terrain(1000, "voxel-a", "page-a"), { kind: "gap", at: 2000 }], { voxels: { "page-a": { size: [1, 1, 1], lod: 0 } } }),
        node("two", "b", [health(1000), terrain(1000, "voxel-b", "page-b")], { voxels: { "page-b": { size: [1, 1, 1], lod: 0 } } })]);
    assert.equal(terrainAt(replay, 1500, frameAt(replay, 1500).sessions).length, 2);
    assert.deepEqual(terrainAt(replay, 2000, frameAt(replay, 2000).sessions).map(r => r.entity), ["voxel-b"]);
});
