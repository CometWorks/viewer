import assert from "node:assert/strict";
import test from "node:test";

let moduleId = 0;
const key = "a".repeat(64);
const resolved = { cacheKey: key, size: 5 };

function installCache(t, { unavailable = false, quota = false, writeBarrier = null } = {}) {
    const previous = globalThis.caches;
    const entries = new Map();
    entries.metrics = { opens: 0, matches: 0, writes: 0, activeWrites: 0, maxActiveWrites: 0 };
    globalThis.caches = unavailable ? undefined : {
        async open() {
            entries.metrics.opens++;
            return {
                async keys() { return [...entries.keys()].map(url => ({ url })); },
                async match(url) { entries.metrics.matches++; return entries.get(String(url))?.clone(); },
                async put(url, response) {
                    const metrics = entries.metrics;
                    metrics.writes++;
                    metrics.activeWrites++;
                    metrics.maxActiveWrites = Math.max(metrics.maxActiveWrites, metrics.activeWrites);
                    try {
                        if (quota) throw new DOMException("Full", "QuotaExceededError");
                        if (writeBarrier) await writeBarrier;
                        entries.set(String(url), response.clone());
                    } finally { metrics.activeWrites--; }
                },
                async delete(url) { return entries.delete(String(url)); },
            };
        },
        async delete() { entries.clear(); return true; },
    };
    t.after(() => { globalThis.caches = previous; });
    return entries;
}

async function waitFor(condition) {
    for (let i = 0; i < 100; i++) {
        if (condition()) return;
        await new Promise(resolve => setTimeout(resolve, 2));
    }
    assert.fail("Background cache operation did not finish");
}

test("a slow persistent write does not delay bytes becoming available to the parser", async t => {
    let release;
    const writeBarrier = new Promise(resolve => { release = resolve; });
    t.after(() => release());
    installCache(t, { writeBarrier });
    const c = await client();
    const result = await Promise.race([
        c.loadStreamedAsset(resolved, async () => new Blob(["asset"])).then(blob => blob.text()),
        new Promise(resolve => setTimeout(() => resolve("blocked on disk write"), 50)),
    ]);
    assert.equal(result, "asset");
});

async function client() {
    return await import(`../src/CometWorks.EntityViewer/wwwroot/asset-cache.js?test=${++moduleId}`);
}

test("model and texture bytes survive a new viewer module without another download", async t => {
    const entries = installCache(t);
    let downloads = 0;
    const fetchBlob = async () => { downloads++; return new Blob(["asset"]); };
    const first = await client();
    for (const cacheKey of [key, "b".repeat(64)])
        await first.loadStreamedAsset({ ...resolved, cacheKey }, fetchBlob);
    await waitFor(() => entries.size === 2);
    const reopened = await client();
    for (const cacheKey of [key, "b".repeat(64)])
        assert.equal(await (await reopened.loadStreamedAsset({ ...resolved, cacheKey }, fetchBlob)).text(), "asset");
    assert.equal(downloads, 2);
});

test("concurrent requests for identical bytes share one download", async t => {
    installCache(t);
    const c = await client();
    let downloads = 0;
    const blobs = await Promise.all(Array.from({ length: 24 }, () => c.loadStreamedAsset(resolved, async () => {
        downloads++;
        await new Promise(resolve => setTimeout(resolve, 5));
        return new Blob(["asset"]);
    })));
    assert.equal(downloads, 1);
    assert.ok(blobs.every(blob => blob === blobs[0]));
});

test("another user or source revision requires a new download", async t => {
    installCache(t);
    const c = await client();
    let downloads = 0;
    for (const cacheKey of [key, "b".repeat(64), "c".repeat(64)])
        await c.loadStreamedAsset({ ...resolved, cacheKey }, async () => {
            downloads++; return new Blob(["asset"]);
        });
    assert.equal(downloads, 3);
});

test("storage disabled or quota exceeded keeps streaming usable", async t => {
    for (const options of [{ unavailable: true }, { quota: true }]) {
        installCache(t, options);
        const c = await client();
        assert.equal(await (await c.loadStreamedAsset(resolved, async () => new Blob(["asset"]))).text(), "asset");
    }
});

test("failed and truncated downloads are not cached", async t => {
    const entries = installCache(t);
    const c = await client();
    await assert.rejects(c.loadStreamedAsset(resolved, async () => { throw new Error("network"); }), /network/);
    await c.loadStreamedAsset(resolved, async () => new Blob(["bad"]));
    assert.equal(entries.size, 0);
    await c.loadStreamedAsset(resolved, async () => new Blob(["asset"]));
    await waitFor(() => entries.size === 1);
    assert.equal(entries.size, 1);
});

test("clearing cache downloads again and prevents an outstanding download from repopulating it", async t => {
    const entries = installCache(t);
    const c = await client();
    let release;
    const pending = c.loadStreamedAsset(resolved, () => new Promise(resolve => { release = resolve; }));
    while (!release) await new Promise(resolve => setTimeout(resolve, 0));
    await c.clearStreamedAssetCache();
    release(new Blob(["asset"]));
    await pending;
    assert.equal(entries.size, 0);
    let downloads = 0;
    await c.loadStreamedAsset(resolved, async () => { downloads++; return new Blob(["asset"]); });
    assert.equal(downloads, 1);
});

test("cold loads open storage once and completed bytes reuse memory without storage reads", async t => {
    const entries = installCache(t);
    const c = await client();
    let downloads = 0;
    const assets = Array.from({ length: 12 }, (_, i) => ({ cacheKey: i.toString(16).padStart(64, "0"), size: 5 }));
    const fetchBlob = async () => { downloads++; return new Blob(["asset"]); };
    await Promise.all(assets.map(asset => c.loadStreamedAsset(asset, fetchBlob)));
    await waitFor(() => entries.size === assets.length);
    await Promise.all(assets.map(asset => c.loadStreamedAsset(asset, fetchBlob)));
    assert.equal(downloads, assets.length);
    assert.equal(entries.metrics.opens, 1);
    assert.equal(entries.metrics.matches, 0);
});

test("persistent writes are bounded while ready assets remain usable", async t => {
    const previousIdleCallback = globalThis.requestIdleCallback;
    let idleStarts = 0;
    globalThis.requestIdleCallback = callback => { idleStarts++; setTimeout(callback, 0); };
    t.after(() => { globalThis.requestIdleCallback = previousIdleCallback; });
    let release;
    const writeBarrier = new Promise(resolve => { release = resolve; });
    t.after(() => release());
    const entries = installCache(t, { writeBarrier });
    const c = await client();
    const assets = Array.from({ length: 12 }, (_, i) => ({ cacheKey: i.toString(16).padStart(64, "0"), size: 5 }));
    await Promise.all(assets.map(asset => c.loadStreamedAsset(asset, async () => new Blob(["asset"]))));
    await waitFor(() => entries.metrics.activeWrites === 2);
    assert.equal(entries.metrics.maxActiveWrites, 2);
    assert.equal(entries.size, 0);
    release();
    await waitFor(() => entries.size === assets.length);
    assert.equal(entries.metrics.maxActiveWrites, 2);
    assert.equal(idleStarts, 1, "The pipeline must keep draining without waiting for a new idle period per file");
});

test("memory evicts older blobs while persistent storage still avoids another download", async t => {
    const entries = installCache(t);
    const c = await client();
    const blob = new Blob([new Uint8Array(17 * 1024 * 1024)]);
    let downloads = 0;
    const fetchBlob = async () => { downloads++; return blob; };
    const a = { cacheKey: key, size: blob.size }, b = { cacheKey: "b".repeat(64), size: blob.size };
    await c.loadStreamedAsset(a, fetchBlob);
    await c.loadStreamedAsset(b, fetchBlob);
    await waitFor(() => entries.size === 2 && entries.metrics.activeWrites === 0);
    await c.loadStreamedAsset(b, fetchBlob);
    assert.equal(entries.metrics.matches, 0);
    await c.loadStreamedAsset(a, fetchBlob);
    assert.equal(entries.metrics.matches, 1);
    assert.equal(downloads, 2);
});

test("a full store stops retrying writes for every asset in the load", async t => {
    const entries = installCache(t, { quota: true });
    const c = await client();
    await c.loadStreamedAsset(resolved, async () => new Blob(["asset"]));
    await waitFor(() => entries.metrics.writes === 1 && entries.metrics.activeWrites === 0);
    for (let i = 0; i < 10; i++)
        await c.loadStreamedAsset({ ...resolved, cacheKey: i.toString(16).padStart(64, "0") }, async () => new Blob(["asset"]));
    await new Promise(resolve => setTimeout(resolve, 5));
    assert.equal(entries.metrics.writes, 1);
});
