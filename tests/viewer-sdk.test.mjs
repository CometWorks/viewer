import test from "node:test";
import assert from "node:assert/strict";
import { viewerSdk, viewerRequest } from "../src/CometWorks.EntityViewer/wwwroot/viewer-sdk.js";
import { client as preview } from "../src/CometWorks.EntityViewer/wwwroot/preview-sdk.js";

test.afterEach(() => { delete globalThis.quasarPluginHost; });
test("an incompatible host fails explicitly without a legacy network fallback", async () => {
    globalThis.quasarPluginHost = { sdk: { version: 2 } };
    await assert.rejects(viewerSdk(), /Update Quasar and the viewer plugin together/);
    delete globalThis.quasarPluginHost;
    await assert.rejects(viewerSdk(), /requires Quasar browser SDK v1/);
});
test("the preview adapter supports local/off state and rejects live operations", async () => {
    globalThis.quasarPluginHost = { sdk: preview };
    assert.equal((await viewerRequest("assets.status")).streamingEnabled, false);
    assert.deepEqual(await viewerRequest("replay.servers"), []);
    await assert.rejects(viewerRequest("assets.consent.update", {}), /does not provide/);
    await assert.rejects(preview.openRead(), /does not stream/);
});
