#version 450

#include "common.glsl"
#include "mesh_push.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inTangent;

layout(location = 0) out vec3 vWorldPosition;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec4 vTangent;
layout(location = 3) out vec2 vUv;
layout(location = 10) out vec4 vClip;
layout(location = 11) out vec4 vPreviousClip;

// The depth prepass runs this shader too, and the opaque pass must reproduce its depth exactly.
invariant gl_Position;

void main() {

    vec4 local = vec4(inPosition, 1.0);
    vec4 world = pc.model * local;
    mat3 normalMatrix = transpose(inverse(mat3(pc.model)));

    gl_Position = frame.viewProjection * world;

    vWorldPosition = world.xyz;
    vNormal = normalMatrix * inNormal;
    vTangent = vec4(mat3(pc.model) * inTangent.xyz, inTangent.w);
    vUv = inUv;

    vClip = frame.unjitteredViewProjection * world;
    vPreviousClip = frame.previousViewProjection * vec4(dot(pc.previous0, local), dot(pc.previous1, local), dot(pc.previous2, local), 1.0);

}
