import * as THREE from "three";
import { movementState } from "./replay-movement.js";
import { cloneModelMaterial } from "./model-material.js";

// GPU skinning uses the asset's bind skeleton; the server never records bone frames.
export function createReplayCharacter(model, sample) {
    const skin = model.skin;
    if (!skin?.bones?.length || !skin.indices || !skin.weights || skin.indices.length !== model.geometry.attributes.position.count * 4
        || skin.weights.length !== skin.indices.length || skin.indices.some(i => i >= skin.bones.length) || skin.weights.some(w => !Number.isFinite(w) || w < 0)) return null;
    const geometry = model.geometry.clone();
    geometry.setAttribute("skinIndex", new THREE.Uint16BufferAttribute(skin.indices, 4));
    geometry.setAttribute("skinWeight", new THREE.Float32BufferAttribute(skin.weights, 4));
    const materials = model.materials.map(original => {
        const material = cloneModelMaterial(original);
        if (material.userData.seColorMaskUniforms) material.userData.seColorMaskUniforms.seBlockColorMask.value.fromArray(sample.color || [0, -1, 0]);
        material.customProgramCacheKey = () => original.customProgramCacheKey() + "/replay-skin";
        return material;
    });
    const mesh = new THREE.SkinnedMesh(geometry, materials);
    const bones = skin.bones.map(definition => {
        const bone = new THREE.Bone(); bone.name = definition.name;
        new THREE.Matrix4().fromArray(definition.transform).decompose(bone.position, bone.quaternion, bone.scale);
        bone.userData.bindQuaternion = bone.quaternion.clone(); bone.userData.bindPosition = bone.position.clone();
        return bone;
    });
    skin.bones.forEach((definition, i) => (definition.parent < 0 ? mesh : bones[definition.parent]).add(bones[i]));
    mesh.updateMatrixWorld(true);
    for (const bone of bones) {
        const inverse = bone.getWorldQuaternion(new THREE.Quaternion()).invert();
        bone.userData.motionAxis = new THREE.Vector3(1, 0, 0).applyQuaternion(inverse);
        bone.userData.verticalAxis = new THREE.Vector3(0, 1, 0).applyQuaternion(bone.parent.getWorldQuaternion(new THREE.Quaternion()).invert());
        const child = bone.children.find(child => child.isBone);
        if (child && bone.name.toLowerCase().includes("upperarm")) {
            const direction = child.position.clone().normalize();
            bone.userData.restArm = new THREE.Quaternion().setFromUnitVectors(direction, new THREE.Vector3(0, -1, 0).applyQuaternion(inverse));
            bone.userData.aimArm = new THREE.Quaternion().setFromUnitVectors(direction, new THREE.Vector3(0, -.15, -1).normalize().applyQuaternion(inverse));
        }
    }
    mesh.bind(new THREE.Skeleton(bones)); mesh.normalizeSkinWeights();
    mesh.castShadow = true; mesh.receiveShadow = true;
    mesh.frustumCulled = false; // Reconstructed poses can leave the asset's bind-pose bounds.
    mesh.userData.assetKind = "character model (GPU skinning; reconstructed animation)";
    return mesh;
}

export function animateReplayCharacter(mesh, sample, at, enabled = true) {
    if (!mesh?.isSkinnedMesh) return;
    const flags = sample.actions ?? 0;
    const mode = movementState(sample.movement);
    const ground = ["walking", "running", "sprinting", "crouch walking"].includes(mode);
    const climbing = mode.startsWith("climbing") || mode === "on ladder";
    const sitting = mode === "sitting" || (sample.movement == null && !!(flags & 32));
    const crouching = mode.startsWith("crouch") || (sample.movement == null && !!(flags & 8));
    const airborne = ["flying", "falling", "jumping"].includes(mode);
    const moving = ground || ["climbing up", "climbing down"].includes(mode) ? 1 : 0; // World velocity includes ships/jetpacks: it cannot identify walking.
    const phase = at / 1000 * (mode === "sprinting" ? 11 : mode === "running" ? 9 : 7);
    const swing = Math.sin(phase) * moving * .45;
    const q = new THREE.Quaternion();
    for (const bone of mesh.skeleton.bones) {
        bone.quaternion.copy(bone.userData.bindQuaternion); bone.position.copy(bone.userData.bindPosition);
        if (!enabled || sample.actions == null && sample.movement == null) continue;
        const name = bone.name.toLowerCase();
        const side = name.includes("rigl") || name.includes("left") ? 1 : -1;
        let angle = 0;
        if (name.includes("thigh")) angle = sitting ? -1.25 : climbing ? -.6 + side * swing : airborne ? -.12 : side * swing - (crouching ? .6 : 0);
        if (name.includes("calf")) angle = sitting ? 1.4 : climbing ? .8 - side * swing : airborne ? .25 : Math.max(0, -side * swing) + (crouching ? 1 : 0);
        if (name.includes("upperarm")) {
            const arm = flags & 7 || climbing ? bone.userData.aimArm : bone.userData.restArm;
            if (arm) bone.quaternion.multiply(arm);
            angle = flags & 6 ? Math.sin(at / 35) * .025 : climbing ? side * swing : -side * swing * .6;
        }
        if (name.includes("head") && !name.includes("dummy")) angle = THREE.MathUtils.degToRad(sample.view?.[0] || 0);
        if (name.includes("pelvis")) {
            if (flags & 64 || mode === "dead") angle = Math.PI / 2;
            if (crouching) bone.position.addScaledVector(bone.userData.verticalAxis, -.25);
        }
        if (angle) bone.quaternion.multiply(q.setFromAxisAngle(bone.userData.motionAxis, angle));
    }
}
