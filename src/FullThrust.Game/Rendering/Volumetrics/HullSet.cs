using System.Runtime.InteropServices;
using Brutal.Numerics;
using FullThrust.Core.Ecs;
using FullThrust.Core.Parts;
using FullThrust.Core.Space;

namespace FullThrust.Game.Rendering.Volumetrics;

// Mirrors HullPrimitive in Shaders/volumetric.glsl: a capped cone between two end centres.
[StructLayout(LayoutKind.Sequential)]
public struct HullPrimitive {

    // xyz render-local aft centre, w its radius.
    public float4 Aft;

    // xyz render-local fore centre, w its radius.
    public float4 Fore;

}

// Every craft's parts as capped cones in render-local space, shared by the volumes that wrap, deflect off or stop at hulls.
public sealed class HullSet {

    public const int Capacity = 128;
    public const int IndexCapacity = 512;

    // A volume tests no more than this many nearby primitives per sample.
    public const int MaxPerVolume = 12;

    private readonly World _world;
    private readonly FloatingOrigin _origin;

    private readonly HullPrimitive[] _primitives = new HullPrimitive[Capacity];
    private readonly Entity[] _owners = new Entity[Capacity];
    private readonly Entity[] _parts = new Entity[Capacity];
    private readonly int[] _indices = new int[IndexCapacity];

    public HullSet(World world, FloatingOrigin origin) {

        _world = world;
        _origin = origin;

    }

    public int Count { get; private set; }

    public int IndexCount { get; private set; }

    public ReadOnlySpan<HullPrimitive> Primitives => _primitives.AsSpan(0, Count);

    public ReadOnlySpan<int> Indices => _indices.AsSpan(0, IndexCount);

    public void Update() {

        Count = 0;
        IndexCount = 0;

        var transforms = _world.Store<Transform>();

        foreach (var row in _world.Query<Part>()) {

            ref readonly var part = ref row.C1;

            if (Count == Capacity || !transforms.Has(part.Craft)) {

                continue;

            }

            ref readonly var craft = ref transforms.Get(part.Craft);

            var definition = part.Definition;
            var axis = craft.Rotation * (part.LocalRotation * double3.UnitY);
            var center = _origin.ToLocal(craft.Position + craft.Rotation * part.LocalPosition);
            var half = SpaceMath.ToFloat(axis * (definition.Length / 2));

            _owners[Count] = part.Craft;
            _parts[Count] = row.Entity;
            _primitives[Count++] = new HullPrimitive {

                Aft = new float4(center - half, (float)definition.Radius),
                Fore = new float4(center + half, (float)definition.TopRadius),

            };

        }

    }

    // Primitives touching a sphere, as an offset and count into the shared index list; one craft and parts thinner than smallest can be left out.
    public int4 Select(float3 center, float radius, Entity exclude, float smallest = 0) {

        var first = IndexCount;

        for (var i = 0; i < Count && IndexCount < IndexCapacity && IndexCount - first < MaxPerVolume; i++) {

            if (_owners[i] == exclude) {

                continue;

            }

            ref readonly var p = ref _primitives[i];

            if (MathF.Max(p.Aft.W, p.Fore.W) < smallest) {

                continue;

            }

            var mid = (p.Aft.XYZ + p.Fore.XYZ) * 0.5f;
            var reach = (p.Fore.XYZ - p.Aft.XYZ).Length() * 0.5f + MathF.Max(p.Aft.W, p.Fore.W);

            if ((mid - center).Length() <= radius + reach) {

                _indices[IndexCount++] = i;

            }

        }

        return new int4(first, IndexCount - first, 0, 0);

    }

    // Nearest hull surface along a ray by sphere tracing, skipping one craft; distance is NaN when nothing is hit.
    public float Raycast(float3 origin, float3 direction, float range, Entity exclude, out float3 normal) {

        normal = float3.Zero;

        var t = 0f;

        for (var step = 0; step < 64 && t < range; step++) {

            var d = Distance(origin + direction * t, exclude, out var nearest);

            if (d < 0.02f) {

                normal = Normal(nearest, origin + direction * t);

                return t;

            }

            t += d;

        }

        return float.NaN;

    }

    public float Distance(float3 p, Entity exclude, out int nearest) {

        var best = float.MaxValue;

        nearest = -1;

        for (var i = 0; i < Count; i++) {

            if (_owners[i] == exclude) {

                continue;

            }

            var d = CappedCone(p, _primitives[i]);

            if (d < best) {

                best = d;
                nearest = i;

            }

        }

        return best;

    }

    public bool Owns(int primitive, Entity part) => primitive >= 0 && primitive < Count && _parts[primitive] == part;

    private float3 Normal(int primitive, float3 p) {

        if (primitive < 0) {

            return float3.Zero;

        }

        const float E = 0.01f;

        var h = _primitives[primitive];
        var gradient = new float3(

            CappedCone(p + new float3(E, 0, 0), h) - CappedCone(p - new float3(E, 0, 0), h),
            CappedCone(p + new float3(0, E, 0), h) - CappedCone(p - new float3(0, E, 0), h),
            CappedCone(p + new float3(0, 0, E), h) - CappedCone(p - new float3(0, 0, E), h)

        );

        return float3.NormalizeOrZero(gradient);

    }

    // Signed distance to a capped cone (Quilez), matching HullConeDistance in the shader.
    public static float CappedCone(float3 p, in HullPrimitive h) {

        var a = h.Aft.XYZ;
        var b = h.Fore.XYZ;
        var ra = h.Aft.W;
        var rb = h.Fore.W;

        var rba = rb - ra;
        var baba = float3.Dot(b - a, b - a);
        var papa = float3.Dot(p - a, p - a);
        var paba = float3.Dot(p - a, b - a) / baba;
        var x = MathF.Sqrt(MathF.Max(papa - paba * paba * baba, 0));
        var cax = MathF.Max(0, x - (paba < 0.5f ? ra : rb));
        var cay = MathF.Abs(paba - 0.5f) - 0.5f;
        var k = rba * rba + baba;
        var f = Math.Clamp((rba * (x - ra) + paba * baba) / k, 0, 1);
        var cbx = x - ra - f * rba;
        var cby = paba - f;
        var s = cbx < 0 && cay < 0 ? -1f : 1f;

        return s * MathF.Sqrt(MathF.Min(cax * cax + cay * cay * baba, cbx * cbx + cby * cby * baba));

    }

}
