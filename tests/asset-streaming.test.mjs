import assert from "node:assert/strict";
import test from "node:test";

let moduleId = 0;

async function streamingClient(t, sessionResponse) {
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
            return Response.json({ found: true, assetToken: "asset-token", logicalPath: "Models/block.mwm" });
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
