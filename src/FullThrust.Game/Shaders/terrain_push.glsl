// Mirrors TerrainPush in Terrain/TerrainRenderer.cs.
layout(push_constant) uniform Push {

    mat4 model;
    vec4 detail;

    // xyz: chunk centre in the body-fixed frame; w: shadow cascade for the shadow vertex shader.
    vec4 center;

    // x: the body's slot in the terrain material uniforms.
    ivec4 body;

    // Camera distances between which a vertex slides from its own level's surface onto its parent's.
    vec4 morph;

} pc;

// 0 near the camera, 1 where the next coarser level takes over, so neighbouring levels always meet.
float TerrainMorph(vec3 position) {

    vec3 world = (pc.model * vec4(position, 1.0)).xyz;

    return smoothstep(pc.morph.x, pc.morph.y, distance(world, frame.cameraPosition.xyz));

}
