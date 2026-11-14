using Brutal.Numerics;

namespace FullThrust.Core.Terrain;

// Deterministic double-precision continents or cratered maria; callers trade detail for speed via the resolution.
public sealed class ProceduralTerrain : ITerrain {

    private const int MaxDetailOctaves = 28;

    private static readonly doubleQuat MareLattice = doubleQuat.CreateFromAxisAngle(double3.Normalize(new double3(0.36, 0.8, 0.48)), 0.97);

    private readonly TerrainData _data;
    private readonly double _radius;
    private readonly GradientNoise _noise;
    private readonly bool _cratered;
    private readonly TerrainSite[] _sites;

    public ProceduralTerrain(TerrainData data, double radius) {

        _data = data;
        _radius = radius;
        _noise = new GradientNoise(data.Seed);
        _cratered = string.Equals(data.Type, "cratered", StringComparison.OrdinalIgnoreCase);

        if (!_cratered && !string.Equals(data.Type, "continental", StringComparison.OrdinalIgnoreCase)) {

            throw new InvalidDataException($"Unknown terrain type '{data.Type}'.");

        }

        HasOcean = data.Ocean;

        if (_cratered) {

            MinHeight = -(data.MareDepth + 0.12 * data.LargestCrater + 3 * data.DetailAmplitude);
            MaxHeight = 0.06 * data.LargestCrater + 3 * data.DetailAmplitude;

        } else {

            MinHeight = -(data.OceanDepth + 3 * data.DetailAmplitude);
            MaxHeight = data.MountainHeight + 800 + 3 * data.DetailAmplitude;

        }

        _sites = new TerrainSite[data.Sites.Count];

        for (var i = 0; i < _sites.Length; i++) {

            var site = data.Sites[i];
            var latitude = site.LatitudeDeg * Math.PI / 180;
            var longitude = site.LongitudeDeg * Math.PI / 180;
            var direction = Direction(latitude, longitude);
            var height = Raw(direction, 0.5).Height;

            if (HasOcean) {

                height = Math.Max(height, site.MinimumHeight);

            }

            _sites[i] = new TerrainSite(site.Name, latitude, longitude, direction, height, site.Radius, site.Blend);

        }

    }

    public bool HasOcean { get; }

    public double MinHeight { get; }

    public double MaxHeight { get; }

    public double Radius => _radius;

    public IReadOnlyList<TerrainSite> Sites => _sites;

    // Matches CelestialSystem.BodyFixedPoint.
    public static double3 Direction(double latitude, double longitude) {

        var cosLatitude = Math.Cos(latitude);

        return new double3(cosLatitude * Math.Cos(longitude), Math.Sin(latitude), -cosLatitude * Math.Sin(longitude));

    }

    public double Height(double3 direction, double resolution = 1.0) => Sample(direction, resolution).Height;

    public TerrainSample Sample(double3 direction, double resolution) {

        var sample = Raw(direction, resolution);

        for (var i = 0; i < _sites.Length; i++) {

            ref readonly var site = ref _sites[i];

            var distance = (direction - site.Direction).Length() * _radius;

            if (distance >= site.Radius + site.Blend) {

                continue;

            }

            var t = 1 - SmoothStep(site.Radius, site.Radius + site.Blend, distance);

            sample.Height += (site.Height - sample.Height) * t;
            sample.Feature *= (float)(1 - t);

        }

        return sample;

    }

    private TerrainSample Raw(double3 direction, double resolution) => _cratered ? Cratered(direction, resolution) : Continental(direction, resolution);

    private TerrainSample Continental(double3 d, double resolution) {

        var f = _data.ContinentFrequency;
        var p = d * f;

        var wx = _noise.Noise(p.X * 1.7 + 3.1, p.Y * 1.7 + 7.2, p.Z * 1.7 + 1.3);
        var wy = _noise.Noise(p.X * 1.7 + 9.4, p.Y * 1.7 + 2.8, p.Z * 1.7 + 5.5);
        var wz = _noise.Noise(p.X * 1.7 + 4.6, p.Y * 1.7 + 8.1, p.Z * 1.7 + 6.9);

        var continent = _noise.Fbm(p.X + 0.45 * wx, p.Y + 0.45 * wy, p.Z + 0.45 * wz, 6) - 0.06;

        var land = SmoothStep(-0.012, 0.05, continent);
        var shelf = -120 - (_data.OceanDepth - 120) * SmoothStep(0.0, 0.16, -continent);
        var inland = SmoothStep(0.0, 0.3, continent);

        var belts = _noise.Fbm(p.X * 2.3 + 41.0, p.Y * 2.3 + 13.0, p.Z * 2.3 + 77.0, 3);
        var mountainMask = SmoothStep(0.12, 0.4, belts) * land;
        var ridges = mountainMask > 0 ? _noise.Ridged(p.X * 16, p.Y * 16, p.Z * 16, 7) : 0;
        var mountains = _data.MountainHeight * mountainMask * Math.Pow(ridges, 1.6);

        var lowland = 15 + 650 * inland;
        var height = shelf + (lowland + mountains - shelf) * land;

        height += Detail(d, resolution, 0.6 + 2.5 * mountainMask) * (0.3 + 0.7 * land);

        // Climate sets the broad belts; drainage and soil break them into patches a few kilometres across, so no plain is one colour.
        var climate = _noise.Fbm(p.X * 3.1 + 101.0, p.Y * 3.1 + 59.0, p.Z * 3.1 + 23.0, 4) * 1.4 + 0.5;
        var local = _noise.Fbm(p.X * 160.0 + 13.0, p.Y * 160.0 + 71.0, p.Z * 160.0 + 37.0, 4);
        var patches = _noise.Fbm(p.X * 1300.0 + 5.0, p.Y * 1300.0 + 43.0, p.Z * 1300.0 + 89.0, 3);
        var moisture = climate + 0.45 * local + 0.7 * patches;

        return new TerrainSample { Height = height, Moisture = (float)Math.Clamp(moisture, 0, 1), Feature = (float)(mountainMask * ridges) };

    }

    private TerrainSample Cratered(double3 d, double resolution) {

        var f = _data.ContinentFrequency;
        var p = d * f;

        var wx = _noise.Noise(p.X * 2.1 + 5.0, p.Y * 2.1 + 1.0, p.Z * 2.1 + 9.0);
        var wy = _noise.Noise(p.X * 2.1 + 2.0, p.Y * 2.1 + 8.0, p.Z * 2.1 + 3.0);
        var wz = _noise.Noise(p.X * 2.1 + 7.0, p.Y * 2.1 + 4.0, p.Z * 2.1 + 6.0);
        var warped = new double3(p.X + 0.45 * wx, p.Y + 0.45 * wy, p.Z + 0.45 * wz);

        // Mare shores trace a contour near zero, which hugs one lattice's planes; two lattices turned against each other leave none straight.
        var tilted = MareLattice * warped;
        var field = (_noise.Fbm(warped.X + 0.37, warped.Y + 0.61, warped.Z + 0.19, 5) + _noise.Fbm(tilted.X + 3.7, tilted.Y + 1.3, tilted.Z + 8.9, 5)) * Math.Sqrt(0.5);
        var mare = SmoothStep(0.04, 0.16, field);

        var height = -_data.MareDepth * mare + Detail(d, resolution, 1.0 - 0.65 * mare);
        var feature = 0.0;

        Craters(d, resolution, mare, ref height, ref feature);

        var regolith = 0.08 * _noise.Fbm(p.X * 37 + 17, p.Y * 37 + 31, p.Z * 37 + 53, 3);
        var tone = Math.Clamp(mare + regolith, 0, 1);

        return new TerrainSample { Height = height, Moisture = (float)tone, Feature = (float)Math.Clamp(feature, 0, 1) };

    }

    // Fractal detail from the base wavelength down to twice the requested resolution.
    private double Detail(double3 d, double resolution, double scale) {

        var wavelength = _data.DetailWavelength;
        var amplitude = _data.DetailAmplitude * scale;
        var decay = Math.Pow(0.5, _data.Roughness);
        var sum = 0.0;

        for (var octave = 0; octave < MaxDetailOctaves && wavelength >= resolution; octave++) {

            var frequency = _radius / wavelength;
            var weight = SmoothStep(resolution, 2 * resolution, wavelength);
            var offset = octave * 31.7;

            sum += amplitude * weight * _noise.Noise(d.X * frequency + offset, d.Y * frequency - offset, d.Z * frequency + offset * 0.5);

            wavelength *= 0.5;
            amplitude *= decay;

        }

        return sum;

    }

    // Bowl-shaped craters from cellular placement at halving radii; the eight nearest cells cover every overlap.
    private void Craters(double3 d, double resolution, double mare, ref double height, ref double feature) {

        var radius = _data.LargestCrater;
        var scale = 0;
        var point = d * _radius;

        while (radius >= _data.SmallestCrater && radius >= resolution * 1.5) {

            var cell = radius * 4;
            var q = point / cell;
            var bx = Math.Floor(q.X - 0.5);
            var by = Math.Floor(q.Y - 0.5);
            var bz = Math.Floor(q.Z - 0.5);
            // Doubled because only about half the candidate centres lie near enough the surface to be kept.
            var density = 2 * _data.CraterDensity * (1 - 0.6 * mare);

            for (var corner = 0; corner < 8; corner++) {

                var cx = bx + (corner & 1);
                var cy = by + ((corner >> 1) & 1);
                var cz = bz + ((corner >> 2) & 1);

                var h0 = Hash(cx, cy, cz, scale, 0);

                // Density follows the mare under each point, so a crater on a shore fades out across it rather than ending in a cliff.
                var presence = SmoothStep(0, 0.08, density - h0);

                if (presence <= 0) {

                    continue;

                }

                var centre = new double3(cx + 0.25 + 0.5 * Hash(cx, cy, cz, scale, 1), cy + 0.25 + 0.5 * Hash(cx, cy, cz, scale, 2), cz + 0.25 + 0.5 * Hash(cx, cy, cz, scale, 3)) * cell;

                // Projecting a centre onto the sphere moves it; past a quarter cell the eight-cell search could miss it and cut the crater off.
                if (Math.Abs(centre.Length() - _radius) > 0.25 * cell) {

                    continue;

                }

                var surface = double3.Normalize(centre) * _radius;
                var r = radius * (0.5 + 0.5 * Hash(cx, cy, cz, scale, 4));
                var distance = (point - surface).Length() / r;

                if (distance >= 2) {

                    continue;

                }

                var fresh = Hash(cx, cy, cz, scale, 5);
                var depth = r * (0.2 + (0.06 - 0.2) * SmoothStep(1500, 25000, r)) * (0.4 + 0.6 * fresh);
                depth *= presence;

                var rim = 0.25 * depth;

                if (distance < 1) {

                    height += Math.Max(depth * (distance * distance - 1), -0.85 * depth) + rim * Math.Pow(distance, 4);

                } else {

                    var falloff = 1 - SmoothStep(1, 2, distance);

                    height += rim * falloff * falloff;

                }

                var normal = surface / _radius;
                var tangent = double3.Normalize(double3.Cross(Math.Abs(normal.Y) < 0.9 ? double3.UnitY : double3.UnitX, normal));
                var delta = point - surface;
                var angle = Math.Atan2(double3.Dot(delta, double3.Cross(normal, tangent)), double3.Dot(delta, tangent));
                // Only the youngest craters keep bright walls and rays; older ones have weathered into the regolith around them.
                var young = SmoothStep(0.72, 0.97, fresh);
                var spokes = 5 + Math.Floor(13 * Hash(cx, cy, cz, scale, 6));
                var sharpness = 3 + 9 * Hash(cx, cy, cz, scale, 7);
                var reach = 1.3 + 0.7 * Hash(cx, cy, cz, scale, 8);
                var rays = Math.Pow(0.5 + 0.5 * Math.Cos(angle * spokes + 2.4 * Math.Sin(angle * 3 + fresh * 17)), sharpness);
                var walls = SmoothStep(0.45, 0.98, distance) * (1 - SmoothStep(1.0, 1.35, distance));
                var blanket = (0.25 + 0.75 * rays) * SmoothStep(0.82, 1.05, distance) * (1 - SmoothStep(1.0, reach, distance)) * young;

                feature = Math.Max(feature, presence * fresh * (0.2 * (1 - SmoothStep(0.5, 1.0, distance)) + 0.72 * walls * (0.25 + 0.75 * young) + blanket));

            }

            radius *= 0.5;
            scale++;

        }

    }

    private double Hash(double x, double y, double z, int scale, int channel) {

        unchecked {

            var h = (ulong)(long)x * 0x9E3779B97F4A7C15UL ^ (ulong)(long)y * 0xC2B2AE3D27D4EB4FUL ^ (ulong)(long)z * 0x165667B19E3779F9UL;

            h ^= (ulong)(scale * 0x27D4EB2F + channel * 0x61C88647 + _data.Seed * 0x3C6EF372);
            h ^= h >> 33;
            h *= 0xFF51AFD7ED558CCDUL;
            h ^= h >> 33;
            h *= 0xC4CEB9FE1A85EC53UL;
            h ^= h >> 33;

            return (h >> 11) * (1.0 / (1UL << 53));

        }

    }

    private static double SmoothStep(double edge0, double edge1, double x) {

        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);

        return t * t * (3 - 2 * t);

    }

}
