#version 450

#include "common.glsl"
#include "mesh_push.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 2) in vec2 inUv;
layout(location = 3) out vec2 vUv;
layout(location = 10) out vec4 vClip;
layout(location = 11) out vec4 vPreviousClip;

void main() {

    gl_Position = frame.shadowViewProjection[pc.view.x] * (pc.model * vec4(inPosition, 1.0));
    vUv = inUv;

    // The cutout fragment shader is shared with the camera prepass, which reads these.
    vClip = vec4(0.0, 0.0, 0.0, 1.0);
    vPreviousClip = vec4(0.0, 0.0, 0.0, 1.0);

}
