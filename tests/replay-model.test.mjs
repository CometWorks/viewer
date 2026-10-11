import test from "node:test";
import assert from "node:assert/strict";
import { buildReplay, frameAt, lastAt, blocksAt, blockCountAt } from "../src/CometWorks.EntityViewer/wwwroot/replay-model.js";
const sample = (at, entity = "9223372036854775807") => ({ at, kind: "sample", entity, position: [1, 2, 3] });
const health = at => ({ at, kind: "health", value: 1 });
test("history holds observations, preserves IDs and does not invent intermediate positions", () => {
    const replay = buildReplay([{ session: "a", records: [sample(1000), health(1000), { ...sample(2000), position: [50, 2, 3] }] }]);
    assert.deepEqual(frameAt(replay, 1500).samples[0].position, [1, 2, 3]);
    assert.equal(frameAt(replay, 1500).samples[0].entity, "9223372036854775807");
    assert.deepEqual(frameAt(replay, 2000).samples[0].position, [50, 2, 3]);
});
test("removals, stale heartbeats and explicit gaps hide unverified positions", () => {
    const replay = buildReplay([{ session: "a", records: [sample(1000), health(1000), { at: 2000, kind: "removed", entity: "9223372036854775807" }] }]);
    assert.equal(frameAt(replay, 2000).samples.length, 0);
    assert.equal(frameAt(replay, 12000).gap, true);
    const gap = buildReplay([{ session: "a", records: [sample(1000), health(1000), { at: 2000, kind: "gap" }, sample(3000)] }]);
    assert.equal(frameAt(gap, 2500).samples.length, 0);
    assert.equal(frameAt(gap, 3000).samples.length, 1);
});
test("new server session never reuses the previous session entity state", () => {
    const replay = buildReplay([{ session: "a", records: [sample(1000), health(1000)] }, { session: "b", records: [health(2000)] }]);
    assert.equal(frameAt(replay, 2000).samples.length, 0);
});
test("query pre-roll restores unchanged entities and expires old observations", () => {
    const replay = buildReplay([{ session: "a", records: [sample(1000), health(31000), health(37000)] }]);
    assert.equal(frameAt(replay, 31000).samples.length, 1);
    assert.equal(frameAt(replay, 37000).samples.length, 0);
    assert.equal(lastAt([], 0), null);
});
test("a focused trail never joins observations across a recording gap", async () => {
    const { trailAt } = await import("../src/CometWorks.EntityViewer/wwwroot/replay-model.js");
    const replay = buildReplay([{ session: "a", records: [sample(1000), sample(2000), { at: 2500, kind: "gap" }, sample(3000), health(3000)] }]);
    const frame = frameAt(replay, 3000);
    assert.equal(trailAt(replay, frame.samples[0], 3000).length, 1);
});
test("a missing focused entity does not display unrelated entities as its context", () => {
    const replay = buildReplay([{ session: "a", records: [sample(1000, "other"), health(1000)] }]);
    assert.equal(frameAt(replay, 1000, "missing").samples.length, 0);
});

test("destruction and rebuilding follow recorded layouts through forward and reverse seeks", () => {
    const left = { cell: [0, 0, 0], integrity: 1 }, right = { cell: [1, 0, 0], integrity: .4 };
    const replay = buildReplay([{ session: "a", geometry: { left: [left], right: [right] }, records: [
        health(1000), { ...sample(1000), layout: ["left", "right"] },
        { ...sample(2000), layout: ["right"] }, { ...sample(3000), layout: ["left", "right"] }
    ] }]);
    for (const [at, count] of [[1000, 2], [2500, 1], [3000, 2], [1500, 2]]) {
        const observation = frameAt(replay, at).samples[0];
        assert.equal(blocksAt(replay, observation).length, count);
        assert.equal(blockCountAt(replay, observation), count);
    }
    assert.equal(blocksAt(replay, frameAt(replay, 2500).samples[0])[0].integrity, .4);
});
test("missing pages never borrow geometry from a different observation", () => {
    const replay = buildReplay([{ session: "a", geometry: { known: [{ cell: [0, 0, 0] }] }, records: [
        health(1000), { ...sample(1000), layout: ["known"] }, { ...sample(2000), layout: ["missing"] }, sample(3000)
    ] }]);
    assert.equal(blocksAt(replay, frameAt(replay, 2000).samples[0]), null);
    assert.equal(blocksAt(replay, frameAt(replay, 3000).samples[0]), null);
    assert.equal(blockCountAt(replay, frameAt(replay, 2000).samples[0]), null);
});
test("a complete empty grid is distinct from unknown geometry and pages can arrive in another batch", () => {
    const replay = buildReplay([{ session: "a", records: [health(1000), { ...sample(1000), layout: [] }, { ...sample(2000), layout: ["p"] }] },
        { session: "a", records: [], geometry: { p: [{ cell: [1, 2, 3] }] } }]);
    assert.deepEqual(blocksAt(replay, frameAt(replay, 1000).samples[0]), []);
    assert.equal(blocksAt(replay, frameAt(replay, 2000).samples[0]).length, 1);
});
