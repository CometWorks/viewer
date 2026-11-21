import assert from "node:assert/strict";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { pathToFileURL } from "node:url";

const { firefox } = await import(pathToFileURL(process.env.PLAYWRIGHT_MODULE).href);

test("Firefox reuses explicitly picked local folders in the next grid dialog", async t => {
    const root = await mkdtemp(join(tmpdir(), "viewer-content-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const content = join(root, "Content");
    for (const path of ["Data", "Models", "Textures"]) await mkdir(join(content, path), { recursive: true });
    await writeFile(join(content, "Data", "Fixture.sbc"), "<Definitions/>");
    await writeFile(join(content, "Models", "first.mwm"), "first synthetic model");
    await writeFile(join(content, "Models", "second.mwm"), "second synthetic model");
    await writeFile(join(content, "Textures", "Fixture.dds"), "synthetic texture");
    const mods = join(root, "Mods");
    await mkdir(join(mods, "123", "Models"), { recursive: true });
    await writeFile(join(mods, "123", "Models", "mod.mwm"), "synthetic mod model");

    const browser = await firefox.launch({ executablePath: process.env.FIREFOX_PATH });
    t.after(() => browser.close());
    const page = await browser.newPage();
    await page.route("**/_sdk/client.js", route => route.fulfill({ contentType: "text/javascript", body: `
        export const client = {
            version: 1,
            async request(plugin, operation, { entityId }) {
                if (operation === "assets.status") return { streamingEnabled: false, fileStreamingReady: false };
                if (operation === "scene.read") return { data: {
                    grid: { id: entityId, gridSize: 2.5 }, mods: [{ rootId: "123", publishedFileId: 123 }],
                } };
                throw new Error(operation);
            },
            openRead() { throw new Error("Streaming is disabled"); },
            dispose() {},
        };
    ` }));
    await page.goto(process.env.VIEWER_PREVIEW_URL);

    async function openGrid(name, id) {
        await page.getByRole("row", { name: new RegExp(name) }).getByRole("button", { name: "Open viewer" }).click();
        const frame = page.frameLocator("iframe.entity-viewer-frame");
        await frame.locator("#log").getByText(`Loaded scene ${id}.`, { exact: false }).waitFor({ state: "attached" });
        return frame;
    }

    const readModel = (frame, name, rootId = "") => frame.locator("body").evaluate(async (_, model) => {
        const { resolveAssetFile } = await import("./content-folder.js");
        const resolved = await resolveAssetFile(`Models/${model.name}.mwm`, { rootId: model.rootId });
        return resolved ? (await resolved.getFile()).text() : null;
    }, { name, rootId });

    async function pickFolder(frame, name, path) {
        const choosing = page.waitForEvent("filechooser");
        await frame.getByRole("button", { name: `Select Space Engineers ${name} Folder` }).click();
        await (await choosing).setFiles(path);
        await frame.getByText(`Using ${name} folder: ${name}`, { exact: true }).waitFor();
    }

    const first = await openGrid("Mining Platform", "281474976710681");
    assert.equal(await readModel(first, "first"), null);
    await pickFolder(first, "Content", content);
    assert.equal(await readModel(first, "first"), "first synthetic model");
    await pickFolder(first, "Mods", mods);
    assert.equal(await readModel(first, "mod", "123"), "synthetic mod model");
    await page.getByRole("button", { name: "Close viewer", exact: true }).click();
    await page.locator("iframe.entity-viewer-frame").waitFor({ state: "detached" });

    const second = await openGrid("Survey Ship", "281474976710684");
    assert.equal(await readModel(second, "second"), "second synthetic model");
    assert.equal(await readModel(second, "mod", "123"), "synthetic mod model");
    assert.equal(await second.locator("#contentStatus").textContent(), "Using saved Content folder: Content");
    assert.equal(await readModel(page.frameLocator("iframe.preview-frame"), "second"), null);

    await page.reload();
    const afterReload = await openGrid("Survey Ship", "281474976710684");
    assert.equal(await readModel(afterReload, "second"), null);
    assert.equal(await readModel(afterReload, "mod", "123"), null);
});
