import assert from "node:assert/strict";
import { access, readdir, readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const { chromium } = await import(pathToFileURL(process.env.PLAYWRIGHT_MODULE).href);
const webroot = fileURLToPath(new URL("./bin/Release/net10.0/publish/wwwroot/", import.meta.url));
const assets = fileURLToPath(new URL("../../src/CometWorks.EntityViewer/wwwroot/", import.meta.url));
const plugin = "_quasar/plugins/cometworks.entityviewer/";
// Quasar's dialogs scale in on open; measure the settled box.
const settledBox = async dialog => {
    await dialog.evaluate(element => Promise.all(element.getAnimations().map(animation => animation.finished)));
    return dialog.boundingBox();
};

test("independently built Razor columns open the renderer in Blazor WASM", async t => {
    const manifestUrl = new URL("../../quasar-plugin.json", import.meta.url);
    const manifest = JSON.parse(await readFile(manifestUrl, "utf8"));
    assert.equal(manifest.browserAssembly, "CometWorks.EntityViewer.dll");
    const adapterProject = fileURLToPath(new URL(manifest.projectPath, manifestUrl));
    const uiDll = join(dirname(adapterProject), "bin/Release/net10.0", manifest.browserAssembly);
    const restored = JSON.parse(await readFile(new URL("../../src/CometWorks.EntityViewer/obj/project.assets.json", import.meta.url), "utf8"));
    const framework = restored.project.frameworks["net10.0"].frameworkReferences;
    assert.ok(!Object.hasOwn(framework, "Microsoft.AspNetCore.App"), "The UI must also be consumable by a browser-wasm project");
    const bootFiles = await readdir(join(webroot, "_framework"));
    assert.ok(bootFiles.every(file => !/EntityViewer|Magnetar/i.test(file)));
    // Quasar.Ui.Abstractions comes from the host; a plugin copy would split the dialog service's type identity.
    await assert.rejects(access(join(dirname(uiDll), "Quasar.Ui.Abstractions.dll")));
    const browser = await chromium.launch({ executablePath: process.env.CHROME_PATH });
    t.after(() => browser.close());

    for (const prefix of ["/", "/hosted-panel/"]) await t.test(prefix, async t => {
        const page = await browser.newPage();
        t.after(() => page.close());
        page.on("pageerror", error => t.diagnostic(error.message));
        page.on("console", message => {
            if (message.type() === "error") t.diagnostic(message.text());
        });
        const base = "http://127.0.0.1" + prefix;
        const manager = prefix === "/hosted-panel/";
        await page.addInitScript(isManager => { globalThis.fixtureManager = isManager; }, manager);
        const pluginRequests = [];
        // Only these installed assets are available to the browser. No Quasar worker is started.
        await page.route("**/*", async route => {
            const url = route.request().url();
            if (!url.startsWith(base)) return route.abort();
            const path = new URL(url).pathname.slice(prefix.length);
            if (path.startsWith(plugin)) {
                const asset = path.slice(plugin.length);
                pluginRequests.push(asset);
                if (asset === "_sdk/client.js") return route.fulfill({
                    path: fileURLToPath(new URL("./fixture-sdk.js", import.meta.url)),
                    contentType: "text/javascript",
                });
                return route.fulfill({ path: join(assets, asset) });
            }
            if (path === "fixture-ui.dll") return route.fulfill({ path: uiDll });
            if (!path || path.startsWith("_framework/") || path === "quasar-ui.js" || path === "quasar-ui.css")
                return route.fulfill({ path: join(webroot, path || "index.html") });
            return route.abort();
        });
        await page.goto(base);
        const open = page.getByRole("button", { name: "Open viewer" });
        await open.waitFor();
        assert.equal(await page.getByRole("columnheader").textContent(), "Viewer");
        assert.deepEqual(pluginRequests, ["quasar-plugin.css"]);
        await open.click();
        const viewer = page.getByRole("dialog", { name: "Freighter" });
        await viewer.waitFor();
        const viewport = page.viewportSize();
        const viewerBox = await settledBox(viewer);
        assert.deepEqual([viewerBox.x, viewerBox.y, viewerBox.width, viewerBox.height], [0, 0, viewport.width, viewport.height]);
        const frame = page.frameLocator("iframe.entity-viewer-frame");
        await frame.locator("#log").getByText("Loaded scene 9007199254740993.", { exact: false }).waitFor({ state: "attached" });
        assert.ok(pluginRequests.includes("vendor/three/build/three.module.js"));
        assert.ok(!pluginRequests.includes("replay.js"));
        if (!manager) await page.getByRole("button", { name: "Server asset streaming" }).click();
        const setup = page.getByRole("dialog", { name: "Server Asset Streaming" });
        const status = manager ? setup.getByRole("button", { name: "Accept and Enable" })
            : setup.getByText("Server asset streaming can only be configured", { exact: false });
        await status.or(setup.getByRole("alert")).first().waitFor();
        assert.equal(await status.isVisible(), true, await setup.innerText());
        // The host centers the stacked setup dialog; it must not hug the left edge or fill the height.
        const setupBox = await settledBox(setup);
        // Centered in the viewport, less the scrollbar gutter Quasar keeps while a dialog is open.
        const layoutWidth = await page.evaluate(() => document.documentElement.getBoundingClientRect().width);
        assert.ok(Math.abs(setupBox.x + setupBox.width / 2 - layoutWidth / 2) <= 1, JSON.stringify(setupBox));
        assert.equal(await setup.evaluate(element => getComputedStyle(element).position), "fixed");
        assert.ok(setupBox.width <= 760 && setupBox.height < viewport.height, JSON.stringify(setupBox));
        if (manager) {
            await status.click();
            await setup.getByText("Consent accepted", { exact: true }).waitFor();
            await setup.getByLabel("Server path to Space Engineers client").fill("/fixture/content");
            await setup.getByRole("button", { name: "Save Asset Roots" }).click();
            await setup.getByText("Asset roots saved.", { exact: true }).waitFor();
            await setup.getByLabel("Base game asset source").selectOption("ManagedSteamCmd");
            await setup.locator("pre").filter({ hasText: "stdout fixture ready" }).waitFor();
            await setup.getByLabel("Steam account (required)").fill("fixture");
            await setup.getByRole("button", { name: "Install or Update" }).click();
            await setup.getByText("Running", { exact: true }).waitFor();
            const refreshedPath = setup.getByLabel("Server path to Space Engineers client");
            await refreshedPath.or(setup.getByRole("alert")).first().waitFor();
            assert.ok(await refreshedPath.isVisible(), await setup.innerText());
            assert.equal(await refreshedPath.inputValue(), "/fixture/refreshed");
        }
        await setup.getByRole("button", { name: "Close", exact: true }).click();
        await setup.waitFor({ state: "detached" });
        await page.keyboard.press("Escape");
        await viewer.waitFor({ state: "detached" });
        // Quasar removes each element once its native close handshake finishes.
        await page.waitForFunction(() => document.querySelectorAll("dialog").length === 0);
        assert.equal(await open.evaluate(button => button === document.activeElement), true);
    });
});
