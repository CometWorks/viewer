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
            source: `${rendererSource}\nexport { buildModelLayer, createRenderContext, configureRelativeView, gridRelativeMatrix, primaryGridRelativeBounds, contextRelativeBounds, contextClipRelativeBounds, floorGridAlignment }; export function invalidateRender() { modelRenderToken++; }` };
        return next(url, context);
    },
});
const { buildModelLayer, createRenderContext, invalidateRender, configureRelativeView, gridRelativeMatrix,
    primaryGridRelativeBounds, contextRelativeBounds, contextClipRelativeBounds, floorGridAlignment } = await import(rendererUrl);
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

function matrixDto(matrix) {
    return Object.fromEntries(matrix.elements.map((value, index) => [`m${Math.floor(index / 4) + 1}${index % 4 + 1}`, value]));
}

function assertVector(actual, expected) {
    assert.ok(actual.distanceTo(expected) < 1e-8, `${actual.toArray()} must match ${expected.toArray()}`);
}

test("gravity frame aligns the entire scene and preserves grid and terrain positions", () => {
    const center = new THREE.Vector3(1000000, -2000000, 3000000);
    const gridWorld = new THREE.Matrix4().makeRotationZ(Math.PI / 2).setPosition(center);
    const up = new THREE.Vector3(1, 2, 3).normalize();
    const right = new THREE.Vector3(0, 0, -1).cross(up).normalize();
    const backward = right.clone().cross(up).normalize();
    const frame = new THREE.Matrix4().makeBasis(right, up, backward).setPosition(center);
    const grid = { id: "primary", isPrimary: true, gridSize: 2.5, worldMatrix: matrixDto(gridWorld) };
    const nearbyWorld = gridWorld.clone().setPosition(center.clone().add(new THREE.Vector3(8, -3, 2)));
    const nearby = { id: "nearby", isContext: true, worldMatrix: matrixDto(nearbyWorld) };
    const scene = { grid, grids: [grid, nearby], gravityAlignedViewFrame: matrixDto(frame),
        blockInstances: [{ gridId: grid.id, min: { x: -2, y: -1, z: -1 }, max: { x: 2, y: 1, z: 1 } }] };
    const originalGridMatrix = structuredClone(grid.worldMatrix);

    configureRelativeView(scene);
    assertVector(center.clone().applyMatrix4(state.viewTransform), new THREE.Vector3());
    assertVector(up.clone().negate().transformDirection(state.viewRotation), new THREE.Vector3(0, -1, 0));
    for (const [candidate, world] of [[grid, gridWorld], [nearby, nearbyWorld]]) {
        const blockPoint = new THREE.Vector3(2, 3, -1);
        const terrainPoint = blockPoint.clone().applyMatrix4(world);
        assertVector(blockPoint.applyMatrix4(gridRelativeMatrix(candidate)), terrainPoint.applyMatrix4(state.viewTransform));
    }
    const localBounds = new THREE.Box3(new THREE.Vector3(-6.25, -3.75, -3.75), new THREE.Vector3(6.25, 3.75, 3.75));
    const expectedBounds = localBounds.applyMatrix4(gridRelativeMatrix(grid));
    const actualBounds = primaryGridRelativeBounds(scene);
    assertVector(actualBounds.min, expectedBounds.min);
    assertVector(actualBounds.max, expectedBounds.max);
    assert.deepEqual(grid.worldMatrix, originalGridMatrix, "Viewer alignment must not change grid transforms");
});

test("gravity-aligned context uses the captured view bounds and disables block parity offsets", () => {
    const scene = { grid: { id: "primary", gridSize: 2.5 }, gravityAlignedViewFrame: matrixDto(new THREE.Matrix4()),
        blockInstances: [{ cell: { x: 0, y: 0, z: 0 } }],
        context: { enabled: true, relativeAabb: { min: { x: -8, y: -4, z: -6 }, max: { x: 8, y: 4, z: 6 } } } };
    configureRelativeView(scene);
    assert.deepEqual(floorGridAlignment(scene), { offsetX: 0, offsetZ: 0, cellCountX: 0, cellCountZ: 0 });
    const bounds = contextRelativeBounds(scene);
    assertVector(bounds.min, new THREE.Vector3(-8, -4, -6));
    assertVector(bounds.max, new THREE.Vector3(8, 4, 6));
    const clip = contextClipRelativeBounds(scene);
    assertVector(clip.min, new THREE.Vector3(-13, -4, -11));
    assertVector(clip.max, new THREE.Vector3(13, 4, 11));

    delete scene.gravityAlignedViewFrame;
    assert.equal(floorGridAlignment(scene).offsetX, 1.25, "Space/older snapshots must retain block parity alignment");
});

test("snapshots without a gravity frame retain grid alignment and standalone voxel centering", () => {
    const world = new THREE.Matrix4().makeRotationZ(0.8).setPosition(30, 40, 50);
    const scene = { grid: { id: "primary", worldMatrix: matrixDto(world),
        bounds: { min: { x: 29, y: 39, z: 49 }, max: { x: 31, y: 41, z: 51 } } },
        blockInstances: [{ cell: { x: 0, y: 0, z: 0 } }] };
    configureRelativeView(scene);
    assertVector(new THREE.Vector3(0, 1, 0).transformDirection(world).transformDirection(state.viewRotation), new THREE.Vector3(0, 1, 0));
    assertVector(new THREE.Vector3(30, 40, 50).applyMatrix4(state.viewTransform), new THREE.Vector3());

    const asteroid = { grid: {}, voxels: [{ worldAabb: { min: { x: 10, y: 20, z: 30 }, max: { x: 20, y: 30, z: 40 } } }] };
    configureRelativeView(asteroid);
    assertVector(new THREE.Vector3(15, 25, 35).applyMatrix4(state.viewTransform), new THREE.Vector3());
});
