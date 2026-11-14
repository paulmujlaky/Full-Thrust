#version 450

#include "common.glsl"

layout(location = 0) in vec2 vUv;

layout(set = 1, binding = 0) uniform sampler2D volumeColor;
layout(set = 1, binding = 1) uniform sampler2D volumeDepth;
layout(set = 1, binding = 2) uniform sampler2D sceneDepth;

layout(location = 0) out vec4 outColor;

layout(push_constant) uniform Push {

    // xy: which pixel of each 2x2 block the half-resolution samples sit on this frame.
    vec4 offset;

} pc;

float ViewDistance(float depth) {

    return frame.projection[3][2] / max(depth, 1e-12);

}

// How well a half-resolution sample stands for a full-resolution pixel: 1 on the same surface, 0 across a silhouette.
float DepthMatch(ivec2 tap, float distance) {

    float difference = abs(ViewDistance(texelFetch(volumeDepth, tap, 0).r) - distance) / (distance * 0.02 + 0.05);

    return exp(-difference * difference);

}

// Depth-aware upsample of the half-resolution volumes; blending gives scene * transmittance + light.
void main() {

    ivec2 size = textureSize(volumeColor, 0);
    vec2 position = (gl_FragCoord.xy - 0.5 - pc.offset.xy) * 0.5;
    ivec2 base = ivec2(floor(position));
    vec2 f = position - vec2(base);

    float distance = ViewDistance(texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r);

    vec4 sum = vec4(0.0);
    float weights = 0.0;
    float matched = 0.0;

    for (int y = 0; y < 2; y++) {

        for (int x = 0; x < 2; x++) {

            ivec2 tap = clamp(base + ivec2(x, y), ivec2(0), size - 1);
            float bilinear = (x == 0 ? 1.0 - f.x : f.x) * (y == 0 ? 1.0 - f.y : f.y);
            float match = DepthMatch(tap, distance);

            sum += texelFetch(volumeColor, tap, 0) * (bilinear * match);
            weights += bilinear * match;
            matched = max(matched, match);

        }

    }

    if (matched > 0.5) {

        outColor = sum / weights;

        return;

    }

    // None of the four samples saw this pixel's surface: a thin edge between them. The best match among the sixteen around takes over, so a silhouette keeps its own side's volume instead of a blocky blend of both.
    vec4 best = vec4(0.0);
    float bestMatch = -1.0;

    for (int y = -1; y <= 2; y++) {

        for (int x = -1; x <= 2; x++) {

            ivec2 tap = clamp(base + ivec2(x, y), ivec2(0), size - 1);
            vec2 offset = vec2(x, y) - f;
            float match = DepthMatch(tap, distance) - 0.02 * dot(offset, offset);

            if (match > bestMatch) {

                bestMatch = match;
                best = texelFetch(volumeColor, tap, 0);

            }

        }

    }

    // Where even those miss, the plain blend is the least wrong.
    float found = smoothstep(0.05, 0.5, bestMatch);
    vec4 blended = weights > 1e-4 ? sum / weights : best;

    outColor = mix(blended, best, max(found, weights > 1e-4 ? 0.0 : 1.0));

}
