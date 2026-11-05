import assert from "node:assert/strict";
import { test } from "node:test";
import { pathToFileURL } from "node:url";
import { readFile } from "node:fs/promises";
const { chromium, firefox } = await import(pathToFileURL(process.env.PLAYWRIGHT_MODULE).href);
const base = process.env.VIEWER_RTC_URL, key = process.env.VIEWER_RTC_KEY;
for (const [name, engine] of [["chromium", chromium], ["firefox", firefox]]) test(`actual Viewer over direct SDK with Caddy accounting: ${name}`, { timeout: 100000 }, async () => {
    const browser = await engine.launch({ headless: true, ...(name === "chromium" ? { executablePath: process.env.CHROME_PATH,
        args: ["--disable-features=WebRtcHideLocalIpsWithMdns"] } : {}) });
    try {
        const context = await browser.newContext({ extraHTTPHeaders: { "X-Fixture-Key": key } });
        const page = await context.newPage(), payloadHttp = [], peerIds = [];
        page.on("request", request => {
            if (/\/(operations|resources)\//.test(request.url()) || /\/api\/assets\/files\//.test(request.url())) payloadHttp.push(request.url());
        });
        page.on("response", async response => { if (/\/rtc\/attempts$/.test(response.url()) && response.ok()) peerIds.push((await response.json()).id); });
        await page.goto(base + "?agentId=server&entityId=9007199254740993");
        await page.evaluate(async () => {
            await fetch(new URL("fixture/login", location.href), { method: "POST" });
            await fetch(new URL("fixture/reset-consent", location.href), { method: "POST" });
            const url = new URL("_quasar/plugins/cometworks.entityviewer/", location.href);
            const { viewerSdk } = await import(new URL("viewer-sdk.js", url));
            const { fetchEntityScene } = await import(new URL("quasar-api.js", url));
            const assets = await import(new URL("asset-streaming.js", url));
            const sdk = globalThis.sdk = await viewerSdk();
            globalThis.viewerAssets = assets;
            const initial = await assets.fetchAssetStreamingStatus();
            if (initial.streamingEnabled) throw new Error("Consent was bypassed");
            await sdk.request("cometworks.entityviewer", "assets.consent.update", { consentAccepted: true, streamingEnabled: true });
            await assets.fetchAssetStreamingStatus();
            const scene = await fetchEntityScene();
            if (scene.large.length !== 150000 || !scene.assetSession?.sessionId || Object.keys(scene).includes("assetSession")) throw new Error("Scene grant/envelope mismatch");
            if (!(await assets.prepareRemoteAssetSession(scene)).active) throw new Error("Trusted scene session missing");
            globalThis.sceneSession = scene.assetSession.sessionId;
            const missing = await assets.resolveRemoteAssetFile("Models/secret.mwm", { rootId: "Hidden" });
            if (missing) throw new Error("Browser guessed an ungranted mod");
            const file = await assets.resolveRemoteAssetFile("Models/mod.mwm", { rootId: "123" });
            if (!file) throw new Error("Scene-granted archive did not resolve");
            globalThis.resolvedFile = file;
        });
        // No network calls are needed while reading a resolved file. Observe the real
        // reverse proxy access log across that exact interval, alongside both peers.
        await new Promise(resolve => setTimeout(resolve, 100));
        const before = (await readFile(process.env.VIEWER_RTC_ACCESS_LOG, "utf8")).trim().split("\n").length;
        const stats = await page.evaluate(async () => {
            const before = globalThis.sdk.transport;
            const file = await globalThis.resolvedFile.getFile();
            const bytes = new Uint8Array(await file.arrayBuffer());
            if (bytes.length !== 16 * 1024 * 1024) throw new Error("Archive length mismatch");
            for (let i = 0; i < bytes.length; i++) if (bytes[i] !== (i & 255)) throw new Error("Archive corrupted");
            const after = globalThis.sdk.transport;
            if (after.path !== "direct" || after.receivedBytes - before.receivedBytes < bytes.length || after.maximumQueuedBytes > 256 * 1024) throw new Error("Direct counters/bounds missing");
            return { bytes: bytes.length, peerReceived: after.receivedBytes - before.receivedBytes, maximumQueued: after.maximumQueuedBytes };
        });
        await new Promise(resolve => setTimeout(resolve, 100));
        const records = (await readFile(process.env.VIEWER_RTC_ACCESS_LOG, "utf8")).trim().split("\n");
        const during = records.slice(before).map(line => JSON.parse(line));
        const proxyBytes = during.reduce((bytes, record) => bytes + (record.size || 0), 0);
        assert.ok(during.every(record => record.request.uri.endsWith("/_quasar/sdk/v1/session")), "Only bounded session metadata may traverse the proxy while reading a resolved file");
        assert.ok(proxyBytes < 4096, "Metadata must remain small beside the 16 MiB direct body");
        // Sample native server counters after its one-second status interval.
        await new Promise(resolve => setTimeout(resolve, 1100));
        const server = await page.evaluate(async id => (await fetch(new URL(`_quasar/sdk/v1/rtc/attempts/${id}`, location.href))).json(), peerIds[0]);
        assert.equal(server.path, "direct"); assert.ok(server.sentBytes >= stats.bytes); assert.ok(server.maximumBufferedBytes <= 256 * 1024);
        await page.evaluate(() => {
            for (let i = 0; i < 2; i++) { const frame = document.createElement("iframe"); frame.src = new URL("?agentId=server&entityId=42&frame=" + i, location.href).href; document.body.append(frame); }
        });
        await page.waitForFunction(() => document.querySelectorAll("iframe").length === 2);
        for (const frame of page.frames().slice(1)) {
            await frame.waitForLoadState();
            await frame.evaluate(async () => {
                const url = new URL("_quasar/plugins/cometworks.entityviewer/", location.href);
                const { viewerSdk } = await import(new URL("viewer-sdk.js", url));
                const { fetchEntityScene } = await import(new URL("quasar-api.js", url));
                const sdk = await viewerSdk();
                if (!(await fetchEntityScene()).assetSession || sdk.transport.path !== "direct") throw new Error("Iframe did not initialize direct viewer");
                sdk.dispose();
            });
        }
        await page.evaluate(() => document.querySelectorAll("iframe").forEach(frame => frame.remove()));
        const userToken = await page.evaluate(async () => {
            const result = await globalThis.sdk.request("cometworks.entityviewer", "assets.resolve", { sessionId: globalThis.sceneSession, asset: { logicalPath: "Models/block.mwm" } });
            const read = await globalThis.sdk.openRead("cometworks.entityviewer", "assets.file.read", { assetToken: result.assetToken }, { range: "bytes=1-2" });
            if (read.status !== 206 || [...new Uint8Array(await new Response(read.body).arrayBuffer())].join() !== "2,3") throw new Error("Real Viewer range failed");
            await read.dispose();
            return result.assetToken;
        });
        const bob = await browser.newContext({ extraHTTPHeaders: { "X-Fixture-Key": key } }); const bobPage = await bob.newPage(); await bobPage.goto(base);
        await bobPage.evaluate(async token => {
            await fetch(new URL("fixture/login?user=bob", location.href), { method: "POST" });
            const { viewerSdk } = await import(new URL("_quasar/plugins/cometworks.entityviewer/viewer-sdk.js", location.href));
            const sdk = await viewerSdk();
            try {
                for (const [op, payload, status] of [["assets.roots.read", {}, 403], ["replay.query", { serverId: "server", from: 0, to: 1 }, 403],
                    ["assets.resolve", { sessionId: "forged", asset: { logicalPath: "Models/block.mwm" } }, 404], ["assets.session.create", {}, 404]]) {
                    try { await sdk.request("cometworks.entityviewer", op, payload); throw new Error("Denied operation succeeded: " + op); } catch (error) { if (error.status !== status) throw error; }
                }
                try { const stream = await sdk.openRead("cometworks.entityviewer", "assets.file.read", { assetToken: token }); await new Response(stream.body).arrayBuffer(); throw new Error("Cross-user token accepted"); }
                catch (error) { if (error.status !== 404) throw error; }
                const status = await sdk.request("cometworks.entityviewer", "assets.status");
                if (status.activeBaseGameContentDirectory) throw new Error("Viewer learned host path");
            } finally { sdk.dispose(); }
        }, userToken);
        await bob.close();
        await page.evaluate(async () => {
            // One slow feature stream is revoked by its own consent validation.
            const token = await globalThis.sdk.request("cometworks.entityviewer", "assets.resolve", { sessionId: globalThis.sceneSession, asset: { logicalPath: "Models/mod.mwm", rootId: "123" } });
            const stream = await globalThis.sdk.openRead("cometworks.entityviewer", "assets.file.read", { assetToken: token.assetToken });
            await globalThis.sdk.request("cometworks.entityviewer", "assets.consent.update", { consentAccepted: true, streamingEnabled: false });
            let failed = false; const start = performance.now();
            try { await new Response(stream.body).arrayBuffer(); } catch { failed = true; }
            await stream.dispose();
            if (!failed || performance.now() - start > 2500) throw new Error("Feature consent revocation did not stop stream");
            globalThis.sdk.dispose();
        });
        // Unsupported direct connectivity is visible before any feature dispatch.
        const unavailable = await context.newPage(); await unavailable.goto(base);
        await unavailable.evaluate(async () => {
            globalThis.RTCPeerConnection = undefined;
            const { viewerSdk } = await import(new URL("_quasar/plugins/cometworks.entityviewer/viewer-sdk.js", location.href));
            const sdk = await viewerSdk();
            try { await sdk.request("cometworks.entityviewer", "assets.status"); throw new Error("Missing native support silently fell back"); }
            catch (error) { if (error.status !== 503 || sdk.transport.path !== "unavailable") throw error; }
            finally { sdk.dispose(); }
        });
        await unavailable.close();
        assert.deepEqual(payloadHttp, []);
        console.log(JSON.stringify({ browser: name, ...stats, proxyMetadataResponseBytesDuringAsset: proxyBytes, proxyAssetBodyBytes: 0, serverSent: server.sentBytes, serverMaximumBuffered: server.maximumBufferedBytes }));
    } finally { await browser.close(); }
});
