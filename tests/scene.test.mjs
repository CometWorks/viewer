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
const { onPointerMove, updateLighting, updateSceneBounds } = await import(sceneUrl);
hooks.deregister();

test("hover picks visible instances, terrain and diagnostic lines without testing hidden LODs or overlays", t => {
    const previousState = { ...state }, previousReadout = els.hoverReadout;
    t.after(() => { Object.assign(state, previousState); els.hoverReadout = previousReadout; });
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
    t.after(() => { lineGeometry.dispose(); lineMaterial.dispose(); });
    const line = new THREE.Line(lineGeometry, lineMaterial);
    line.userData.logisticsEdge = { systemId: 7 };
    state.logisticsGroup.add(line);
    state.gridGroup.updateMatrixWorld(true);
    hover();
    assert.match(els.hoverReadout.textContent, /Logistics edge \| system 7/);
});

test("lighting and terrain visibility changes invalidate cached shadows", t => {
    const previousState = { ...state }, previousLighting = els.showLighting;
    const previousDocument = globalThis.document, previousStyle = globalThis.getComputedStyle;
    globalThis.document = { documentElement: {} };
    globalThis.getComputedStyle = () => ({ getPropertyValue: () => "" });
    t.after(() => {
        Object.assign(state, previousState); els.showLighting = previousLighting;
        globalThis.document = previousDocument; globalThis.getComputedStyle = previousStyle;
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
