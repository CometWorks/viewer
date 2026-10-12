import * as THREE from "three";
import { movementState } from "./replay-movement.js";
import { actionLabels } from "./replay-model.js";

// Same basis as ViewerReferenceFrame.GravityAligned: preserve projected grid forward.
export function replayViewRotation(sample) {
    const rotation = new THREE.Quaternion().fromArray(sample?.rotation || [0, 0, 0, 1]).normalize();
    const gravity = new THREE.Vector3().fromArray(sample?.gravity || [0, 0, 0]);
    if (gravity.lengthSq() < 0.0001) return rotation.invert();
    const up = gravity.normalize().negate();
    const forward = new THREE.Vector3(0, 0, -1).applyQuaternion(rotation);
    forward.addScaledVector(up, -forward.dot(up));
    if (forward.lengthSq() < 0.0001) forward.crossVectors(up, new THREE.Vector3(1, 0, 0).applyQuaternion(rotation));
    forward.normalize();
    const right = new THREE.Vector3().crossVectors(forward, up).normalize();
    return new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(right, up, forward.negate())).invert();
}

export function diagnosticText(sample, block, at, assetKind = "bounds") {
    const vector = value => value ? value.map(v => Number(v).toFixed(2)).join(", ") : "not recorded";
    return [
        `${sample.name || sample.entity} (${sample.type})`, `Entity: ${sample.entity}`,
        `Observed: ${new Date(sample.at).toISOString()} · age ${Math.max(0, at - sample.at) / 1000}s · ${sample.high ? "incident" : "baseline"}`,
        sample.interpolated ? `Motion: interpolated toward ${new Date(sample.nextAt).toISOString()}` : "Motion: held observation",
        ...(sample.type === "player" ? [`Movement: ${movementState(sample.movement)}${sample.movement == null ? "" : ` (state ${sample.movement})`}`, `Actions: ${actionLabels(sample.actions)}`, `View pitch/yaw: ${vector(sample.view)} degrees`] : []),
        `Position: ${vector(sample.position)} m`, `Velocity: ${vector(sample.velocity)} m/s`,
        `Angular velocity: ${vector(sample.angularVelocity)} rad/s`, `Natural gravity: ${vector(sample.gravity)} m/s²`,
        `Source / controlled entity: ${sample.source || "not recorded"}`, `Rendered: ${assetKind}`,
        ...(block?.cell ? [`Block: ${block.definition} · cell ${block.cell.join(", ")}`,
            `Integrity: ${((block.integrity ?? 0) * 100).toFixed(1)}% · build ${((block.build ?? 0) * 100).toFixed(1)}%`, `Model: ${block.model || "unavailable"}`] : []),
        ...(sample.detail ? [sample.detail] : []),
    ].join("\n");
}
