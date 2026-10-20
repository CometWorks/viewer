import * as THREE from "three";
import { createReplayCharacter } from "./replay-character.js";
import { armorFallbackModelForDefinition } from "./armor-fallback-models.js";
import { createReplayRenderContext, prepareReplayModel, createReplayModelBatch, disposeReplayRenderContext, displayColorForBlock } from "./entity-renderer.js";
import { disposeTextureCache } from "./content-folder.js";
import { resolveModelAsset, clearModelCache } from "./mwm-loader.js";

// Reuse the live viewer's MWM parser and armor topology. Storage pages share model batches per grid.
export class ReplayGrids {
    constructor(changed) {
        this.changed = changed;
        this.models = new Map();
        this.armor = new Map();
        this.parts = new Map();
        this.box = new THREE.BoxGeometry(1, 1, 1);
        this.solid = new THREE.MeshLambertMaterial({ vertexColors: false });
        this.proxy = new THREE.MeshBasicMaterial({ wireframe: true });
        this.epoch = 0;
        this.active = 0;
        this.queue = [];
        this.revision = 0;
        this.modelBytes = 0;
        this.context = createReplayRenderContext();
        this.waiters = [];
        this.disposed = false;
    }
    clear() {
        this.epoch++; clearModelCache();
        this.queue.length = 0; this.waiters.splice(0).forEach(resolve => resolve());
        disposeReplayRenderContext(this.context); disposeTextureCache();
        if (!this.disposed) this.context = createReplayRenderContext();
        this.models.clear(); this.modelBytes = 0; this.revision++;
    }
    dispose() {
        if (this.disposed) return;
        this.disposed = true;
        this.clear();
        this.parts.clear(); this.box.dispose(); this.solid.dispose(); this.proxy.dispose();
        for (const geometry of this.armor.values()) geometry.dispose();
        this.armor.clear();
    }
    model(block) {
        const key = `${block.mod || ""}/${block.model || ""}`;
        let entry = this.models.get(key);
        if (!this.disposed && !entry && block.model) {
            entry = { pending: true };
            this.models.set(key, entry);
            this.queue.push({ key, block, entry, epoch: this.epoch });
            this.pump();
        }
        if (entry?.geometry) return { geometry: entry.geometry, materials: entry.materials, kind: "model", key, skin: entry.skin };
        const fallback = !block.mod && (block.build ?? 1) >= 0.999 ? armorFallbackModelForDefinition({ id: block.definition }) : null;
        if (fallback) {
            const key = fallback.description;
            let geometry = this.armor.get(key);
            if (!geometry) {
                geometry = new THREE.BufferGeometry();
                geometry.setAttribute("position", new THREE.Float32BufferAttribute(fallback.triangles.flatMap(t => t.flatMap(i => fallback.vertices[i])), 3));
                geometry.computeVertexNormals(); this.armor.set(key, geometry);
            }
            return { geometry, kind: "armor", key };
        }
        return { geometry: this.box, kind: "proxy", key: "proxy" };
    }
    preload(replay) {
        if (this.disposed) return Promise.resolve();
        // Successful assets and in-flight work belong to the window, not the range.
        // Failed model requests may succeed in a renewed streaming session.
        for (const [key, entry] of this.models) if (entry.error) this.models.delete(key);
        // Start with player appearances, then all historical pages, including future destruction stages.
        for (const record of replay.records) if (record.type === "player" && record.model) this.model(record);
        this.parts = replay.modelParts || new Map();
        for (const page of replay.geometry.values()) for (const block of page) for (const source of this.sources(block)) this.model(source);
        return this.active || this.queue.length ? new Promise(resolve => this.waiters.push(resolve)) : Promise.resolve();
    }
    sources(block) {
        const parts = this.parts.get(block.partsId)?.parts;
        if (!parts) return [block];
        const patterns = typeof block.patterns === "string" ? Uint8Array.from(atob(block.patterns), c => c.charCodeAt(0)) : block.patterns;
        return parts.map((part, index) => ({ ...block, model: part.model, partMatrix: part.matrix, recordedBlock: block,
            patternOffset: { x: patterns?.[index * 4], y: patterns?.[index * 4 + 1], z: patterns?.[index * 4 + 2], w: patterns?.[index * 4 + 3] } }));
    }
    get status() {
        const ready = [...this.models.values()].filter(entry => entry.geometry).length;
        return `${ready}/${this.models.size} models ready (${[...this.models.values()].filter(entry => entry.error).length} unavailable) · ${this.active + this.queue.length} loading · ${[...this.context.preloadedTextures.values()].filter(Boolean).length}/${this.context.preloadedTextures.size} textures loaded`;
    }
    pump() {
        while (this.active < 4 && this.queue.length) {
            const item = this.queue.shift(), context = this.context; this.active++;
            resolveModelAsset({ logicalPath: item.block.model, rootId: item.block.mod ? `mods-direct:${item.block.mod}` : "" })
                .then(async result => {
                    if (item.epoch !== this.epoch) return;
                    if (result.status !== "parsed") { item.entry.error = result.message; return; }
                    const model = result.model;
                    const bytes = (model.blendIndices?.byteLength || 0) + (model.blendWeights?.byteLength || 0) + model.positions.byteLength + model.indices.byteLength + (model.normals?.byteLength || 0) + (model.uvs?.byteLength || 0);
                    this.modelBytes += bytes;
                    const renderable = await prepareReplayModel(model, context);
                    if (item.epoch !== this.epoch || !renderable) return;
                    Object.assign(item.entry, renderable, { skin: model.bones?.length ? { bones: model.bones, indices: model.blendIndices, weights: model.blendWeights } : null }); this.revision++;
                }).catch(error => { item.entry.error = error.message; })
                .finally(() => { this.active--; this.pump(); if (!this.active && !this.queue.length) this.waiters.splice(0).forEach(resolve => resolve()); if (item.epoch === this.epoch) this.changed(); });
        }
    }
    character(sample) {
        const model = this.model(sample);
        if (model.kind !== "model") return null;
        return createReplayCharacter(model, sample) || createReplayModelBatch(model, [{ matrix: new THREE.Matrix4(), colorMask: mask(sample.color), block: sample }]);
    }
    update(group, sample, pages) {
        if (group.userData.epoch !== this.epoch || group.userData.gridSize !== sample.gridSize) {
            this.release(group);
            group.userData.epoch = this.epoch; group.userData.gridSize = sample.gridSize;
        }
        group.userData.pages ||= new Map();
        group.userData.batches ||= new Map();
        const batches = group.userData.batches, dirty = new Set();
        const retained = new Set(sample.layout);
        for (const [id, page] of group.userData.pages) {
            if (retained.has(id) && !page.pending.some(key => this.models.get(key)?.geometry)) continue;
            for (const key of page.keys) { batches.get(key).pages.delete(id); dirty.add(key); }
            group.userData.pages.delete(id);
        }
        for (const id of retained) {
            if (group.userData.pages.has(id)) continue;
            const page = { keys: new Set(), pending: new Set(), count: pages.get(id).length, proxies: 0 };
            for (const source of pages.get(id)) for (const block of this.sources(source)) {
                const model = this.model(block);
                const key = `${model.kind}/${model.key}`;
                if (!batches.has(key)) batches.set(key, { model, pages: new Map(), mesh: null });
                const batch = batches.get(key);
                if (!batch.pages.has(id)) batch.pages.set(id, []);
                batch.pages.get(id).push(block);
                page.keys.add(key); dirty.add(key);
                if (model.kind === "proxy") page.proxies++;
                if (model.kind !== "model" && block.model) page.pending.add(`${block.mod || ""}/${block.model}`);
            }
            page.pending = [...page.pending]; group.userData.pages.set(id, page);
        }
        // Pages are hash buckets, not spatial chunks. Consolidate identical models without
        // touching unrelated batches or rebuilding any instances for transform-only playback.
        for (const key of dirty) {
            const batch = batches.get(key);
            if (batch.mesh) { releaseInstances(batch.mesh); group.remove(batch.mesh); }
            if (!batch.pages.size) { batches.delete(key); continue; }
            batch.mesh = this.createBatch(batch.model, [...batch.pages.values()].flat(), sample.gridSize);
            group.add(batch.mesh);
        }
        return [...group.userData.pages.values()].reduce((sum, page) => ({ blocks: sum.blocks + page.count, proxies: sum.proxies + page.proxies }), { blocks: 0, proxies: 0 });
    }
    createBatch(model, blocks, gridSize) {
        let mesh;
        const instances = [];
        if (model.kind !== "model") mesh = new THREE.InstancedMesh(model.geometry, model.kind === "proxy" ? this.proxy : this.solid, blocks.length);
        const matrix = new THREE.Matrix4(), position = new THREE.Vector3(), rotation = new THREE.Quaternion(), scale = new THREE.Vector3();
        blocks.forEach((block, i) => {
            rotation.fromArray(block.rotation || [0, 0, 0, 1]).normalize();
            position.fromArray(block.translation);
            scale.setScalar(model.kind === "armor" ? gridSize : 1);
            if (model.kind === "proxy") {
                position.fromArray(block.min.map((v, axis) => (v + block.max[axis]) * gridSize / 2));
                scale.fromArray(block.min.map((v, axis) => (block.max[axis] - v + 1) * gridSize));
                rotation.identity();
            }
            matrix.compose(position, rotation, scale);
            if (model.kind === "model") {
                if (block.partMatrix) matrix.makeTranslation(...block.translation).multiply(new THREE.Matrix4().fromArray(block.partMatrix));
                instances.push({ matrix: matrix.clone(), colorMask: mask(block.color), patternOffset: block.patternOffset, block: block.recordedBlock || block }); return;
            }
            mesh.setMatrixAt(i, matrix);
            mesh.setColorAt(i, displayColorForBlock({ colorMaskHsv: mask(block.color) }));
        });
        if (model.kind === "model") mesh = createReplayModelBatch(model, instances);
        else {
            mesh.matrixAutoUpdate = false;
            mesh.instanceMatrix.needsUpdate = true; if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
            mesh.computeBoundingSphere();
        }
        mesh.userData.blocks = blocks.map(block => block.recordedBlock || block);
        mesh.userData.assetKind = model.kind;
        return mesh;
    }
    release(group) {
        releaseInstances(group);
        group.clear(); group.userData.pages?.clear(); group.userData.batches?.clear();
    }
}

function mask(color) { color ||= [0, -1, 0]; return { x: color[0], y: color[1], z: color[2] }; }
function releaseInstances(group) {
    group.traverse(object => {
        if (object.isSkinnedMesh) {
            object.skeleton.dispose(); object.geometry.dispose(); object.material.forEach(material => material.dispose()); return;
        }
        if (!object.isInstancedMesh) return;
        if (object.userData.isModelBatch) object.geometry.dispose(); // Per-batch UV offsets; base geometry remains shared.
        object.dispose();
    });
}
