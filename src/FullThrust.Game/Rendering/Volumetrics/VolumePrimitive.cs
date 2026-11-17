using System.Runtime.InteropServices;
using Brutal.Numerics;

namespace FullThrust.Game.Rendering.Volumetrics;

// Mirrors the kind constants in Shaders/volumetric.glsl.
public enum VolumeKind {

    None,
    NoiseSphere,
    Plume,
    CloudLayer,
    Shock,
    GroundOutflow,
    Cirrus,

}

// Mirrors VolumePrimitive in Shaders/volumetric.glsl (std430).
[StructLayout(LayoutKind.Sequential)]
public struct VolumePrimitive {

    // x kind, y march steps, z shadow steps.
    public int4 Kind;

    // xyz render-local origin, w bounding radius.
    public float4 Position;

    // xyz unit axis, w length.
    public float4 Axis;

    // rgb single-scattering albedo, w extinction per metre at full density.
    public float4 Medium;

    // rgb emitted radiance per unit density, w phase anisotropy.
    public float4 Emission;

    // Meaning depends on the kind.
    public float4 Params0;
    public float4 Params1;
    public float4 Params2;

    // Meaning depends on the kind; plumes keep the surface they strike here: xyz normal, w distance along the axis or 0.
    public float4 Params3;

    // x first entry and y count in the shared hull index list.
    public int4 Hull;

    public readonly VolumePush Push => new() {

        Kind = Kind,
        Position = Position,
        Axis = Axis,

        Medium = Medium,
        Emission = Emission,

        Params0 = Params0,
        Params1 = Params1,
        Params2 = Params2,

    };

}

// The leading fields of a primitive, for kernels that take one as push constants.
[StructLayout(LayoutKind.Sequential)]
public struct VolumePush {

    public int4 Kind;
    public float4 Position;
    public float4 Axis;
    public float4 Medium;
    public float4 Emission;
    public float4 Params0;
    public float4 Params1;
    public float4 Params2;

}

// The primitives gathered for one frame; fixed capacity so collecting never allocates.
public sealed class VolumeList {

    private readonly VolumePrimitive[] _items;

    public VolumeList(int capacity) => _items = new VolumePrimitive[capacity];

    public int Count { get; private set; }

    public int Capacity => _items.Length;

    public ReadOnlySpan<VolumePrimitive> Items => _items.AsSpan(0, Count);

    public void Clear() => Count = 0;

    public bool Add(in VolumePrimitive primitive) {

        if (Count == _items.Length) {

            return false;

        }

        _items[Count++] = primitive;

        return true;

    }

}

// Anything that contributes participating media: plumes, clouds, smoke.
public interface IVolumeSource {

    bool Visible { get; }

    void Collect(VolumeList volumes);

}
