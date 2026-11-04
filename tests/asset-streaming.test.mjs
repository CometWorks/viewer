import assert from "node:assert/strict";
import test from "node:test";

let moduleId = 0;

async function streamingClient(t, sessionResponse, resolveResponse = () =>
    Response.json({ found: true, assetToken: "asset-token", logicalPath: "Models/block.mwm" })) {
    const previousWindow = globalThis.window;
    globalThis.window = { location: { search: "?agentId=server&entityId=123" } };
    t.after(() => { globalThis.window = previousWindow; });
    const previousHost = globalThis.quasarPluginHost;
    t.after(() => { globalThis.quasarPluginHost = previousHost; });
    const requests = [];
    async function result(response) {
        if (!response.ok) throw Object.assign(new Error(`${response.status}: ${(await response.json().catch(() => ({}))).detail || "Forbidden"}`), { status: response.status });
        return response.json();
    }
    globalThis.quasarPluginHost = { sdk: {
        version: 1, dispose() {},
        async request(plugin, operation, payload) {
            requests.push({ operation, payload });
            assert.equal(plugin, "cometworks.entityviewer");
            if (operation === "assets.status") return { streamingEnabled: true, fileStreamingReady: true };
            if (operation === "assets.session.create") return result(sessionResponse());
            if (operation === "assets.resolve") return result(resolveResponse());
            throw new Error(`Unexpected SDK operation: ${operation}`);
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

test("a valid session streams model bytes without local folders", async t => {
    const { client, requests } = await streamingClient(t, () => Response.json({ sessionId: "session-token" }));
    assert.deepEqual(await client.prepareRemoteAssetSession({ mods: [] }), { active: true, changed: true });
    assert.equal(client.getRemoteAssetSessionKey(), "session-token");
    const asset = await client.resolveRemoteAssetFile("Models/block.mwm", { rootId: "mod-root", sourceKind: "mod" });
    assert.equal(await (await asset.getFile()).text(), "model bytes");
    assert.equal(requests[2].operation, "assets.resolve");
    assert.deepEqual(requests[2].payload, {
        sessionId: "session-token", asset: { logicalPath: "Models/block.mwm", rootId: "mod-root", sourceKind: "mod" },
    });
});

test("a successful HTTP response without a session ID is not reported ready", async t => {
    const { client } = await streamingClient(t, () => Response.json({ expiresAtUtc: "2026-10-02T00:00:00Z" }));
    await assert.rejects(client.prepareRemoteAssetSession({}), /session ID/i);
    assert.equal(client.getRemoteAssetSessionKey(), "");
});

test("a failed session renewal clears the old session", async t => {
    let attempt = 0;
    const { client } = await streamingClient(t, () => ++attempt === 1
        ? Response.json({ sessionId: "old-session" })
        : Response.json({ detail: "Streaming disabled" }, { status: 409 }));
    await client.prepareRemoteAssetSession({});
    await assert.rejects(client.prepareRemoteAssetSession({}), /409.*Streaming disabled/);
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
        () => Response.json({ sessionId: `session-${++session}` }),
        () => allowed ? Response.json({ found: true, assetToken: "asset-token", cacheKey: "f".repeat(64), size: 11 })
            : new Response(null, { status: 403 }));
    for (let i = 0; i < 2; i++) {
        await client.prepareRemoteAssetSession({});
        const asset = await client.resolveRemoteAssetFile("Models/block.mwm");
        assert.equal(await (await asset.getFile()).text(), "model bytes");
    }
    assert.equal(requests.filter(r => r.operation === "assets.resolve").length, 2);
    assert.equal(requests.filter(r => r.operation === "assets.file.read").length, 1);
    allowed = false;
    await client.prepareRemoteAssetSession({});
    assert.equal(await client.resolveRemoteAssetFile("Models/block.mwm"), null);
    assert.equal(requests.filter(r => r.operation === "assets.file.read").length, 1);
});
