import * as THREE from "three";
import { createVoxelDataChunkMesh, prepareReplayTerrain } from "./entity-renderer.js";
import { terrainAt } from "./replay-model.js";

export class ReplayTerrain {
    constructor(world) { this.world = world; this.meshes = new Map(); this.epoch = 0; this.ready = 0; }
    clear() {
        this.epoch++; this.ready = 0;
        for (const mesh of this.meshes.values()) if (mesh) {
            this.world.remove(mesh); mesh.geometry.dispose(); mesh.material.forEach(m => m.dispose());
        }
        this.meshes.clear();
    }
    async preload(replay, context, progress) {
        const epoch = this.epoch;
        // Four concurrent assets/material sets, one meshing slice at a time; every recorded
        // terrain revision is prepared before playback. No current terrain is substituted.
        const pages = [...replay.voxels];
        let next = 0;
        await Promise.all(Array.from({ length: 4 }, async () => {
            while (next < pages.length && epoch === this.epoch) {
                const [id, page] = pages[next++];
                await prepareReplayTerrain(page.palette, context);
                await new Promise(resolve => setTimeout(resolve, 0));
                if (epoch !== this.epoch || context.disposed) return;
                const v = a => ({ x: a[0], y: a[1], z: a[2] });
                const chunk = { voxelBodyId: page.body, chunkId: id, lod: page.lod,
                    storageMin: v([0, 0, 0]), size: v(page.size), content: page.content, materials: page.materials };
                const mesh = createVoxelDataChunkMesh(chunk, { positionLeftBottomCorner: v([0, 0, 0]) },
                    new Map(page.palette.map(m => [m.index, m])), { viewTransform: new THREE.Matrix4(), clipBounds: null,
                        countStats: false, preloadedTextures: context.preloadedTextures, textureToken: context.textureToken });
                if (mesh) {
                    mesh.visible = false; mesh.quaternion.fromArray(page.rotation).normalize();
                    mesh.userData.assetKind = `historical terrain (${2 ** page.lod} m samples)`; mesh.userData.replayPage = page; this.world.add(mesh);
                }
                this.meshes.set(id, mesh); this.ready++; progress();
            }
        }));
    }
    draw(replay, at, session, origin, center) {
        for (const mesh of this.meshes.values()) if (mesh) mesh.visible = false;
        let count = 0, coarsest = 0;
        for (const record of terrainAt(replay, at, session)) {
            const mesh = this.meshes.get(record.terrainId);
            const page = replay.voxels.get(record.terrainId);
            const position = new THREE.Vector3().fromArray(page.min).multiplyScalar(2 ** page.lod)
                .applyQuaternion(new THREE.Quaternion().fromArray(page.rotation)).add(new THREE.Vector3().fromArray(page.origin));
            const radius = Math.hypot(...page.size) * 2 ** page.lod / 2;
            const chunkCenter = new THREE.Vector3().fromArray(page.size).multiplyScalar(2 ** page.lod / 2)
                .applyQuaternion(new THREE.Quaternion().fromArray(page.rotation)).add(position);
            if (center && chunkCenter.distanceTo(new THREE.Vector3().fromArray(center)) > 500 + radius) continue;
            if (mesh) { mesh.visible = true; mesh.position.copy(position).sub(origin); mesh.userData.sample = record; }
            count++; coarsest = Math.max(coarsest, 2 ** page.lod);
        }
        return count ? `${count} historical terrain chunks · up to ${coarsest} m samples` : "terrain not recorded at this time";
    }
}
