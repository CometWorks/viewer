import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { registerHooks } from "node:module";
import test from "node:test";
import * as THREE from "three";
import { state } from "../src/CometWorks.EntityViewer/wwwroot/state.js";

// Expose private construction functions only to this test, keeping the browser API unchanged.
const rendererUrl = new URL("../src/CometWorks.EntityViewer/wwwroot/entity-renderer.js", import.meta.url).href;
const rendererSource = await readFile(new URL(rendererUrl), "utf8");
const hooks = registerHooks({
    resolve(specifier, context, next) {
        return next(specifier === "zip.js" ? "@zip.js/zip.js" : specifier, context);
    },
    load(url, context, next) {
        if (url === rendererUrl) return { format: "module", shortCircuit: true,
            source: `${rendererSource}\nexport { buildModelLayer, createRenderContext }; export function invalidateRender() { modelRenderToken++; }` };
        return next(url, context);
    },
});
const { buildModelLayer, createRenderContext, invalidateRender } = await import(rendererUrl);
const { disposeObjectTree } = await import("../src/CometWorks.EntityViewer/wwwroot/scene.js");
hooks.deregister();

function fixture() {
    const model = path => ({ logicalPath: path, rootId: "content",
        positions: new Float32Array([0, 0, 0, 1, 0, 0, 0, 1, 0]),
        indices: new Uint16Array([0, 1, 2]),
        groups: [{ start: 0, count: 3, materialIndex: 0, materialName: "test", technique: "MESH", textures: {} }],
    });
    const base = model("Models/base.mwm");
    base.lods = [{ level: 1, distance: 10, model: model("Models/lod1.mwm") }];
    state.modelResolution.clear();
    state.modelResolution.set("model", { status: "parsed", model: base });
    state.stats = {};
    state.viewerDisposed = false;
    state.camera = new THREE.PerspectiveCamera();
    state.viewTransform = new THREE.Matrix4();
    const grid = { id: "grid", isPrimary: true, gridSize: 2.5 };
    const scene = { grid, blockInstances: Array.from({ length: 100 }, (_, i) => ({
        id: String(i), gridId: "grid", blockTypeId: "test", buildLevel: 1,
        currentModelAssetId: i < 80 ? "model" : "missing",
        cell: { x: i, y: 0, z: 0 }, translation: { x: i * 2.5, y: 0, z: 0 },
    })) };
    return { scene, definitions: new Map(), context: createRenderContext(0, new Map()) };
}

function frames(t, onFrame = () => {}) {
    let clock = 0, count = 0;
    t.mock.method(performance, "now", () => { clock += 3; return clock; });
    const previous = globalThis.requestAnimationFrame;
    globalThis.requestAnimationFrame = callback => setImmediate(() => { count++; onFrame(); callback(clock); });
    t.after(() => { globalThis.requestAnimationFrame = previous; state.viewerDisposed = false; });
    return () => count;
}

test("model construction yields while preserving instancing, proxies and camera-driven LOD", async t => {
    const countFrames = frames(t);
    const { scene, definitions, context } = fixture();
    const result = await buildModelLayer(scene, definitions, context, new Map(), 0);
    assert.ok(countFrames() > 1, "Large model builds must allow browser frames between work slices");
    assert.equal(result.stats.modelMeshes, 160);
    assert.equal(result.stats.proxyMeshes, 20);
    assert.equal(result.stats.modelBatches, 2);
    assert.equal(result.stats.proxyBatches, 1);
    const lods = [], meshes = [];
    result.layer.traverse(object => { if (object.isLOD) lods.push(object); if (object.isInstancedMesh) meshes.push(object); });
    assert.equal(lods.length, 1);
    assert.deepEqual(lods[0].levels.map(level => level.distance), [0, 40]);
    assert.deepEqual(meshes.map(mesh => mesh.count).sort((a, b) => a - b), [20, 80, 80]);
    result.layer.updateMatrixWorld(true);
    state.camera.position.set(0, 0, 1);
    state.camera.updateMatrixWorld(true);
    lods[0].update(state.camera);
    assert.equal(lods[0].getCurrentLevel(), 0);
    state.camera.position.set(0, 0, 1000);
    state.camera.updateMatrixWorld(true);
    lods[0].update(state.camera);
    assert.equal(lods[0].getCurrentLevel(), 1, "Framing the camera must not need a second model build");
    disposeObjectTree(result.layer);
});

test("closing the viewer during construction abandons the layer", async t => {
    frames(t, () => { state.viewerDisposed = true; });
    const { scene, definitions, context } = fixture();
    const result = await buildModelLayer(scene, definitions, context, new Map(), 0);
    assert.equal(result, null);
    assert.equal(state.stats["Model build max slice ms"], undefined);
});


test("a replacement scene invalidates an unfinished model build", async t => {
    frames(t, invalidateRender);
    const { scene, definitions, context } = fixture();
    assert.equal(await buildModelLayer(scene, definitions, context, new Map(), 0), null);
});
