// Wind-driven deep-water waves; unresolved ones fold into roughness so the glint survives from orbit.

// Trains down to about a metre; shorter capillary ripples come from noise, since crossing sinusoids there read as a lattice.
const int WaveCount = 16;

vec3 WaveRandom(float i) {

    return fract(sin(vec3(i * 12.9898, i * 78.233, i * 37.719)) * 43758.5453);

}

struct OceanSurface {

    vec3 normal;
    float roughness;

    // Share of the surface covered by whitecaps.
    float foam;

    // 0..1 where a steep crest stands between the eye and the light, which glows with light scattered through it.
    float crest;

    // Wind at ten metres, in m/s.
    float wind;

};

// Wind over the water at a body-fixed direction: calm between weather systems, a gale under them, with gusts a few hundred kilometres across.
float SeaWind(vec3 direction) {

    float storm = sqrt(WeatherSystems(direction).a);
    float gusts = texture(cloudNoise, direction * 19.0 + vec3(0.41, 0.17, 0.83)).r;
    float regional = texture(cloudNoise, direction * 3.1 + vec3(0.09, 0.63, 0.27)).g;

    return mix(2.0, 17.0, smoothstep(0.15, 1.05, 0.55 * storm + 0.45 * gusts + 0.35 * regional));

}

// Mean square slope of a wind sea (Cox and Munk 1954).
float SlopeVariance(float wind) {

    return 0.003 + 0.00512 * wind;

}

// Whitecap coverage of the open sea (Monahan and O'Muircheartaigh 1980).
float WhitecapCoverage(float wind) {

    return min(3.84e-6 * pow(wind, 3.41), 0.35);

}

OceanSurface Ocean(vec3 position, vec3 up, float time, float metresPerPixel) {

    OceanSurface o;

    o.wind = SeaWind(up);
    o.crest = 0.0;

    float variance = SlopeVariance(o.wind);
    float coverage = WhitecapCoverage(o.wind);

    if (metresPerPixel >= 40.0) {

        o.normal = up;
        o.roughness = pow(0.04 * 0.04 + variance, 0.25);
        o.foam = coverage;

        return o;

    }

    float sea = clamp((o.wind - 2.0) / 15.0, 0.0, 1.0);

    // Three winds a third of a turn apart share the sea; which one rules turns slowly across the globe, so swell arrives from a different quarter on each coast.
    // Every train keeps one heading and wavelength everywhere: coordinates are tens of kilometres, so the least drift in either would tear the phase.
    const vec3 winds[3] = vec3[](vec3(0.80, 0.31, 0.52), vec3(-0.45, 0.62, -0.64), vec3(-0.28, -0.74, 0.61));

    vec3 sector = texture(cloudNoise, up * 5.3 + vec3(0.77, 0.31, 0.12)).gba;
    vec3 rule = pow(max(sector - min(sector.x, min(sector.y, sector.z)) * 0.8, vec3(0.0)), vec3(3.0));

    rule = sqrt(rule / max(rule.x + rule.y + rule.z, 1e-5));

    vec3 downwind = normalize(winds[0] * rule.x + winds[1] * rule.y + winds[2] * rule.z);

    downwind = normalize(downwind - up * dot(downwind, up));

    vec3 crosswind = cross(up, downwind);

    // A fully developed sea's dominant wave grows with the square of the wind; longer trains than that have not been raised yet.
    float dominant = 10.0 + 0.9 * o.wind * o.wind;
    float wavelength = 290.0;
    float raised = 0.0;

    for (int i = 0; i < WaveCount; i++) {

        float present = 1.0 - smoothstep(dominant * 0.9, dominant * 1.6, wavelength);

        raised += present * present;
        wavelength *= 0.72;

    }

    // Four fifths of the slope variance is in the trains; ripples and what the pixel cannot show carry the rest.
    float steepness = sqrt(1.6 * variance / max(raised, 0.5));

    wavelength = 290.0;

    vec3 slope = vec3(0.0);
    float lostVariance = 0.0;
    float crests = 0.0;
    float crestScale = 0.0;

    // A slow field groups each train and bends its crests, so the crossing trains never settle into a lattice seen from above.
    mat3 sway = mat3(0.36, 0.48, -0.80, -0.80, 0.60, 0.00, 0.48, 0.64, 0.60);
    vec4 groups = texture(cloudNoise, sway * position * (1.0 / 211.0) + vec3(0.37, 0.11, 0.73));
    vec3 warped = position + (texture(cloudNoise, sway * position * (1.0 / 67.0) + vec3(0.71, 0.29, 0.53)).gba - 0.5) * 8.0;

    for (int i = 0; i < WaveCount; i++) {

        vec3 random = WaveRandom(float(i) + 1.0);
        float span = wavelength * (0.85 + 0.3 * random.x);
        float k = 6.2831853 / span;
        float omega = sqrt(9.81 * k);

        // Each train belongs to one of the three winds; long waves run with it, short ones spread up to a right angle either side.
        int owner = i % 3;
        float spread = 0.25 + 1.3 * float(i) / float(WaveCount);
        vec3 direction = normalize(winds[owner] + (random * 2.0 - 1.0) * spread);
        float share = (owner == 0 ? rule.x : owner == 1 ? rule.y : rule.z) * 1.7320508;
        float present = (1.0 - smoothstep(dominant * 0.9, dominant * 1.6, wavelength)) * share;

        // A wave needs about four pixels per wavelength to be drawn; below that only its slope variance remains.
        float resolved = 1.0 - smoothstep(0.15, 0.3, metresPerPixel / span);
        float group = owner == 0 ? groups.g : (owner == 1 ? groups.b : groups.a);
        float phase = dot(warped, direction) * k - omega * time + (random.y + groups.r * (1.0 + random.z)) * 6.2831853;
        float weight = steepness * present * (0.3 + 1.4 * group) * resolved;
        float lift = sin(phase);

        // Wind sharpens crests and flattens troughs: the slope steepens towards the top of each wave.
        slope += direction * (weight * cos(phase) * (1.0 + 0.7 * sea * lift));
        crests += weight * max(lift, 0.0) * max(lift, 0.0);
        crestScale += steepness * present;
        lostVariance += 0.5 * steepness * steepness * present * present * (1.0 - resolved);

        wavelength *= 0.72;

    }

    // Wind ripples from drifting noise break up what regularity the wave trains leave.
    float rippleResolved = 1.0 - smoothstep(0.1, 0.4, metresPerPixel);
    float rippleScale = 0.4 + 1.2 * sea;

    if (rippleResolved > 0.0) {

        vec3 drift = downwind * (time * 0.05);
        // Rotated off the noise texture's axes, at scales that share no period, so its tiling never lines up.
        mat3 turn = mat3(0.78, 0.42, -0.46, -0.31, 0.90, 0.30, 0.54, -0.11, 0.83);
        vec3 fine = texture(cloudNoise, turn * (position - drift * 7.3) * (1.0 / 7.3)).gba - 0.5;
        vec3 finer = texture(cloudNoise, (position - drift * 5.8) * (turn * vec3(1.0 / 2.9))).gba - 0.5;

        slope += (fine * 0.12 + finer * 0.05) * (rippleResolved * rippleScale);

    }

    lostVariance += 0.2 * variance * (1.0 - rippleResolved);
    slope -= up * dot(slope, up);

    o.normal = normalize(up - slope);

    // GGX alpha squared grows by the unresolved slope variance; roughness is the square root of alpha.
    o.roughness = pow(0.04 * 0.04 + lostVariance, 0.25);

    // Whitecaps break where the trains pile their crests together, then trail downwind as streaks that fade.
    float piled = crests / max(crestScale * 0.35, 1e-4);
    vec3 streakPosition = position - downwind * (time * 0.6);
    float streaks = texture(cloudNoise, (crosswind * dot(streakPosition, crosswind) * 3.0 + downwind * dot(streakPosition, downwind) * 0.5 + up * dot(streakPosition, up)) * (1.0 / 23.0)).r;
    float bubbles = texture(cloudNoise, position * (1.0 / 3.7) + vec3(time * 0.02)).a;
    // Crests average about 0.7 here and rarely pass twice that, so only the tallest few break, more of them as the wind rises.
    float threshold = mix(1.7, 1.15, coverage / 0.35);
    float breaking = smoothstep(threshold, threshold + 0.25, piled * (0.75 + 0.5 * streaks)) * (0.35 + 0.65 * smoothstep(0.3, 0.7, bubbles));
    float drawn = 1.0 - smoothstep(4.0, 30.0, metresPerPixel);

    o.foam = mix(coverage, breaking * step(3.5, o.wind), drawn);
    o.crest = clamp(piled * sea, 0.0, 1.0) * drawn;

    return o;

}

// Breaking water along the shore: depth is metres below sea level, so it shows only in the shallows.
float ShoreFoam(vec3 position, float depth, float time) {

    float band = 1.0 - smoothstep(0.0, 1.2, depth);
    float surge = 0.5 + 0.5 * sin(time * 0.9 - depth * 3.0 + dot(position, vec3(0.031, 0.017, 0.023)));

    return band * smoothstep(0.35, 0.9, surge * band);

}
