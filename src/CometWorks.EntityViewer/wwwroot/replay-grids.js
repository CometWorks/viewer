import * as THREE from "three";
import { armorFallbackModelForDefinition } from "./armor-fallback-models.js";
import { resolveModelAsset, clearModelCache } from "./mwm-loader.js";

// Reuse the live viewer's MWM parser and armor topology. Only changed layout pages rebuild.
export class ReplayGrids {
    constructor(changed) {
        this.changed = changed;
        this.models = new Map();
        this.armor = new Map();
        this.box = new THREE.BoxGeometry(1, 1, 1);
        this.solid = new THREE.MeshLambertMaterial({ vertexColors: false });
        this.proxy = new THREE.MeshBasicMaterial({ wireframe: true });
        this.epoch = 0;
        this.active = 0;
        this.queue = [];
        this.revision = 0;
        this.modelBytes = 0;
    }
    clear() {
        this.epoch++; clearModelCache();
        this.queue.length = 0;
        for (const entry of this.models.values()) entry.geometry?.dispose();
        this.models.clear(); this.modelBytes = 0; this.revision++;
    }
    dispose() {
        this.clear(); this.box.dispose(); this.solid.dispose(); this.proxy.dispose();
        for (const geometry of this.armor.values()) geometry.dispose();
        this.armor.clear();
    }
    model(block) {
        const key = `${block.mod || ""}/${block.model || ""}`;
        let entry = this.models.get(key);
        if (!entry && block.model && this.models.size < 128) {
            entry = { pending: true };
            this.models.set(key, entry);
            this.queue.push({ key, block, entry, epoch: this.epoch });
            this.pump();
        }
        if (entry?.geometry) return { geometry: entry.geometry, kind: "model", key };
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
    pump() {
        while (this.active < 4 && this.queue.length) {
            const item = this.queue.shift(); this.active++;
            resolveModelAsset({ logicalPath: item.block.model, rootId: item.block.mod ? `mods-direct:${item.block.mod}` : "" })
                .then(result => {
                    if (item.epoch !== this.epoch || result.status !== "parsed") return;
                    const model = result.model;
                    const bytes = model.positions.byteLength + model.indices.byteLength + (model.normals?.byteLength || 0);
                    if (this.modelBytes + bytes > 64 * 1024 * 1024) return;
                    const geometry = new THREE.BufferGeometry();
                    geometry.setAttribute("position", new THREE.BufferAttribute(model.positions, 3));
                    geometry.setIndex(new THREE.BufferAttribute(model.indices, 1));
                    if (model.normals) geometry.setAttribute("normal", new THREE.BufferAttribute(model.normals, 3));
                    else geometry.computeVertexNormals();
                    item.entry.geometry = geometry; this.modelBytes += bytes; this.revision++;
                    this.changed();
                }).catch(() => { /* Missing assets remain explicit wireframe block proxies. */ })
                .finally(() => { this.active--; this.pump(); });
        }
    }
    update(group, sample, pages) {
        group.userData.pages ||= new Map();
        const retained = new Set(sample.layout);
        for (const [id, child] of group.userData.pages) {
            if (retained.has(id) && child.userData.revision === this.revision) continue;
            child.traverse(object => { if (object.isInstancedMesh) object.dispose(); });
            group.remove(child); group.userData.pages.delete(id);
        }
        for (const id of retained) {
            if (group.userData.pages.has(id)) continue;
            const page = new THREE.Group();
            const batches = new Map();
            for (const block of pages.get(id)) {
                const model = this.model(block);
                const key = `${model.kind}/${model.key}`;
                if (!batches.has(key)) batches.set(key, { model, blocks: [] });
                batches.get(key).blocks.push(block);
            }
            let proxies = 0, count = 0;
            for (const { model, blocks } of batches.values()) {
                const mesh = new THREE.InstancedMesh(model.geometry, model.kind === "proxy" ? this.proxy : this.solid, blocks.length);
                const matrix = new THREE.Matrix4(), position = new THREE.Vector3(), rotation = new THREE.Quaternion(), scale = new THREE.Vector3();
                blocks.forEach((block, i) => {
                    rotation.fromArray(block.rotation || [0, 0, 0, 1]).normalize();
                    position.fromArray(block.translation);
                    scale.setScalar(model.kind === "armor" ? sample.gridSize : 1);
                    if (model.kind === "proxy") {
                        position.fromArray(block.min.map((v, axis) => (v + block.max[axis]) * sample.gridSize / 2));
                        scale.fromArray(block.min.map((v, axis) => (block.max[axis] - v + 1) * sample.gridSize));
                        rotation.identity(); proxies++;
                    }
                    matrix.compose(position, rotation, scale); mesh.setMatrixAt(i, matrix);
                    // Integrity is observed state; red tint is an audit overlay, not reconstructed damage textures.
                    const hsv = block.color || [0, -1, 0];
                    const color = new THREE.Color().setHSL(hsv[0], Math.min(1, Math.max(0, hsv[1] + 0.8)), Math.min(0.8, Math.max(0.2, hsv[2] + 0.5)));
                    color.lerp(new THREE.Color(0xff4050), (1 - (block.integrity ?? 0)) * 0.7);
                    mesh.setColorAt(i, color); count++;
                });
                mesh.instanceMatrix.needsUpdate = true; mesh.instanceColor.needsUpdate = true;
                mesh.computeBoundingSphere(); page.add(mesh);
            }
            page.userData = { revision: this.revision, proxies, count };
            group.userData.pages.set(id, page); group.add(page);
        }
        return [...group.userData.pages.values()].reduce((sum, page) => ({ blocks: sum.blocks + page.userData.count, proxies: sum.proxies + page.userData.proxies }), { blocks: 0, proxies: 0 });
    }
    release(group) {
        group.traverse(object => { if (object.isInstancedMesh) object.dispose(); });
        group.clear(); group.userData.pages?.clear();
    }
}
