#version 450

layout(location = 0) in vec2 vUv;

layout(set = 0, binding = 0) uniform sampler2D hdr;
layout(set = 0, binding = 1) uniform sampler2D bloom;

layout(push_constant) uniform Push {

    int tonemapper;
    float whitePoint;
    float bloomIntensity;

    // Lens: share of bright light thrown into ghosts and a halo, colour fringing at the corners in pixels, and how far the corners darken.
    float flare;
    float fringe;
    float vignette;

} pc;

layout(location = 0) out vec4 outColor;

// Narkowicz's fit of the ACES RRT and sRGB ODT.
vec3 Aces(vec3 x) {

    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);

}

// Per channel, a bright orange glow saturates every channel and turns white; past white this leans towards scaling by the brightest channel, which keeps the hue.
vec3 HueKeepingAces(vec3 color) {

    float peak = max(color.r, max(color.g, color.b));
    vec3 channels = Aces(color);

    if (peak <= 1.0) {

        return channels;

    }

    float white = Aces(vec3(peak)).r;
    vec3 hue = color * (white / peak);

    // Far past white, film bleaches towards white; holding the full hue turned bright limb clouds pink.
    hue = mix(hue, vec3(white), 0.6 * smoothstep(1.0, 8.0, peak));

    return mix(channels, hue, 0.5 * smoothstep(1.0, 4.0, peak));

}

// Minimal AgX (Troy Sobotka) with a sigmoid fit; output is linear sRGB.
vec3 AgX(vec3 color) {

    const mat3 inset = mat3(0.842479, 0.0423282, 0.0423756, 0.0784336, 0.878468, 0.0784336, 0.0792237, 0.0791661, 0.879142);
    const mat3 outset = mat3(1.19688, -0.0528969, -0.0529716, -0.0980209, 1.15190, -0.0980435, -0.0990297, -0.0989612, 1.15107);
    const float minEv = -12.47393;
    const float maxEv = 4.026069;

    vec3 x = inset * max(color, vec3(1e-10));

    x = clamp((log2(x) - minEv) / (maxEv - minEv), 0.0, 1.0);

    vec3 x2 = x * x;
    vec3 x4 = x2 * x2;

    x = 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
    x = outset * x;

    return pow(max(x, vec3(0.0)), vec3(2.2));

}

// Khronos PBR Neutral: keeps base colours faithful up to the highlights.
vec3 PbrNeutral(vec3 color) {

    const float startCompression = 0.8 - 0.04;
    const float desaturation = 0.15;

    float x = min(color.r, min(color.g, color.b));
    float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;

    color -= offset;

    float peak = max(color.r, max(color.g, color.b));

    if (peak < startCompression) {

        return color;

    }

    float d = 1.0 - startCompression;
    float newPeak = 1.0 - d * d / (peak + d - startCompression);

    color *= newPeak / peak;

    float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);

    return mix(color, vec3(newPeak), g);

}

vec3 Reinhard(vec3 x, float white) {

    return x * (1.0 + x / (white * white)) / (1.0 + x);

}

// Interleaved gradient noise breaks up banding in dark gradients before 8-bit quantisation.
float Dither(vec2 pixel) {

    return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715)))) - 0.5;

}

float EdgeLuma(vec3 color) {

    float y = dot(color, vec3(0.2126, 0.7152, 0.0722));

    return sqrt(y / (1.0 + y));

}

vec3 ResolveEdges(vec2 uv) {

    vec2 pixel = 1.0 / vec2(textureSize(hdr, 0));
    vec3 center = texture(hdr, uv).rgb;
    float nw = EdgeLuma(textureOffset(hdr, uv, ivec2(-1, -1)).rgb);
    float ne = EdgeLuma(textureOffset(hdr, uv, ivec2(1, -1)).rgb);
    float sw = EdgeLuma(textureOffset(hdr, uv, ivec2(-1, 1)).rgb);
    float se = EdgeLuma(textureOffset(hdr, uv, ivec2(1, 1)).rgb);
    float mid = EdgeLuma(center);
    float low = min(mid, min(min(nw, ne), min(sw, se)));
    float high = max(mid, max(max(nw, ne), max(sw, se)));

    if (high - low < max(0.035, high * 0.13)) {

        return center;

    }

    vec2 tangent = vec2(sw + se - nw - ne, nw + sw - ne - se);
    float reduction = max((nw + ne + sw + se) * 0.03125, 0.008);
    vec2 span = clamp(tangent / (min(abs(tangent.x), abs(tangent.y)) + reduction), -6.0, 6.0) * pixel;
    vec3 inner = 0.5 * (texture(hdr, uv - span / 6.0).rgb + texture(hdr, uv + span / 6.0).rgb);
    vec3 outer = inner * 0.5 + 0.25 * (texture(hdr, uv - span * 0.5).rgb + texture(hdr, uv + span * 0.5).rgb);
    float filtered = EdgeLuma(outer);

    return filtered < low || filtered > high ? inner : outer;

}

// Only light far past white is bright enough for its reflections inside the lens to show.
vec3 Glare(vec2 uv) {

    vec2 inside = step(vec2(0.0), uv) * step(uv, vec2(1.0));

    return min(max(texture(bloom, uv).rgb - 8.0, vec3(0.0)), vec3(60.0)) * inside.x * inside.y;

}

// Reflections between lens elements: ghosts strung through the image centre opposite each bright source, and a faint ring around it.
vec3 LensFlare(vec2 uv) {

    vec2 across = vec2(0.5) - uv;
    vec3 flare = vec3(0.0);

    const float scales[4] = float[](0.6, 1.1, 1.7, 2.4);
    const vec3 tints[4] = vec3[](vec3(1.0, 0.7, 0.5), vec3(0.6, 0.8, 1.0), vec3(0.8, 1.0, 0.7), vec3(0.7, 0.6, 1.0));

    for (int i = 0; i < 4; i++) {

        vec2 ghost = vec2(0.5) + across * scales[i];
        float fade = 1.0 - smoothstep(0.2, 0.7, length(ghost - 0.5));

        flare += Glare(ghost) * tints[i] * fade * (0.5 / float(i + 1));

    }

    float aspect = float(textureSize(hdr, 0).x) / float(textureSize(hdr, 0).y);
    vec2 ring = normalize(across * vec2(aspect, 1.0)) * vec2(1.0 / aspect, 1.0) * 0.38;

    flare += Glare(uv + ring) * vec3(0.9, 0.8, 1.0) * 0.35;

    return flare;

}

void main() {

    vec2 centred = vUv - 0.5;
    float aspect = float(textureSize(hdr, 0).x) / float(textureSize(hdr, 0).y);
    float corner = dot(centred * vec2(aspect, 1.0), centred * vec2(aspect, 1.0)) / (0.25 * (aspect * aspect + 1.0));

    vec3 scene = ResolveEdges(vUv);

    if (pc.fringe > 0.0) {

        // A simple lens focuses red and blue at slightly different magnifications, more so towards the corners.
        vec2 spread = centred * corner * pc.fringe / vec2(textureSize(hdr, 0)) * 2.0;

        scene.r = texture(hdr, vUv + spread).r;
        scene.b = texture(hdr, vUv - spread).b;

    }

    // Energy-conserving: bloom takes its share of the image rather than adding light.
    vec3 color = mix(scene, texture(bloom, vUv).rgb, pc.bloomIntensity);

    if (pc.flare > 0.0) {

        color += LensFlare(vUv) * pc.flare;

    }

    // Light reaching the corners crosses the lens obliquely and is spread over more of the sensor.
    float falloff = 1.0 / (1.0 + corner * 0.9);

    color *= mix(1.0, falloff * falloff, pc.vignette);

    vec3 mapped;

    switch (pc.tonemapper) {

        case 0: mapped = HueKeepingAces(color); break;
        case 1: mapped = AgX(color); break;
        case 2: mapped = PbrNeutral(color); break;
        case 3: mapped = Reinhard(color, pc.whitePoint); break;
        default: mapped = clamp(color, 0.0, 1.0); break;

    }

    // The swapchain encodes sRGB, so dither in (approximately) encoded space and convert back.
    vec3 encoded = pow(clamp(mapped, 0.0, 1.0), vec3(1.0 / 2.2)) + Dither(gl_FragCoord.xy) / 255.0;

    outColor = vec4(pow(max(encoded, vec3(0.0)), vec3(2.2)), 1.0);

}
