#version 450

#include "common.glsl"

layout(location = 10) in vec4 vClip;
layout(location = 11) in vec4 vPreviousClip;

layout(location = 0) out vec2 outVelocity;

void main() {

    outVelocity = ScreenVelocity(vClip, vPreviousClip);

}
