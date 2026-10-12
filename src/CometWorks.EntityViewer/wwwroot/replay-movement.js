// VRage.Game.MyCharacterMovementEnum: basic state in low four bits; direction and
// speed flags above it. Unknown/old recordings never infer gait from world velocity.
export function movementState(value) {
    if (!Number.isInteger(value)) return "movement not recorded";
    const basic = value & 15;
    if (value === 16384) return "climbing off ladder";
    if (basic === 7) return value & 256 ? "climbing up" : value & 512 ? "climbing down" : "on ladder";
    if (value & 240) return value & 2048 ? "sprinting" : value & 1024 ? "running" : basic === 2 ? "crouch walking" : "walking";
    if (value & 12288) return basic === 2 ? "crouch turning" : "turning";
    return ["standing", "sitting", "crouching", "flying", "falling", "jumping", "dead"][value] || `unknown movement (${value})`;
}
