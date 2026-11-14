#version 450

#include "common.glsl"
#include "mesh_material.glsl"

layout(location = 3) in vec2 vUv;
layout(location = 10) in vec4 vClip;
layout(location = 11) in vec4 vPreviousClip;

layout(location = 0) out vec2 outVelocity;

void main() {

    if ((!gl_FrontFacing && material.flags.y < 0.5) || material.baseColor.a * texture(baseColorMap, vUv).a < material.params.w) {

        discard;

    }

    outVelocity = ScreenVelocity(vClip, vPreviousClip);

}
