import assert from "node:assert/strict";
import test from "node:test";

let moduleId = 0;

async function streamingClient(t, sessionResponse, resolveResponse = () =>
    Response.json({ found: true, assetToken: "asset-token", logicalPath: "Models/block.mwm" })) {
    const previousWindow = globalThis.window;
    globalThis.window = { location: { search: "?agentId=server&entityId=123" } };
    t.after(() => { globalThis.window = previousWindow; });
    const requests = [];
    t.mock.method(globalThis, "fetch", async (url, options) => {
        requests.push({ url: String(url), options });
        if (String(url).endsWith("/status"))
            return Response.json({ streamingEnabled: true, fileStreamingReady: true });
        if (String(url).endsWith("/sessions")) return sessionResponse();
        if (String(url).endsWith("/resolve"))
            return resolveResponse();
        if (String(url).endsWith("/files/asset-token")) return new Response("model bytes");
        throw new Error(`Unexpected request: ${url}`);
    });
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
    assert.match(requests[2].url, /\/sessions\/session-token\/resolve$/);
    assert.deepEqual(JSON.parse(requests[2].options.body), {
        logicalPath: "Models/block.mwm", rootId: "mod-root", sourceKind: "mod",
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
    assert.equal(requests.filter(r => r.url.endsWith("/resolve")).length, 2);
    assert.equal(requests.filter(r => r.url.endsWith("/files/asset-token")).length, 1);
    allowed = false;
    await client.prepareRemoteAssetSession({});
    assert.equal(await client.resolveRemoteAssetFile("Models/block.mwm"), null);
    assert.equal(requests.filter(r => r.url.endsWith("/files/asset-token")).length, 1);
});
