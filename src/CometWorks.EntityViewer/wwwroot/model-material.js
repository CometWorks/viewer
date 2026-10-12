// Material.copy JSON-serializes userData. Shader uniforms contain live GPU textures;
// keep those references and clone only mutable uniform values, never serialize images.
export function cloneModelMaterial(original) {
    const source = Object.create(original);
    source.userData = {};
    const material = new original.constructor().copy(source);
    material.userData = { ...original.userData };
    if (original.userData.seColorMaskUniforms) material.userData.seColorMaskUniforms = Object.fromEntries(
        Object.entries(original.userData.seColorMaskUniforms).map(([key, uniform]) => [key,
            { value: uniform.value?.clone && !uniform.value.isTexture ? uniform.value.clone() : uniform.value }]));
    material.onBeforeCompile = original.onBeforeCompile;
    material.customProgramCacheKey = original.customProgramCacheKey;
    return material;
}
