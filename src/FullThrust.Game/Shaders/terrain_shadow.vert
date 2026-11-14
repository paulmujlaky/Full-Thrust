#version 450

#include "common.glsl"
#include "terrain_push.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 6) in vec3 inMorphPosition;

void main() {

    vec3 position = inPosition + inMorphPosition * TerrainMorph(inPosition);

    gl_Position = frame.shadowViewProjection[int(pc.center.w)] * (pc.model * vec4(position, 1.0));

}
