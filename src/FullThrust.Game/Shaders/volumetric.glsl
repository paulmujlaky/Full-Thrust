// Participating media shared by every volume: primitives, density by kind, and lighting. Positions are render-local metres.

const int VolumeNoiseSphere = 1;
const int VolumePlume = 2;
const int VolumeCloudLayer = 3;
const int VolumeShock = 4;
const int VolumeGroundOutflow = 5;
const int VolumeCirrus = 6;

// Mirrors VolumePrimitive in Rendering/Volumetrics/VolumePrimitive.cs.
struct VolumePrimitive {

    ivec4 kind;
    vec4 position;
    vec4 axis;
    vec4 medium;
    vec4 emission;
    vec4 params0;
    vec4 params1;
    vec4 params2;
    vec4 params3;
    ivec4 hull;

};

layout(set = 1, binding = 0) uniform sampler2D sceneDepth;
layout(set = 1, binding = 1) uniform sampler3D shapeNoise;
layout(set = 1, binding = 2) uniform sampler3D detailNoise;

layout(std430, set = 1, binding = 3) readonly buffer Volumes {

    ivec4 count;
    VolumePrimitive items[];

} volumes;

// Mirrors HullPrimitive in Rendering/Volumetrics/HullSet.cs: a capped cone from the aft centre and radius to the fore ones.
struct HullPrimitive {

    vec4 aft;
    vec4 fore;

};

layout(std430, set = 1, binding = 8) readonly buffer Hulls {

    ivec4 count;
    HullPrimitive items[128];
    int indices[512];

} hulls;

// Signed distance to a capped cone (Quilez).
float HullConeDistance(vec3 p, HullPrimitive h) {

    vec3 a = h.aft.xyz;
    vec3 b = h.fore.xyz;
    float ra = h.aft.w;
    float rb = h.fore.w;

    float rba = rb - ra;
    float baba = dot(b - a, b - a);
    float papa = dot(p - a, p - a);
    float paba = dot(p - a, b - a) / baba;
    float x = sqrt(max(papa - paba * paba * baba, 0.0));
    float cax = max(0.0, x - (paba < 0.5 ? ra : rb));
    float cay = abs(paba - 0.5) - 0.5;
    float k = rba * rba + baba;
    float f = clamp((rba * (x - ra) + paba * baba) / k, 0.0, 1.0);
    float cbx = x - ra - f * rba;
    float cby = paba - f;
    float s = (cbx < 0.0 && cay < 0.0) ? -1.0 : 1.0;

    return s * sqrt(min(cax * cax + cay * cay * baba, cbx * cbx + cby * cby * baba));

}

// Distance to the nearest hull a volume was given, and which one that is; 1e30 with none.
float HullDistance(VolumePrimitive v, vec3 p, out int nearest) {

    float best = 1e30;

    nearest = -1;

    for (int i = 0; i < v.hull.y; i++) {

        int index = hulls.indices[v.hull.x + i];
        float d = HullConeDistance(p, hulls.items[index]);

        if (d < best) {

            best = d;
            nearest = index;

        }

    }

    return best;

}

// Outward surface normal of one hull primitive, from a tetrahedron of distance taps.
vec3 HullNormal(int index, vec3 p, float epsilon) {

    HullPrimitive h = hulls.items[index];
    const vec2 k = vec2(1.0, -1.0);

    return normalize(k.xyy * HullConeDistance(p + k.xyy * epsilon, h) + k.yyx * HullConeDistance(p + k.yyx * epsilon, h) + k.yxy * HullConeDistance(p + k.yxy * epsilon, h) + k.xxx * HullConeDistance(p + k.xxx * epsilon, h));

}

struct VolumeSample {

    // Per metre.
    float extinction;
    vec3 emission;

};

// Metres covered by one volume pixel at the current sample, set while marching; noise finer than this is faded out.
float volumeFootprint = 0.0;

// The first stretch of a spherical shell along a ray, from below, inside or above it. Even a ray grazing the limb from orbit is shorter than the cap.
bool ShellSegment(VolumePrimitive v, vec3 origin, vec3 direction, out float enter, out float exit) {

    const float MaxSpan = 900000.0;

    vec3 o = origin - v.params0.xyz;
    float r = length(o);
    float inner = v.params0.w;
    float outer = v.params1.x;

    float outerNear;
    float outerFar;
    float innerNear;
    float innerFar;

    enter = 0.0;
    exit = 0.0;

    if (!RaySphere(o, direction, outer, outerNear, outerFar) || outerFar <= 0.0) {

        return false;

    }

    bool hitsInner = RaySphere(o, direction, inner, innerNear, innerFar);

    if (r < inner) {

        enter = innerFar;
        exit = outerFar;

    } else {

        enter = r < outer ? 0.0 : outerNear;
        exit = hitsInner && innerNear > 0.0 ? innerNear : outerFar;

    }

    exit = min(exit, enter + MaxSpan);

    return exit > enter;

}

bool PlumeSegment(VolumePrimitive v, vec3 origin, vec3 direction, out float enter, out float exit) {

    vec3 offset = origin - v.params0.xyz;
    float h = dot(offset, v.axis.xyz);
    float dh = dot(direction, v.axis.xyz);
    enter = 0.0;
    exit = 1e30;

    if (abs(dh) > 1e-6) {

        float nearCap = -h / dh;
        float farCap = (v.axis.w - h) / dh;
        enter = max(enter, min(nearCap, farCap));
        exit = max(nearCap, farCap);

    } else if (h < 0.0 || h > v.axis.w) {

        return false;

    }

    if (exit <= enter) {

        return false;

    }

    // Outside two Gaussian radii the jet contributes less than a ten-thousandth of its centre.
    float spread = 2.0 * (v.params1.x + 0.02);
    float radius = 2.0 * v.params0.w + spread * h;
    vec3 radial = offset - v.axis.xyz * h;
    vec3 ray = direction - v.axis.xyz * dh;
    float growth = spread * dh;
    float a = dot(ray, ray) - growth * growth;
    float b = dot(ray, radial) - growth * radius;
    float c = dot(radial, radial) - radius * radius;

    if (abs(a) < 1e-6) {

        if (abs(b) < 1e-6) {

            return c <= 0.0;

        }

        float hit = -0.5 * c / b;
        if (b > 0.0) {

            exit = min(exit, hit);

        } else {

            enter = max(enter, hit);

        }

        return exit > enter;

    }

    float discriminant = b * b - a * c;
    if (discriminant <= 0.0) {

        return a < 0.0;

    }

    float root = sqrt(discriminant);
    float first = (-b - root) / a;
    float second = (-b + root) / a;

    if (a > 0.0) {

        enter = max(enter, first);
        exit = min(exit, second);

    } else if (dh > 0.0) {

        enter = max(enter, first);

    } else {

        exit = min(exit, second);

    }

    return exit > enter;

}

// The wall jet of a plume striking a surface: a slab over the surface, out to position.w from the strike point.
bool WallSegment(VolumePrimitive v, vec3 origin, vec3 direction, out float enter, out float exit) {

    vec3 normal = v.params3.xyz;
    vec3 strike = v.params0.xyz + v.axis.xyz * v.params3.w;
    float strikeRadius = v.params0.w + v.params3.w * (v.params1.x + 0.02);
    float runout = v.position.w;
    float thickness = (strikeRadius * 0.45 + runout * 0.1) * 1.6 + strikeRadius * 0.3;

    float near;
    float far;

    enter = 0.0;
    exit = 0.0;

    if (!RaySphere(origin - strike, direction, runout, near, far) || far <= 0.0) {

        return false;

    }

    float height = dot(origin - strike, normal);
    float rate = dot(direction, normal);
    float low = -1e30;
    float high = 1e30;

    if (abs(rate) > 1e-6) {

        float a = (-thickness * 0.1 - height) / rate;
        float b = (thickness - height) / rate;

        low = min(a, b);
        high = max(a, b);

    } else if (height < -thickness * 0.1 || height > thickness) {

        return false;

    }

    enter = max(max(near, low), 0.0);
    exit = min(far, high);

    return exit > enter;

}

bool VolumeSegment(VolumePrimitive v, vec3 origin, vec3 direction, out float enter, out float exit) {

    float near;
    float far;

    if (v.kind.x == VolumePlume) {

        bool jet = PlumeSegment(v, origin, direction, enter, exit);

        if (v.params3.w <= 0.0) {

            return jet;

        }

        // A plume spreading over a surface also fills a thin slab around the strike point.
        float wallEnter;
        float wallExit;

        if (WallSegment(v, origin, direction, wallEnter, wallExit)) {

            enter = jet ? min(enter, wallEnter) : wallEnter;
            exit = jet ? max(exit, wallExit) : wallExit;

            return true;

        }

        return jet;

    }

    if (v.kind.x == VolumeCloudLayer || v.kind.x == VolumeCirrus) {

        return ShellSegment(v, origin, direction, enter, exit);

    }

    if (!RaySphere(origin - v.position.xyz, direction, v.position.w, near, far) || far <= 0.0) {

        enter = 0.0;
        exit = 0.0;

        return false;

    }

    enter = max(near, 0.0);
    exit = far;

    return true;

}

// A noisy ball of smoke: params0.x noise scale in metres.
VolumeSample NoiseSphere(VolumePrimitive v, vec3 p) {

    vec3 local = p - v.position.xyz;
    float radial = length(local) / v.position.w;

    vec4 shape = texture(shapeNoise, local / v.params0.x + v.params0.yzw);
    float erosion = texture(detailNoise, local / (v.params0.x * 0.25)).r;

    float base = (1.0 - smoothstep(0.4, 1.0, radial)) * shape.r;
    float density = clamp(Remap(base, 0.25 + erosion * 0.2, 1.0, 0.0, 1.0), 0.0, 1.0);

    if (v.kind.w == 1) {

        float lobe = smoothstep(0.30, 0.72, shape.g);
        float shell = radial + 0.44 * (0.5 - lobe) + 0.19 * (erosion - 0.5);
        float entrained = smoothstep(0.18, 0.68, shape.r * (0.48 + 0.67 * lobe) - 0.16 * erosion);
        density = (1.0 - smoothstep(0.32, 0.89, shell)) * entrained;

    }

    VolumeSample s;

    s.extinction = density * v.medium.w;
    s.emission = v.emission.rgb * density;

    return s;

}

// Rocket exhaust: params0 nozzle exit and radius, axis direction and length, params1 spread, diamond spacing and strength.
VolumeSample Plume(VolumePrimitive v, vec3 p) {

    VolumeSample s;

    s.extinction = 0.0;
    s.emission = vec3(0.0);

    vec3 offset = p - v.params0.xyz;
    float exitRadius = v.params0.w;
    float x = dot(offset, v.axis.xyz);
    float span = v.axis.w;
    float growth = v.params1.x + 0.02;

    vec3 transverse = offset - v.axis.xyz * x;
    float r = length(transverse);

    // Where the jet strikes a surface it turns into a wall jet: distance along the surface continues the jet, height above it takes the place of radius.
    float impact = v.params3.w;

    if (impact > 0.0) {

        vec3 normal = v.params3.xyz;
        vec3 strike = p - (v.params0.xyz + v.axis.xyz * impact);
        float height = dot(strike, normal);
        float strikeRadius = exitRadius + impact * growth;

        if (height < -0.1 * strikeRadius) {

            return s;

        }

        float reach = length(strike - normal * height);
        float wallX = impact + max(reach - strikeRadius * 0.3, 0.0);
        float wallThickness = strikeRadius * 0.45 + reach * 0.1;
        float wallR = max(height, 0.0) / wallThickness * (exitRadius + wallX * growth);
        float turn = 1.0 - smoothstep(0.0, strikeRadius * 1.5, dot(strike, -v.axis.xyz));

        x = mix(x, wallX, turn);
        r = mix(r, wallR, turn);

    }

    if (x < 0.0 || x > span) {

        return s;

    }

    // Gas stops at hulls: the stage below, a neighbouring craft.
    if (v.hull.y > 0) {

        int nearest;

        if (HullDistance(v, p, nearest) < 0.0) {

            return s;

        }

    }

    float radius = exitRadius + x * growth;
    float dilution = exitRadius * exitRadius / (radius * radius);
    float spacing = max(v.params1.y, 1e-3);

    // In dense air neighbouring bells merge into one column within a few exit radii; in thin air they stay apart further.
    float merged = exitRadius * mix(4.0, 12.0, v.params1.w);

    if (v.kind.w != 0 && x < merged) {

        float ring = length(v.position.xyz);
        vec3 u = v.position.xyz / ring;
        vec3 w = cross(v.axis.xyz, u);
        float pitch = 2.0 * PI / float(abs(v.kind.w));
        float theta = atan(dot(transverse, w), dot(transverse, u));
        float angle = floor(theta / pitch + 0.5) * pitch;
        float bellDistance = length(transverse - ring * (u * cos(angle) + w * sin(angle)));
        float separate = 1.0 - smoothstep(exitRadius * 0.5, merged, x);
        float bellRadius = exitRadius - ring;
        r = mix(r, v.kind.w > 0 ? min(r, bellDistance) : bellDistance, separate);
        radius = mix(radius, bellRadius + x * (v.params1.x + 0.055), separate);
        spacing *= mix(1.0, bellRadius / exitRadius, separate);

    }

    // Eddies stretch along the jet; isotropic cells read as a string of beads.
    vec3 flow = transverse / (exitRadius * 3.0) + v.axis.xyz * (x / (exitRadius * 14.0) - frame.exposure.w * 6.0);
    vec4 eddy = texture(shapeNoise, flow);
    float mixing = smoothstep(0.0, span * 0.35, x);
    float edge = r / radius + mixing * (eddy.r - 0.5) * 1.35;
    float profile = exp(-2.5 * edge * edge);
    float along = (1.0 - x / span) * (1.0 - x / span) * smoothstep(0.0, exitRadius * 0.3, x);

    // Turbulence streams downstream with the flow and grows with distance from the nozzle.
    float turbulence = mix(1.0, eddy.g * 1.8, mixing);

    float density = profile * dilution * along * turbulence;

    // Mach disks: a compact knot on the axis at the end of each shock cell, gone within a few cells as the jet mixes out.
    float cell = x / spacing;
    float knot = (fract(cell + 0.5) - 0.5) * spacing / max(0.35 * exitRadius, 1e-3);
    float disk = exp(-knot * knot - pow(r / max(0.45 * radius, 1e-4), 2.0));
    float diamonds = v.params1.z * 0.45 * disk * exp(-cell / 1.6) * smoothstep(0.3, 0.7, cell) * along;

    // A white-hot core near the nozzle inside a cooler sheath; params2 holds the sheath colour and core whiteness.
    vec3 white = vec3(dot(v.emission.rgb, vec3(0.2126, 0.7152, 0.0722)));
    float core = exp(-4.0 * (r * r) / (radius * radius)) * exp(-x / (span * 0.3));
    vec3 tint = mix(vec3(1.0), v.params2.rgb, smoothstep(0.3, 1.0, r / radius));
    vec3 hot = mix(v.emission.rgb * tint, white, core * v.params2.w) * (0.3 + 0.7 * exp(-x / (span * 0.25)));

    s.extinction = density * v.medium.w;
    // Afterburning keeps the exhaust glowing as it spreads, so emission thins more slowly than mass.
    float cooling = pow(dilution, mix(0.5, 1.13, v.params1.w));
    s.emission = hot * profile * cooling * along * turbulence + mix(v.emission.rgb, white, 0.6) * diamonds * 0.85;

    // In thin air the flow fans out widely, but its hot inviscid core stays narrow for many exit diameters; the fan is left as a faint halo.
    float thin = v.params1.w;

    if (thin > 0.0) {

        float coreRadius = exitRadius + x * 0.07;
        float coreProfile = exp(-2.5 * (r * r) / (coreRadius * coreRadius));
        float coreDilution = exitRadius * exitRadius / (coreRadius * coreRadius);
        float coreAlong = exp(-x / (exitRadius * 18.0)) * smoothstep(0.0, exitRadius * 0.3, x);
        vec3 coreGlow = mix(v.emission.rgb * mix(vec3(1.0), v.params2.rgb, smoothstep(0.2, 1.0, r / coreRadius)), white, v.params2.w * exp(-x / (exitRadius * 6.0)));

        s.emission = mix(s.emission, s.emission * 0.35 + coreGlow * coreProfile * sqrt(coreDilution) * coreAlong * turbulence, thin);

    }

    return s;

}

// Cumulus after Schneider's Nubis: a body-fixed weather map sets coverage and height, Perlin-Worley billows, Worley wisps.
VolumeSample CloudLayer(VolumePrimitive v, vec3 p) {

    VolumeSample s;

    s.extinction = 0.0;
    s.emission = vec3(0.0);

    vec3 relative = p - v.params0.xyz;
    float r = length(relative);
    float h = (r - v.params0.w) / (v.params1.x - v.params0.w);

    if (h <= 0.0 || h >= 1.0) {

        return s;

    }

    vec3 body = QuaternionRotate(v.axis, relative);
    float weather = CloudWeather(shapeNoise, body, v.params1.z, v.params1.w, v.params1.y);

    if (weather <= 0.0) {

        return s;

    }

    // Coarse towers: their height gives the tops relief that a low sun picks out, even from orbit.
    vec3 cell = body * v.params2.z;
    vec3 driftField = texture(shapeNoise, cell.zxy * 0.0193 + vec3(0.13, 0.37, 0.71)).gba - 0.5;
    vec4 coarse = texture(shapeNoise, cell * 0.125 + 0.37 + 0.83 * driftField);
    float towers = smoothstep(0.32, 0.80, coarse.r);

    // Flat bases and rounded tops that rise where the weather is densest.
    float top = (0.35 + 0.65 * weather) * (0.6 + 0.4 * towers);
    float base = 0.10 * smoothstep(0.2, 0.8, coarse.b) * (1.0 - 0.6 * weather);
    float gradient = smoothstep(base, base + 0.08, h) * clamp(Remap(h, top * 0.6, top, 1.0, 0.0), 0.0, 1.0);

    if (gradient <= 0.0) {

        return s;

    }

    // Far away the billows fade to a value that keeps about the same area covered.
    float shapeWeight = 1.0 - smoothstep(0.1, 0.4, volumeFootprint * v.params2.z);
    float detailWeight = 1.0 - smoothstep(0.1, 0.4, volumeFootprint * v.params2.w);
    float billows = 0.55 + 0.45 * towers;

    if (shapeWeight > 0.0) {

        vec4 shape = texture(shapeNoise, cell + 1.8 * driftField + 0.65 * (coarse.gba - 0.5) + vec3(v.params2.x, 0.0, 0.0));
        float fbm = shape.g * 0.625 + shape.b * 0.25 + shape.a * 0.125;

        // The billows tile every shape period, so in thin weather their few peaks line up; cells of the coarse field, a period across, decide which rise.
        float rise = 0.85 + 0.3 * coarse.b;

        billows = mix(billows, clamp(Remap(shape.r * rise, fbm - 1.0, 1.0, 0.0, 1.0), 0.0, 1.0), shapeWeight);

    }

    float density = clamp(Remap(billows * gradient, 1.0 - weather, 1.0, 0.0, 1.0), 0.0, 1.0);
    density *= mix(0.18, 1.0, smoothstep(0.18, 0.80, coarse.g)) * sqrt(weather);

    if (density <= 0.0 || detailWeight <= 0.0) {

        float extinction = density * v.medium.w;
        // A distant pixel sees clear gaps between opaque billows; average their transmission.
        float path = clamp(volumeFootprint, 1.0, 1300.0);
        float fill = 0.32 + 0.63 * smoothstep(0.02, 0.54, density);
        float filtered = -log(max(0.0001, mix(1.0, exp(-extinction * path), fill))) / path;
        s.extinction = mix(extinction, filtered, smoothstep(240.0, 1400.0, volumeFootprint));

        return s;

    }

    // Wispy at the base, billowy towards the top.
    vec4 detail = texture(detailNoise, body.zxy * v.params2.w + 2.3 * coarse.bag + vec3(v.params2.y, 0.0, 0.0));
    float wisps = detail.r * 0.625 + detail.g * 0.25 + detail.b * 0.125;

    wisps = mix(wisps, 1.0 - wisps, clamp(h * 4.0, 0.0, 1.0));
    density = clamp(Remap(density, wisps * 0.3 * detailWeight, 1.0, 0.0, 1.0), 0.0, 1.0);

    s.extinction = density * v.medium.w;

    return s;

}

// How far the next shock sample can move without stepping over the glowing sheet, set by Shock.
float shockStep = 1e30;

// Shock layer around the hulls the volume was given: a glowing sheet standing off every windward face by a sliver of the nose radius (params0.w), streaming down axis.xyz.
VolumeSample Shock(VolumePrimitive v, vec3 p) {

    VolumeSample s;

    s.extinction = 0.0;
    s.emission = vec3(0.0);

    float nose = v.params0.w;
    int nearest;
    float d = HullDistance(v, p, nearest);

    shockStep = 1e30;

    if (nearest < 0) {

        return s;

    }

    if (d < 0.0) {

        shockStep = max(-d, nose * 0.02);

        return s;

    }

    vec3 downstream = v.axis.xyz;
    vec3 normal = HullNormal(nearest, p, max(nose * 0.01, 0.005));
    float facing = dot(normal, -downstream);

    // Behind the shoulders the layer peels away from the hull and widens downstream.
    float trailing = max(dot(p - v.position.xyz, downstream) + nose, 0.0);
    float lee = clamp(-facing * 2.0 + 0.4, 0.0, 1.0);
    float standoff = nose * (0.08 + 0.12 * (1.0 - clamp(facing, 0.0, 1.0))) + lee * trailing * 0.22;
    float thickness = nose * 0.03 + standoff * 0.18;

    shockStep = max(abs(d - standoff) - 2.0 * thickness, 0.0) * 0.8 + thickness * 0.5;

    if (d > standoff + 3.0 * thickness) {

        return s;

    }

    // Newtonian heating goes with the square of the flow normal to the face, so a flat shield glows evenly and the flanks barely do.
    float windward = clamp(facing, 0.0, 1.0);
    float heat = windward * windward + 0.015 * exp(-trailing / (nose * 0.8));
    float sheet = exp(-pow((d - standoff) / thickness, 2.0));
    float layer = smoothstep(0.0, standoff * 0.4, d) * (1.0 - smoothstep(standoff, standoff + thickness, d));

    vec3 q = (p - v.position.xyz) / (nose * 1.4) - downstream * (frame.exposure.w * 2.0 + trailing / (nose * 3.0));
    float streaks = texture(shapeNoise, q + 0.37).r;

    float glow = (sheet + 0.6 * layer) * heat * (0.35 + streaks) * (1.0 - smoothstep(v.axis.w * 0.5, v.axis.w, trailing));

    s.emission = v.emission.rgb * mix(vec3(1.0, 0.38, 0.16), vec3(1.0), windward) * glow;
    s.extinction = v.medium.w * glow;

    return s;

}

VolumeSample SampleVolume(VolumePrimitive v, vec3 p) {

    if (v.kind.x == VolumeCirrus) {

        vec3 body = QuaternionRotate(v.axis, p - v.params0.xyz);
        vec3 cell = body * v.params1.z;
        vec4 warp = texture(shapeNoise, cell * 0.036);
        vec3 streak = cell * vec3(1.7, 7.0, 9.0) + 2.5 * warp.rgb + vec3(v.params1.w, 0.0, 0.0);
        vec4 systems = WeatherSystems(body);
        float weather = sqrt(systems.a) * smoothstep(0.32, 0.68, warp.g) * v.params1.y;
        float fibres = texture(shapeNoise, streak).r;
        float fine = texture(detailNoise, streak * 2.1).g;
        float density = weather * smoothstep(0.31, 0.78, fibres * 0.78 + fine * 0.22);

        return VolumeSample(v.medium.w * density, vec3(0.0));

    }

    if (v.kind.x == VolumeShock) {

        return Shock(v, p);

    }

    if (v.kind.x == VolumeCloudLayer) {

        return CloudLayer(v, p);

    }

    if (v.kind.x == VolumeNoiseSphere) {

        return NoiseSphere(v, p);

    }

    if (v.kind.x == VolumeGroundOutflow) {

        vec3 local = (p - v.position.xyz) / v.position.w;
        float height = dot(local, v.axis.xyz);
        vec3 lateral = local - height * v.axis.xyz;
        float reach = length(lateral);
        float time = frame.exposure.w;
        vec3 outward = lateral / max(reach, 0.05);
        // Advect through a bounded phase so the billows keep their spatial frequency.
        vec3 noiseUv = local * 1.9 - outward * 0.16 * sin(time * 1.5 - reach * 9.0) - v.axis.xyz * time * 0.11;
        vec4 lobes = texture(shapeNoise, noiseUv);
        float wisps = texture(detailNoise, noiseUv * 3.4).r;
        float swell = smoothstep(0.28, 0.78, lobes.r * 0.80 + 0.20 * wisps);
        float top = (0.045 + 0.36 * swell) * smoothstep(0.0, 0.4, reach);
        float edge = 1.0 - smoothstep(0.44 + 0.24 * lobes.b, 0.73 + 0.22 * lobes.g, reach);
        float layer = (1.0 - smoothstep(top * 0.18, max(top, 0.01), height)) * step(-0.012, height);

        return VolumeSample(v.medium.w * edge * layer * (0.15 + 0.85 * swell), vec3(0.0));

    }

    if (v.kind.x == VolumePlume) {

        return Plume(v, p);

    }

    VolumeSample empty;

    empty.extinction = 0.0;
    empty.emission = vec3(0.0);

    return empty;

}

float HenyeyGreenstein(float cosTheta, float g) {

    float g2 = g * g;

    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4), 1.5));

}

// Optical depth towards the sun through the same primitive, for self-shadowing.
float SunOpticalDepth(VolumePrimitive v, vec3 p, vec3 sunDirection) {

    int steps = v.kind.z;
    float reach = v.kind.x == VolumeCloudLayer ? v.params1.x - v.params0.w : v.position.w * 2.0;
    float dt = reach / float(max(steps, 1));
    float depth = 0.0;

    // Self-shadow is shaped by the billows, not the wisps, so the detail noise is skipped here.
    float footprint = volumeFootprint;

    volumeFootprint = max(volumeFootprint, 250.0);

    for (int i = 0; i < steps; i++) {

        depth += SampleVolume(v, p + sunDirection * (float(i) + 0.5) * dt).extinction * dt;

    }

    volumeFootprint = footprint;

    return depth;

}

// Smoke between a sample and a light inside or beside it, so flame-lit billows shade their far sides.
float LightOpticalDepth(VolumePrimitive v, vec3 p, vec3 toLight) {

    if (v.kind.x != VolumeNoiseSphere && v.kind.x != VolumeGroundOutflow) {

        return 0.0;

    }

    const int Steps = 3;

    float distance = sqrt(max(dot(toLight, toLight), 1e-6));
    vec3 direction = toLight / distance;
    float dt = min(distance, v.position.w * 2.0) / float(Steps);
    float depth = 0.0;

    for (int i = 0; i < Steps; i++) {

        depth += SampleVolume(v, p + direction * (float(i) + 0.5) * dt).extinction * dt;

    }

    return depth;

}

struct VolumeResult {

    vec3 luminance;
    float transmittance;

    // Emission also carries a depth when the gas is optically thin.
    float depth;
    float depthWeight;

};

// Sunlight blocked by the opaque scene: a craft shading its own exhaust, the tower shading the steam.
float VolumeSunShadow(vec3 p) {

    int count = int(frame.shadowParams.x);

    if (count == 0) {

        return 1.0;

    }

    int cascade = ShadowCascade(ViewDepth(p));

    if (cascade >= count) {

        return 1.0;

    }

    vec4 clip = frame.shadowViewProjection[cascade] * vec4(p, 1.0);
    vec2 uv = clip.xy * 0.5 + 0.5;

    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))) || clip.z > 1.0) {

        return 1.0;

    }

    return texture(shadowMap, vec4(uv, float(cascade), clip.z));

}

// The shock sheet is thinner than a fixed step; steps shrink near it and stretch across the empty gap around it.
void MarchShock(VolumePrimitive v, vec3 origin, vec3 direction, float enter, float exit, float jitter, inout VolumeResult result) {

    float largest = (exit - enter) / 12.0;
    float smallest = (exit - enter) / float(max(v.kind.y, 1) * 4);
    float t = enter;
    float carry = jitter;

    for (int i = 0; i < v.kind.y && t < exit; i++) {

        vec3 p = origin + direction * t;
        VolumeSample s = Shock(v, p);
        float dt = clamp(shockStep, smallest, largest);

        // The first step lands at a jittered point so frames fill in between each other.
        dt *= i == 0 ? max(carry, 0.05) : 1.0;
        dt = min(dt, exit - t);

        vec3 light = result.transmittance * s.emission * dt;
        float weight = min(dot(light, vec3(0.2126, 0.7152, 0.0722)) * frame.exposure.x, result.transmittance);

        result.luminance += light;
        result.depth += weight * t;
        result.depthWeight += weight;
        t += dt;

    }

}

// Marches one primitive between enter and exit, adding to the running result. Luminance is before exposure.
void MarchVolume(VolumePrimitive v, vec3 origin, vec3 direction, float enter, float exit, float jitter, inout VolumeResult result) {

    if (v.kind.x == VolumeShock) {

        MarchShock(v, origin, direction, enter, exit, jitter, result);

        return;

    }

    float pixelAngle = 4.0 / (abs(frame.projection[1][1]) * frame.viewport.y);

    // Clouds step no finer than they need: 60 m up close, about a pixel's footprint further away.
    float stepLength = max(60.0, enter * pixelAngle * 0.35);
    int steps = v.kind.x == VolumeCloudLayer ? clamp(int((exit - enter) / stepLength), 24, v.kind.y) : max(v.kind.y, 1);
    float stepSpan = (exit - enter) / float(steps);
    float growth = 1.0;

    if (v.kind.x == VolumePlume && v.params3.w <= 0.0) {

        float startHeight = max(0.0, dot(origin - v.params0.xyz + direction * enter, v.axis.xyz));
        float endHeight = max(0.0, dot(origin - v.params0.xyz + direction * exit, v.axis.xyz));
        float spread = v.params1.x + 0.02;
        float ratio = (v.params0.w + spread * endHeight) / (v.params0.w + spread * startHeight);
        if (abs(ratio - 1.0) > 0.06) {

            // Short steps resolve the hot throat even when the same ray crosses a long vacuum fan.
            growth = pow(ratio, 1.0 / float(steps));
            stepSpan = (exit - enter) * (growth - 1.0) / (ratio - 1.0);

        }

    }

    if (v.kind.x == VolumeCloudLayer || v.kind.x == VolumeCirrus) {

        float crowding = (exit - enter) / (stepLength * float(steps));

        if (crowding > 1.0) {

            // More layer than steps: they lengthen with distance, as a pixel's footprint does, so the near cloud keeps its detail and the far cloud is still reached.
            // The last step is this many times the first: the root of ratio - 1 = crowding * log(ratio).
            float ratio = max(crowding, 1.02);

            for (int i = 0; i < 5; i++) {

                ratio = 1.0 + crowding * log(ratio);

            }

            ratio = max(ratio, 1.02);
            growth = pow(ratio, 1.0 / float(steps));
            stepSpan = (exit - enter) * (growth - 1.0) / (ratio - 1.0);

        }

    }

    float stepStart = enter;
    vec3 sunDirection = frame.sunDirection.xyz;
    float cosTheta = dot(direction, sunDirection);
    float g = v.emission.w;
    vec3 albedo = v.medium.rgb;
    float shadow = 0.0;
    float shadowAt = -1e30;

    // Sun and sky light barely change across a plume or a puff, so small volumes look them up once.
    bool local = v.kind.x != VolumeCloudLayer && v.kind.x != VolumeCirrus;
    bool cached = false;
    vec3 sunCache = vec3(0.0);
    vec3 skyCache = vec3(0.0);

    for (int i = 0; i < steps; i++) {

        float dt = stepSpan;
        float t = stepStart + jitter * dt;
        stepStart += dt;
        stepSpan *= growth;

        // Detail finer than the step is as unresolved as detail finer than the pixel.
        volumeFootprint = max(t * pixelAngle, growth > 1.0 && v.kind.x != VolumePlume ? dt * 0.4 : 0.0);
        vec3 p = origin + direction * t;
        VolumeSample s = SampleVolume(v, p);

        if (s.extinction <= 1e-6 && dot(s.emission, s.emission) <= 0.0) {

            continue;

        }

        if (s.extinction <= 1e-6) {

            vec3 light = result.transmittance * s.emission * dt;
            float weight = min(dot(light, vec3(0.2126, 0.7152, 0.0722)) * frame.exposure.x, result.transmittance);
            result.luminance += light;
            result.depth += weight * t;
            result.depthWeight += weight;

            continue;

        }

        if (!cached) {

            vec3 up = normalize(PlanetRelative(p));

            sunCache = SunIlluminanceAt(p);
            skyCache = (AmbientIrradiance(p, up) + AmbientIrradiance(p, -up)) / (4.0 * PI) * EclipseSkylight(p);
            cached = local;

        }

        vec3 sun = local ? sunCache * VolumeSunShadow(p) : sunCache;
        vec3 ambient = skyCache;

        // Shadow variation below a volume pixel is unresolved; nearby march steps can share it.
        if (v.kind.x != VolumeCloudLayer || t - shadowAt >= min(volumeFootprint, 650.0)) {

            shadow = SunOpticalDepth(v, p, sunDirection);
            shadowAt = t;

        }

        // Multiple scattering as fading octaves of the single-scattering term (Wrenninge 2013).
        vec3 scattered = vec3(0.0);
        float a = 1.0;
        float b = 1.0;
        float c = 1.0;

        for (int octave = 0; octave < 4; octave++) {

            // Beer-Powder (Schneider 2015) on the first octave: thin, sun-facing edges darken, sculpting the billows.
            float attenuation = exp(-shadow * b);
            float powder = octave == 0 ? mix(1.0, 2.0 * (1.0 - exp(-2.0 * shadow)), 0.5) : 1.0;

            scattered += a * sun * attenuation * powder * mix(HenyeyGreenstein(cosTheta, g * c), HenyeyGreenstein(cosTheta, -0.3 * c), 0.25);
            a *= 0.7;
            b *= 0.3;
            c *= 0.4;

        }

        // Local extinction keeps lit smoke from glowing equally on both sides.
        for (int light = 0; light < int(frame.lightCount.x); light++) {

            vec3 toLight = frame.lightPositions[light].xyz - p;
            float distanceSquared = dot(toLight, toLight) + frame.lightPositions[light].w * frame.lightPositions[light].w;

            float beam = BeamWeight(light, toLight * inversesqrt(max(dot(toLight, toLight), 1e-6)));
            float visibility = exp(-LightOpticalDepth(v, p, toLight));
            ambient += frame.lightIntensities[light].rgb * beam * visibility / distanceSquared / (4.0 * PI);

        }

        vec3 source = albedo * s.extinction * (scattered + ambient) + s.emission;
        float stepTransmittance = exp(-s.extinction * dt);

        // Light added across the step; in a nearly clear glowing gas it tends to the step length rather than vanishing.
        float span = s.extinction * dt > 1e-4 ? (1.0 - stepTransmittance) / s.extinction : dt;

        result.luminance += result.transmittance * source * span;
        float emitted = dot(s.emission, vec3(0.2126, 0.7152, 0.0722)) * span * frame.exposure.x;
        float weight = result.transmittance * max(1.0 - stepTransmittance, min(emitted, 1.0));
        result.depth += weight * t;
        result.depthWeight += weight;
        result.transmittance *= stepTransmittance;

        if (result.transmittance < 0.005) {

            break;

        }

    }

}
