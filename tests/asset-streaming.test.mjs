import assert from "node:assert/strict";
import test from "node:test";

let moduleId = 0;

async function streamingClient(t, resolveResponse = () =>
    Response.json({ found: true, assetToken: "asset-token", logicalPath: "Models/block.mwm" })) {
    const previousWindow = globalThis.window;
    globalThis.window = { location: { search: "?agentId=server&entityId=123" } };
    t.after(() => { globalThis.window = previousWindow; });
    const previousHost = globalThis.quasarPluginHost;
    t.after(() => { globalThis.quasarPluginHost = previousHost; });
    const requests = [];
    globalThis.quasarPluginHost = { sdk: {
        version: 1, dispose() {},
        async request(plugin, operation, payload) {
            requests.push({ operation, payload });
            assert.equal(plugin, "cometworks.entityviewer");
            if (operation === "assets.status") return { streamingEnabled: true, fileStreamingReady: true };
            if (operation === "assets.resolve") {
                const response = await resolveResponse();
                if (!response.ok) throw Object.assign(new Error("Forbidden"), { status: response.status });
                return response.json();
            }
            throw new Error(`Unexpected operation: ${operation}`);
        },
        async openRead(plugin, operation, payload) {
            requests.push({ operation, payload });
            return { async blob() { return new Blob(["model bytes"]); }, async dispose() {} };
        },
    } };
    const client = await import(`../src/CometWorks.EntityViewer/wwwroot/asset-streaming.js?test=${++moduleId}`);
    await client.fetchAssetStreamingStatus();
    return { client, requests };
}

test("a live scene activates only the opaque session issued by the server", async t => {
    const previousWindow = globalThis.window;
    globalThis.window = { location: { search: "?agentId=server&entityId=123" } };
    t.after(() => { globalThis.window = previousWindow; });
    const previousHost = globalThis.quasarPluginHost;
    t.after(() => { globalThis.quasarPluginHost = previousHost; });
    globalThis.quasarPluginHost = { sdk: { version: 1, dispose() {}, openRead() {},
        async request(plugin, operation) {
            assert.equal(operation, "scene.read");
            return { data: { mods: [{ rootId: "123" }] }, assetSession: {
                sessionId: "server-session", expiresAtUtc: "2026-10-31T12:00:00Z",
            } };
        },
    } };
    const api = await import(`../src/CometWorks.EntityViewer/wwwroot/quasar-api.js?test=${++moduleId}`);
    const scene = await api.fetchEntityScene();
    assert.deepEqual(scene.assetSession, {
        sessionId: "server-session",
        expiresAtUtc: "2026-10-31T12:00:00Z",
    });
    assert.deepEqual(Object.keys(scene), ["mods"]);
});

test("a valid session streams model bytes without local folders", async t => {
    const { client, requests } = await streamingClient(t);
    assert.deepEqual(await client.prepareRemoteAssetSession({ assetSession: { sessionId: "session-token" } }), { active: true, changed: true });
    assert.equal(client.getRemoteAssetSessionKey(), "session-token");
    const asset = await client.resolveRemoteAssetFile("Models/block.mwm", { rootId: "mod-root", sourceKind: "mod" });
    assert.equal(await (await asset.getFile()).text(), "model bytes");
    assert.equal(requests[1].operation, "assets.resolve");
    assert.deepEqual(requests[1].payload, {
        sessionId: "session-token", asset: { logicalPath: "Models/block.mwm", rootId: "mod-root", sourceKind: "mod" },
    });
    assert.equal(requests.some(request => request.operation === "assets.session.create"), false);
});

test("a server response without an issued session is not reported ready", async t => {
    const { client } = await streamingClient(t);
    assert.deepEqual(await client.prepareRemoteAssetSession({}), { active: false, changed: false });
    assert.equal(client.getRemoteAssetSessionKey(), "");
});

test("a scene without a renewed session clears the old session", async t => {
    const { client } = await streamingClient(t);
    await client.prepareRemoteAssetSession({ assetSession: { sessionId: "old-session" } });
    assert.deepEqual(await client.prepareRemoteAssetSession({}), { active: false, changed: true });
    assert.equal(client.getRemoteAssetSessionKey(), "");
});

test("cached bytes still require a successful resolve in each new session", async t => {
    const previousCache = globalThis.caches;
    const cached = new Map();
    globalThis.caches = { async open() { return {
        async keys() { return [...cached.keys()].map(url => ({ url })); },
        async match(url) { return cached.get(String(url))?.clone(); },
        async put(url, response) { cached.set(String(url), response.clone()); },
    }; } };
    t.after(() => { globalThis.caches = previousCache; });
    let allowed = true;
    let session = 0;
    const { client, requests } = await streamingClient(t,
        () => allowed ? Response.json({ found: true, assetToken: "asset-token", cacheKey: "f".repeat(64), size: 11 })
            : new Response(null, { status: 403 }));
    for (let i = 0; i < 2; i++) {
        await client.prepareRemoteAssetSession({ assetSession: { sessionId: `session-${++session}` } });
        const asset = await client.resolveRemoteAssetFile("Models/block.mwm");
        assert.equal(await (await asset.getFile()).text(), "model bytes");
    }
    assert.equal(requests.filter(r => r.operation === "assets.resolve").length, 2);
    assert.equal(requests.filter(r => r.operation === "assets.file.read").length, 1);
    allowed = false;
    await client.prepareRemoteAssetSession({ assetSession: { sessionId: `session-${++session}` } });
    assert.equal(await client.resolveRemoteAssetFile("Models/block.mwm"), null);
    assert.equal(requests.filter(r => r.operation === "assets.file.read").length, 1);
});

test("parallel model, LOD and texture work shares two slots through stream disposal", async t => {
    let active = 0, maximum = 0, reads = 0, disposed = 0;
    const enter = () => { maximum = Math.max(maximum, ++active); };
    const yieldIo = () => new Promise(resolve => setImmediate(resolve));
    const { client, requests } = await streamingClient(t, async () => {
        enter();
        await yieldIo();
        active--;
        return Response.json({ found: true, assetToken: "asset-token" });
    });
    t.mock.method(globalThis.quasarPluginHost.sdk, "openRead", async () => {
        enter(); reads++;
        await yieldIo();
        return {
            async blob() { await yieldIo(); return new Blob(["model bytes"]); },
            async dispose() { await yieldIo(); disposed++; active--; },
        };
    });
    await client.prepareRemoteAssetSession({ assetSession: { sessionId: "session-token" } });
    await Promise.all(Array.from({ length: 48 }, async (_, index) => {
        for (const path of [`Models/${index}.mwm`, `Models/${index}_LOD0.mwm`, `Textures/${index}.dds`]) {
            const asset = await client.resolveRemoteAssetFile(path);
            assert.equal(await (await asset.getFile()).text(), "model bytes");
        }
    }));
    assert.equal(requests.filter(request => request.operation === "assets.resolve").length, 144);
    assert.equal(reads, 144);
    assert.equal(disposed, reads);
    assert.equal(maximum, 2);
    assert.equal(active, 0);
});

test("explicit resolve and read-opening throttling retries without reporting missing files", async t => {
    const delays = [];
    t.mock.method(globalThis, "setTimeout", (callback, delay) => { delays.push(delay); queueMicrotask(callback); });
    let resolves = 0, opens = 0, disposed = 0;
    const { client } = await streamingClient(t, () => ++resolves <= 2
        ? new Response(null, { status: 429 })
        : Response.json({ found: true, assetToken: "asset-token" }));
    t.mock.method(globalThis.quasarPluginHost.sdk, "openRead", async () => {
        if (++opens === 1) throw Object.assign(new Error("Stream capacity"), { status: 429 });
        return { async blob() { return new Blob(["model bytes"]); }, async dispose() { disposed++; } };
    });
    await client.prepareRemoteAssetSession({ assetSession: { sessionId: "session-token" } });
    const asset = await client.resolveRemoteAssetFile("Models/block.mwm");
    assert.equal(await (await asset.getFile()).text(), "model bytes");
    assert.equal(resolves, 3);
    assert.equal(opens, 2);
    assert.equal(disposed, 1);
    assert.deepEqual(delays, [250, 500, 250]);
});

test("exhausted throttling and transport failures remain errors and release capacity", async t => {
    const delays = [];
    t.mock.method(globalThis, "setTimeout", (callback, delay) => { delays.push(delay); queueMicrotask(callback); });
    let status = 429;
    const { client, requests } = await streamingClient(t, () => status
        ? new Response(null, { status }) : Response.json({ found: true, assetToken: "asset-token" }));
    await client.prepareRemoteAssetSession({ assetSession: { sessionId: "session-token" } });
    await assert.rejects(client.resolveRemoteAssetFile("Models/block.mwm"), { status: 429 });
    assert.deepEqual(delays, [250, 500, 1000, 2000, 4000, 8000, 16000, 30000]);
    assert.equal(requests.filter(request => request.operation === "assets.resolve").length, 9);
    status = 503;
    await assert.rejects(client.resolveRemoteAssetFile("Models/block.mwm"), { status: 503 });
    assert.equal(requests.filter(request => request.operation === "assets.resolve").length, 10);
    status = 0;
    assert.ok(await client.resolveRemoteAssetFile("Models/block.mwm"));
});

test("an interrupted download is disposed and never replayed, including body errors with status 429", async t => {
    const { client } = await streamingClient(t);
    let opens = 0, disposed = 0;
    t.mock.method(globalThis.quasarPluginHost.sdk, "openRead", async () => {
        opens++;
        return {
            async blob() { throw Object.assign(new Error("Body interrupted"), { status: 429 }); },
            async dispose() { disposed++; },
        };
    });
    await client.prepareRemoteAssetSession({ assetSession: { sessionId: "session-token" } });
    const asset = await client.resolveRemoteAssetFile("Models/block.mwm");
    await assert.rejects(asset.getFile(), /Body interrupted/);
    assert.equal(opens, 1);
    assert.equal(disposed, 1);
    assert.ok(await client.resolveRemoteAssetFile("Models/other.mwm"));
});
