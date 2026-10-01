import assert from "node:assert/strict";
import test from "node:test";

let moduleId = 0;
const key = "a".repeat(64);
const resolved = { cacheKey: key, size: 5 };

function installCache(t, { unavailable = false, quota = false } = {}) {
    const previous = globalThis.caches;
    const entries = new Map();
    globalThis.caches = unavailable ? undefined : {
        async open() {
            return {
                async match(url) { return entries.get(String(url))?.clone(); },
                async put(url, response) {
                    if (quota) throw new DOMException("Full", "QuotaExceededError");
                    entries.set(String(url), response.clone());
                },
                async delete(url) { return entries.delete(String(url)); },
            };
        },
        async delete() { entries.clear(); return true; },
    };
    t.after(() => { globalThis.caches = previous; });
    return entries;
}

async function client() {
    return await import(`../src/CometWorks.EntityViewer/wwwroot/asset-cache.js?test=${++moduleId}`);
}

test("model and texture bytes survive a new viewer module without another download", async t => {
    installCache(t);
    let downloads = 0;
    const fetchBlob = async () => { downloads++; return new Blob(["asset"]); };
    const first = await client();
    for (const cacheKey of [key, "b".repeat(64)])
        await first.loadStreamedAsset({ ...resolved, cacheKey }, fetchBlob);
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
