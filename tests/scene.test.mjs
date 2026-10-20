import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { registerHooks } from "node:module";
import test from "node:test";
import * as THREE from "three";
import { els, state } from "../src/CometWorks.EntityViewer/wwwroot/state.js";

const sceneUrl = new URL("../src/CometWorks.EntityViewer/wwwroot/scene.js", import.meta.url).href;
const source = await readFile(new URL(sceneUrl), "utf8");
const hooks = registerHooks({
    resolve(specifier, context, next) {
        return next(specifier === "zip.js" ? "@zip.js/zip.js" : specifier, context);
    },
    load(url, context, next) {
        if (url === sceneUrl) return { format: "module", shortCircuit: true, source: `${source}\nexport { onPointerMove };` };
        return next(url, context);
    },
});
const { onPointerMove, updateLighting, updateSceneBounds, requestRender, invalidateShadows } = await import(sceneUrl);
hooks.deregister();

test("hover picks visible instances, terrain and diagnostic lines without testing hidden LODs or overlays", t => {
    const previousState = { ...state }, previousReadout = els.hoverReadout;
    const previousFrame = globalThis.requestAnimationFrame;
    let requestedFrames = 0;
    globalThis.requestAnimationFrame = () => ++requestedFrames;
    t.after(() => { Object.assign(state, previousState); els.hoverReadout = previousReadout; globalThis.requestAnimationFrame = previousFrame; });
    const geometry = new THREE.BoxGeometry(), material = new THREE.MeshBasicMaterial();
    t.after(() => { geometry.dispose(); material.dispose(); });
    const lod = new THREE.LOD();
    const hidden = new THREE.Group();
    const expensive = new THREE.Mesh(geometry, material);
    expensive.raycast = () => assert.fail("Hidden geometry must never be raycast");
    hidden.add(expensive);
    const instances = new THREE.InstancedMesh(geometry, material, 1);
    instances.setMatrixAt(0, new THREE.Matrix4());
    instances.userData.blocks = [{ id: "visible-instance", blockTypeId: "Armor" }];
    lod.addLevel(hidden, 0);
    lod.addLevel(instances, 5);
    Object.assign(state, {
        scene: new THREE.Scene(), animationFrameHandle: 0, viewerDisposed: false,
        gridGroup: new THREE.Group(), voxelGroup: new THREE.Group(),
        logisticsGroup: new THREE.Group(), damagedGroup: null,
        raycaster: new THREE.Raycaster(), pointer: new THREE.Vector2(),
        camera: new THREE.PerspectiveCamera(55, 1, 0.1, 100), cameraMode: "orbit",
        renderer: { domElement: { getBoundingClientRect: () => ({ left: 0, top: 0, width: 100, height: 100 }) } },
    });
    els.hoverReadout = { textContent: "" };
    state.camera.position.z = 10;
    state.camera.updateMatrixWorld(true);
    lod.update(state.camera);
    state.logisticsGroup.visible = false;
    state.logisticsGroup.add(expensive.clone());
    state.logisticsGroup.children[0].raycast = expensive.raycast;
    state.gridGroup.add(lod, state.logisticsGroup);
    state.gridGroup.updateMatrixWorld(true);
    const hover = () => onPointerMove({ clientX: 50, clientY: 50, buttons: 0 });
    hover();
    assert.match(els.hoverReadout.textContent, /visible-instance/);

    state.gridGroup.visible = false;
    instances.raycast = expensive.raycast;
    const terrain = new THREE.Mesh(geometry, material);
    terrain.userData.voxel = { displayName: "Visible terrain" };
    state.voxelGroup.add(terrain);
    state.voxelGroup.updateMatrixWorld(true);
    hover();
    assert.match(els.hoverReadout.textContent, /Visible terrain/);
    state.voxelGroup.visible = false;
    terrain.raycast = expensive.raycast;
    hover();
    assert.equal(els.hoverReadout.textContent, "No block or voxel selected");

    state.gridGroup.visible = true;
    lod.visible = false;
    state.logisticsGroup.clear();
    state.logisticsGroup.visible = true;
    const lineGeometry = new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(-1, 0, 0), new THREE.Vector3(1, 0, 0)]);
    const lineMaterial = new THREE.LineBasicMaterial();
    lineMaterial.userData.logisticsBaseOpacity = 0.8;
    t.after(() => { lineGeometry.dispose(); lineMaterial.dispose(); });
    const line = new THREE.Line(lineGeometry, lineMaterial);
    line.userData.logisticsEdge = { systemId: 7 };
    line.userData.logisticsSystemId = 7;
    state.logisticsGroup.add(line);
    state.gridGroup.updateMatrixWorld(true);
    state.animationFrameHandle = 0;
    const beforeHover = requestedFrames;
    hover();
    assert.match(els.hoverReadout.textContent, /Logistics edge \| system 7/);
    assert.equal(lineMaterial.opacity, 0.8);
    assert.equal(requestedFrames, beforeHover + 1, "Hover focus must wake the idle renderer");
    state.animationFrameHandle = 0;
    hover();
    assert.equal(requestedFrames, beforeHover + 1, "Unchanged focus must not keep rendering");
    line.visible = false;
    hover();
    assert.equal(lineMaterial.opacity, 0.8 * 0.72);
    assert.equal(requestedFrames, beforeHover + 2, "Clearing focus must also wake the renderer");
});

test("lighting and terrain visibility changes invalidate cached shadows", t => {
    const previousState = { ...state }, previousLighting = els.showLighting;
    const previousDocument = globalThis.document, previousStyle = globalThis.getComputedStyle;
    globalThis.document = { documentElement: {} };
    globalThis.getComputedStyle = () => ({ getPropertyValue: () => "" });
    const previousFrame = globalThis.requestAnimationFrame;
    globalThis.requestAnimationFrame = () => 1;
    t.after(() => {
        Object.assign(state, previousState); els.showLighting = previousLighting;
        globalThis.document = previousDocument; globalThis.getComputedStyle = previousStyle;
        globalThis.requestAnimationFrame = previousFrame;
    });
    Object.assign(state, {
        renderer: { shadowMap: { needsUpdate: false } },
        scene: new THREE.Scene(), sunLight: new THREE.DirectionalLight(), sunLightTarget: new THREE.Object3D(),
        gridGroup: null, voxelGroup: new THREE.Group(), gridLights: [], gridLightGroup: null,
        sunMarker: null, sunMarkerLine: null, floorGrid: null, clippingBox: null,
        currentBounds: new THREE.Box3(new THREE.Vector3(-1, -1, -1), new THREE.Vector3(1, 1, 1)),
        lastScene: null, contextBounds: null, contextClipBounds: null,
    });
    els.showLighting = { checked: false };
    updateLighting();
    assert.equal(state.sunLight.visible, false);
    assert.equal(state.renderer.shadowMap.needsUpdate, true);
    state.renderer.shadowMap.needsUpdate = false;
    els.showLighting.checked = true;
    updateLighting();
    assert.equal(state.sunLight.visible, true);
    assert.equal(state.renderer.shadowMap.needsUpdate, true);
    state.renderer.shadowMap.needsUpdate = false;
    state.voxelGroup.visible = false;
    updateSceneBounds(false); // Same path as the Voxels control.
    assert.equal(state.renderer.shadowMap.needsUpdate, true);
});

test("main viewer wakes on changes, settles after damping and keeps shadows cached during camera movement", t => {
    const previousState = {...state}, previousFrame = globalThis.requestAnimationFrame;
    const callbacks = new Map(); let next = 0, renders = 0, shadowPasses = 0, damping = 2;
    globalThis.requestAnimationFrame = callback => { callbacks.set(++next, callback); return next; };
    t.after(() => { Object.assign(state, previousState); globalThis.requestAnimationFrame = previousFrame; });
    Object.assign(state, {viewerDisposed:false,animationFrameHandle:0,lastFrameTime:0,cameraMode:'orbit',
        scene:new THREE.Scene(),camera:new THREE.PerspectiveCamera(),flyKeys:new Set(),
        controls:{update() { if (damping-- > 0) requestRender(); }},
        renderer:{shadowMap:{needsUpdate:false},render() {
            renders++; if (this.shadowMap.needsUpdate) {shadowPasses++; this.shadowMap.needsUpdate=false;}
        }},
    });
    const tick = time => { const [id,callback] = callbacks.entries().next().value; callbacks.delete(id); callback(time); };
    invalidateShadows(); requestRender(); requestRender();
    assert.equal(callbacks.size,1);
    tick(16); tick(32); tick(48);
    assert.equal(renders,3); assert.equal(shadowPasses,1);
    assert.equal(callbacks.size,0); assert.equal(state.lastFrameTime,0);
    requestRender(); tick(10000);
    assert.equal(renders,4); assert.equal(shadowPasses,1);
    invalidateShadows(); tick(10016);
    assert.equal(shadowPasses,2);
    state.cameraMode='fly'; state.flyKeys.add('KeyW');
    requestRender(); tick(20000);
    assert.equal(state.camera.position.z,0,'Idle time must not become movement');
    tick(20016); assert.ok(state.camera.position.z < 0);
    state.flyKeys.clear(); tick(20032);
    assert.equal(callbacks.size,0);
    state.viewerDisposed=true; invalidateShadows();
    assert.equal(callbacks.size,0);
});
