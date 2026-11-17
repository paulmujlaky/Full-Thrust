using Brutal.Numerics;
using FullThrust.Core.Craft;
using FullThrust.Core.Ecs;
using FullThrust.Core.Parts;
using FullThrust.Core.Space;

namespace FullThrust.Game.Rendering.Volumetrics;

// Plasma around craft entering an atmosphere fast: a glowing bow shock ahead, a wake behind, and the light they throw on the hull.
public sealed class ReentrySource : IVolumeSource {

    // Sutton-Graves constant for air: heat flux in W/m2 from sqrt(density / nose radius) * speed^3.
    private const double SuttonGraves = 1.83e-4;

    // Heat flux where the shock layer starts to glow, and where it is at its brightest.
    private const double GlowFlux = 4e4;
    private const double PeakFlux = 2e6;

    // Radiance of the shock layer at peak heating, in cd/m2.
    private const float PeakLuminance = 200000f;

    // Wake radius at the neck as a share of the nose radius, how fast it widens per metre beyond the plume's own 0.02, and its glow against the shock.
    private const float WakeRadius = 0.55f;
    private const float WakeSpread = -0.012f;
    private const float WakeGlow = 0.5f;

    private static readonly float3 Dim = new(1.0f, 0.36f, 0.12f);
    private static readonly float3 Hot = new(1.0f, 0.72f, 0.66f);

    private readonly World _world;
    private readonly FloatingOrigin _origin;
    private readonly PointLights _lights;
    private readonly HullSet _hulls;

    public ReentrySource(World world, FloatingOrigin origin, PointLights lights, HullSet hulls) {

        _hulls = hulls;
        _world = world;
        _origin = origin;
        _lights = lights;

    }

    public bool Visible { get; set; } = true;

    public int Count { get; private set; }

    // Strongest glow last collected, 0 to 1.
    public float Strength { get; private set; }

    public void Collect(VolumeList volumes) {

        Count = 0;
        Strength = 0;

        foreach (var row in _world.Query<FlightState, Transform>()) {

            ref readonly var flight = ref row.C1;
            ref readonly var craft = ref row.C2;

            var speed = flight.AirVelocity.Length();

            if (speed < 1000 || flight.Ambient.Density <= 0) {

                continue;

            }

            var upstream = flight.AirVelocity / speed;

            Measure(row.Entity, craft, upstream, out var front, out var back, out var nose, out var reach);

            var flux = SuttonGraves * Math.Sqrt(flight.Ambient.Density / nose) * speed * speed * speed;
            var strength = (float)Math.Clamp(Math.Log(flux / GlowFlux) / Math.Log(PeakFlux / GlowFlux), 0, 1);

            if (strength <= 0) {

                continue;

            }

            Strength = MathF.Max(Strength, strength);

            var colour = float3.Lerp(Dim, Hot, strength);
            var luminance = PeakLuminance * strength * MathF.Sqrt(strength);
            var downstream = -SpaceMath.ToFloat(upstream);

            // The shock wraps the hull itself, so a flat shield wears a flat glowing sheet and a broadside stack a long one.
            var radius = (float)nose;
            var length = radius * 3;
            var center = _origin.ToLocal(craft.Position);
            var bound = (float)reach + radius * 0.6f + length * 0.5f;
            var middle = center + downstream * (length * 0.5f);

            var shock = volumes.Add(new VolumePrimitive {

                Kind = new int4((int)VolumeKind.Shock, 96, 0, 0),
                Position = new float4(middle, bound),
                Axis = new float4(downstream, length),
                Medium = new float4(0, 0, 0, 0),
                Emission = new float4(colour * (luminance / (radius * 0.5f)), 0),
                Params0 = new float4(center, radius),
                Hull = _hulls.Select(middle, bound, Entity.Null, radius * 0.5f),

            });

            if (shock) {

                Count++;

            }


            // The flow closes in behind the shoulders into a neck, then trails as a thin streaming filament rather than a widening cone.
            var wakeStart = _origin.ToLocal(craft.Position - upstream * (back * 0.6));
            var wakeRadius = (float)nose * WakeRadius;
            var wakeLength = (float)nose * (8 + 30 * strength);
            var wakeEnd = wakeRadius + wakeLength * (WakeSpread + 0.02f);

            var wake = volumes.Add(new VolumePrimitive {

                Kind = new int4((int)VolumeKind.Plume, 24, 1, 0),
                Position = new float4(wakeStart + downstream * (wakeLength * 0.5f), MathF.Sqrt(wakeLength * wakeLength * 0.25f + wakeEnd * wakeEnd * 4)),
                Axis = new float4(downstream, wakeLength),
                Medium = new float4(0.5f, 0.5f, 0.5f, 0),
                Emission = new float4(colour * (luminance * WakeGlow / (2 * wakeRadius)), 0.3f),
                Params0 = new float4(wakeStart, wakeRadius),
                Params1 = new float4(WakeSpread, 1, 0, 0),
                Params2 = new float4(1.0f, 0.45f, 0.55f, 0.35f),

            });

            if (wake) {

                Count++;

            }

            // The glowing gas just ahead of the craft, as seen by the hull.
            var glow = _origin.ToLocal(craft.Position + upstream * front);

            _lights.Add(glow, (float)nose * 2, colour * (luminance * MathF.PI * (float)(nose * nose) * 4));

        }

    }

    // How far the craft reaches upstream and downstream of its origin along the flow, its largest part radius, and how far any part reaches from its origin.
    private void Measure(Entity entity, in Transform craft, double3 upstream, out double front, out double back, out double nose, out double reach) {

        front = double.MinValue;
        back = 1;
        nose = 0.5;
        reach = 1;

        foreach (var row in _world.Query<Part>()) {

            ref readonly var part = ref row.C1;

            if (part.Craft != entity) {

                continue;

            }

            nose = Math.Max(nose, part.Definition.Radius);
            front = Math.Max(front, Leading(part, craft, upstream, out var offset));
            back = Math.Max(back, Leading(part, craft, -upstream, out _));
            reach = Math.Max(reach, offset.Length() + Math.Max(part.Definition.Length * 0.5, part.Definition.Radius));

        }

        front = front == double.MinValue ? 1 : front;

    }

    // How far a part reaches along a direction from the craft origin; offset is its centre from that origin.
    private static double Leading(in Part part, in Transform craft, double3 direction, out double3 offset) {

        offset = craft.Rotation * part.LocalPosition;

        var axis = craft.Rotation * (part.LocalRotation * double3.UnitY);
        var tilt = Math.Abs(double3.Dot(axis, direction));

        return double3.Dot(offset, direction) + tilt * part.Definition.Length * 0.5 + (1 - tilt) * part.Definition.Radius;

    }

}
