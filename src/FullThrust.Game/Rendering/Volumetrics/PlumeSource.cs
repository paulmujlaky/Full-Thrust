using System.Text.Json;
using Brutal.Numerics;
using FullThrust.Core.Bodies;
using FullThrust.Core.Craft;
using FullThrust.Core.Ecs;
using FullThrust.Core.Parts;
using FullThrust.Core.Space;

namespace FullThrust.Game.Rendering.Volumetrics;

public sealed class PlumeStyleData {

    public string Fuel { get; set; } = "";

    public double[] Core { get; set; } = [1, 0.6, 0.3];

    public double Brightness { get; set; } = 1;

    public double Soot { get; set; }

    public double[] Albedo { get; set; } = [0.5, 0.5, 0.5];

    // Colour the cooler outer sheath shifts towards, as a multiplier of the core colour.
    public double[] Sheath { get; set; } = [1.0, 0.55, 0.3];

    // How far the hot core near the nozzle washes out towards white.
    public double CoreWhiteness { get; set; } = 0.6;

}

public sealed class PlumeStylesData {

    public List<PlumeStyleData> Styles { get; set; } = [];

}

// Exhaust plumes for every burning engine, shaped by its nozzle and the ambient pressure.
public sealed class PlumeSource : IVolumeSource {

    // Luminance of a brightness-1 plume core, in cd/m2; kerosene afterburning is about this bright.
    private const float CoreLuminance = 4.0e5f;

    private const float PuffLength = 2.4f;

    private const int MaxEngines = 64;

    // Engines whose exits sit within this many summed exit radii share one column of exhaust.
    private const float MergeReach = 1.6f;

    private static readonly JsonSerializerOptions Options = new() {

        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,

    };

    private readonly World _world;
    private readonly FloatingOrigin _origin;

    // Indexed by fuel resource; null where no style is defined.
    private readonly PlumeStyleData?[] _styles;

    private readonly PointLights _lights;
    private readonly GroundSteam _steam;
    private readonly HullSet _hulls;
    private readonly Pending[] _pending = new Pending[MaxEngines];

    private struct Pending {

        public Entity Engine;
        public Entity Craft;

        public double3 Exit;
        public double3 Axis;

        public VolumePrimitive Plume;
        public float Pump;
        public bool Clustered;

        // Index of the engine leading this one's merged column, or -1 before grouping.
        public int Group;

    }

    public PlumeSource(World world, FloatingOrigin origin, PointLights lights, PartCatalog parts, string stylesFile, CelestialSystem bodies, HullSet hulls) {

        _hulls = hulls;
        _world = world;
        _origin = origin;
        _lights = lights;
        _steam = new GroundSteam(bodies, origin);

        var data = JsonSerializer.Deserialize<PlumeStylesData>(File.ReadAllText(stylesFile), Options) ?? new PlumeStylesData();

        _styles = new PlumeStyleData?[parts.Resources.Count];

        for (var i = 0; i < _styles.Length; i++) {

            _styles[i] = data.Styles.Find(s => string.Equals(s.Fuel, parts.Resources[i].Name, StringComparison.OrdinalIgnoreCase));

        }

    }

    public bool Visible { get; set; } = true;

    public int Count { get; private set; }
    public double UniversalTime { get; set; }
    public void ClearSteam() => _steam.Clear();

    public PlumeStyleData? StyleFor(int fuel) => fuel >= 0 && fuel < _styles.Length ? _styles[fuel] : null;

    public void Collect(VolumeList volumes) {

        var transforms = _world.Store<Transform>();
        var flights = _world.Store<FlightState>();
        var masses = _world.Store<CraftMass>();

        Count = 0;

        var pending = 0;

        foreach (var row in _world.Query<EngineState, Part>()) {

            ref readonly var engine = ref row.C1;
            ref readonly var part = ref row.C2;

            if (pending == MaxEngines || !engine.IsBurning || engine.Pump <= 0.01 || engine.Definition.Fuel < 0 || engine.Definition.Fuel >= _styles.Length || _styles[engine.Definition.Fuel] is not { } style) {

                continue;

            }

            ref readonly var craft = ref transforms.Get(part.Craft);

            var exit = craft.Position + craft.Rotation * (part.LocalPosition - part.LocalRotation * new double3(0, part.Definition.Length / 2, 0));
            var axis = -double3.NormalizeOrZero(craft.Rotation * engine.ThrustDirection);
            var ambient = flights.Has(part.Craft) ? flights.Get(part.Craft).Ambient.Pressure : 0;
            var plume = Describe(engine, part.Definition, style, _origin.ToLocal(exit), SpaceMath.ToFloat(axis), (float)ambient);

            if (engine.Definition.Cluster is { } cluster) {

                var right = craft.Rotation * (part.LocalRotation * double3.UnitX);
                var across = double3.Normalize(right - axis * double3.Dot(right, axis));
                // A plume intersects its cone directly, leaving these coordinates free for the nozzle ring.
                plume.Position = new float4(SpaceMath.ToFloat(across * cluster.RingRadius), plume.Position.W);
                plume.Kind.W = cluster.Center ? cluster.RingCount : -cluster.RingCount;

            }

            _pending[pending++] = new Pending {

                Engine = row.Entity,
                Craft = part.Craft,

                Exit = exit,
                Axis = axis,

                Plume = plume,
                Pump = (float)engine.Pump,
                Clustered = engine.Definition.Cluster is not null,
                Group = -1,

            };

        }

        for (var i = 0; i < pending; i++) {

            if (_pending[i].Group >= 0) {

                continue;

            }

            var members = Gather(i, pending);
            var lead = _pending[i];
            var plume = members > 1 ? Merge(i, pending, members) : lead.Plume;
            var exit = members > 1 ? _origin.ToWorld(plume.Params0.XYZ) : lead.Exit;
            var flight = flights.Has(lead.Craft) ? flights.Get(lead.Craft) : default;

            ref readonly var craft = ref transforms.Get(lead.Craft);

            var centerOfMass = craft.Position + craft.Rotation * masses.Get(lead.Craft).CenterOfMass;

            Strike(ref plume, lead.Craft, flight, centerOfMass, exit, lead.Axis);

            if (!volumes.Add(plume)) {

                continue;

            }

            Count++;

            var exitRadius = plume.Params0.W;
            var length = plume.Axis.W;

            // The brightest third of the plume, seen side on, as a point light at its middle.
            var luminance = new float3(plume.Emission.X, plume.Emission.Y, plume.Emission.Z) * (2 * exitRadius);

            _lights.Add(plume.Params0.XYZ + plume.Axis.XYZ * (length * 0.15f), exitRadius * 2, luminance * (length * 0.3f * 2 * exitRadius));

            AddSteam(volumes, lead.Engine, flight, centerOfMass, exit, lead.Axis, exitRadius, length, lead.Pump);

        }

        AddPuffs(volumes, transforms);
        _steam.Collect(volumes, UniversalTime);

    }

    // Marks engines on the same craft firing side by side, close enough that their exhaust runs together, as one group led by the first.
    private int Gather(int first, int pending) {

        ref var lead = ref _pending[first];

        lead.Group = first;

        var members = 1;

        for (var j = first + 1; j < pending && !lead.Clustered; j++) {

            ref var other = ref _pending[j];

            var gap = (float)(other.Exit - lead.Exit).Length();
            var reach = MergeReach * (lead.Plume.Params0.W + other.Plume.Params0.W);

            if (other.Group >= 0 || other.Clustered || other.Craft != lead.Craft || double3.Dot(other.Axis, lead.Axis) < 0.995 || gap > reach) {

                continue;

            }

            other.Group = first;
            members++;

        }

        return members;

    }

    // One column for the group: a ring of bells around their centroid, as engine clusters are drawn.
    private VolumePrimitive Merge(int first, int pending, int members) {

        var centroid = float3.Zero;
        var emission = float3.Zero;
        var bell = 0f;
        var length = 0f;

        for (var j = first; j < pending; j++) {

            ref readonly var member = ref _pending[j];

            if (member.Group != first) {

                continue;

            }

            centroid += member.Plume.Params0.XYZ;
            emission += member.Plume.Emission.XYZ;
            bell += member.Plume.Params0.W;
            length = MathF.Max(length, member.Plume.Axis.W);

        }

        centroid /= members;
        emission /= members;
        bell /= members;

        var ring = 0f;
        var ringVector = float3.Zero;
        var centred = false;

        for (var j = first; j < pending; j++) {

            ref readonly var member = ref _pending[j];

            if (member.Group != first) {

                continue;

            }

            var offset = member.Plume.Params0.XYZ - centroid;

            if (offset.Length() < bell * 0.5f) {

                centred = true;
                continue;

            }

            ring = MathF.Max(ring, offset.Length());
            ringVector = offset;

        }

        var plume = _pending[first].Plume;
        var exitRadius = ring + bell;
        var spread = plume.Params1.X;
        var widest = exitRadius + length * spread;
        var ringCount = centred ? members - 1 : members;

        // The column carries every bell's light, spread over its wider exit.
        var glow = MathF.Min(members * bell / exitRadius, 1.5f);

        plume.Kind.W = ringCount == 0 ? 0 : centred ? ringCount : -ringCount;
        plume.Position = new float4(ringCount == 0 ? float3.Zero : float3.Normalize(ringVector) * ring, MathF.Sqrt(length * length * 0.25f + widest * widest));
        plume.Axis = new float4(plume.Axis.XYZ, length);
        plume.Emission = new float4(emission * glow, plume.Emission.W);
        plume.Params0 = new float4(centroid, exitRadius);
        plume.Params1 = new float4(spread, plume.Params1.Y * exitRadius / bell, plume.Params1.Z, plume.Params1.W);

        return plume;

    }

    // Where the jet meets the ground or another hull it spreads along it, and gas stops at any hull nearby.
    private void Strike(ref VolumePrimitive plume, Entity craft, in FlightState flight, double3 centerOfMass, double3 exit, double3 axis) {

        var length = plume.Axis.W;
        var exitRadius = plume.Params0.W;
        var start = plume.Params0.XYZ;
        var direction = plume.Axis.XYZ;
        var impact = float.MaxValue;
        var normal = float3.Zero;

        if (flight.Gravity != double3.Zero) {

            var up = -double3.Normalize(flight.Gravity);
            var down = -double3.Dot(axis, up);
            var height = flight.SurfaceAltitude + double3.Dot(exit - centerOfMass, up);

            if (down > 0.2 && height > -1) {

                impact = (float)(Math.Max(height, 0) / down);
                normal = SpaceMath.ToFloat(up);

            }

        }

        var hit = _hulls.Raycast(start + direction * (exitRadius * 0.1f), direction, MathF.Min(length, impact), craft, out var hullNormal);

        if (!float.IsNaN(hit) && hit + exitRadius * 0.1f < impact) {

            impact = hit + exitRadius * 0.1f;
            normal = hullNormal;

        }

        var widest = exitRadius + length * plume.Params1.X;
        var center = start + direction * (length * 0.5f);

        if (impact < length * 0.95f) {

            // The wall jet runs out along the surface as far as the flow would have reached.
            var strikeRadius = exitRadius + impact * (plume.Params1.X + 0.02f);
            var runout = length - impact + strikeRadius;

            plume.Params3 = new float4(normal, MathF.Max(impact, 1e-3f));
            plume.Position.W = runout * 1.05f;
            center = start + direction * impact;
            widest = plume.Position.W;

        }

        plume.Hull = _hulls.Select(center, MathF.Max(plume.Position.W, widest), craft);

    }

    // Firing RCS nozzles vent a short cone of propellant mist, seen by the sunlight it scatters.
    private void AddPuffs(VolumeList volumes, ComponentStore<Transform> transforms) {

        foreach (var row in _world.Query<RcsState, Part>()) {

            ref readonly var rcs = ref row.C1;

            if (rcs.Firing == 0) {

                continue;

            }

            ref readonly var part = ref row.C2;
            ref readonly var craft = ref transforms.Get(part.Craft);

            var nozzles = rcs.Definition.Nozzles;

            for (var n = 0; n < nozzles.Length && n < 32; n++) {

                if ((rcs.Firing & (1u << n)) == 0) {

                    continue;

                }

                var exhaust = -double3.Normalize(craft.Rotation * (part.LocalRotation * nozzles[n]));
                var exit = _origin.ToLocal(craft.Position + craft.Rotation * part.LocalPosition + exhaust * (part.Definition.Radius * 0.8));
                var axis = SpaceMath.ToFloat(exhaust);

                volumes.Add(new VolumePrimitive {

                    Kind = new int4((int)VolumeKind.Plume, 16, 2, 0),
                    Position = new float4(exit + axis * (PuffLength * 0.5f), PuffLength),
                    Axis = new float4(axis, PuffLength),
                    Medium = new float4(0.92f, 0.92f, 0.92f, 70),
                    Emission = new float4(0, 0, 0, 0.5f),
                    Params0 = new float4(exit, 0.05f),
                    Params1 = new float4(0.5f, 1, 0, 0),
                    Params2 = new float4(1, 1, 1, 0),

                });

            }

        }

    }

    // Where the exhaust strikes the ground it throws up a cloud of steam and dust, lit by the plume.
    private void AddSteam(VolumeList volumes, Entity engine, in FlightState flight, double3 centerOfMass, double3 exit, double3 axis, float exitRadius, float length, float throttle) {

        if (flight.Gravity == double3.Zero) {

            return;

        }

        var up = -double3.Normalize(flight.Gravity);
        var down = -double3.Dot(axis, up);
        var height = flight.SurfaceAltitude + double3.Dot(exit - centerOfMass, up);

        if (down < 0.3 || height < -1 || height > length * 0.8) {

            return;

        }

        var impact = exit + axis * (height / down);
        var coupling = Math.Clamp(1 - (float)(height / (length * 0.8)), 0, 1);
        var air = Math.Clamp((float)flight.Ambient.Pressure / 6000, 0, 1);
        _steam.Emit(engine, flight.Body, impact, exitRadius, coupling * throttle * air, UniversalTime);
        var radius = exitRadius * (9 + 17 * coupling) * MathF.Sqrt(throttle);

        volumes.Add(new VolumePrimitive {

            Kind = new int4((int)VolumeKind.GroundOutflow, 20, 2, 0),
            Position = new float4(_origin.ToLocal(impact), radius),
            Axis = new float4(SpaceMath.ToFloat(up), 0),
            Medium = new float4(0.92f, 0.91f, 0.88f, (0.05f + 0.36f * air) * coupling * throttle),
            Emission = new float4(0, 0, 0, 0.3f),

        });

    }

    private static VolumePrimitive Describe(in EngineState engine, PartDefinition part, PlumeStyleData style, float3 exit, float3 axis, float ambient) {

        var definition = engine.Definition;
        var throttle = (float)Math.Clamp(engine.Pump, 0, 1);

        var exitRadius = (float)Math.Min(Math.Sqrt(Math.Max(definition.ExitArea, 1e-4) / Math.PI), part.Radius * 0.95);

        if (definition.Cluster is { } cluster) {

            exitRadius = (float)(cluster.RingRadius + cluster.Diameter * 0.5);

        }

        // Nozzles designed for sea level exhaust near ambient pressure; vacuum nozzles expand far further.
        var isp = (float)(definition.SeaLevelIsp / Math.Max(definition.VacuumIsp, 1));
        var exitPressure = (float)PhysicalConstants.SeaLevelPressure * Math.Clamp(0.75f * MathF.Pow(isp, 6), 0.002f, 0.8f);
        var ratio = exitPressure / MathF.Max(ambient, 1.0f);

        var spread = ratio > 1 ? Math.Clamp(0.04f + 0.11f * MathF.Log(ratio), 0.04f, 1.2f) : 0.02f;
        var thin = 1 - Math.Clamp(ambient / (0.3f * (float)PhysicalConstants.SeaLevelPressure), 0, 1);
        var length = exitRadius * (20 + 40 * throttle) * (1 + 2.5f * thin);

        // Shock diamonds stand in dense air, spaced by the exit diameter and how far the flow is from matching ambient.
        var diamondSpacing = 2 * exitRadius * 1.3f * MathF.Sqrt(MathF.Max(ratio, 0.2f));
        var diamonds = Math.Clamp((ambient - 5000) / 25000, 0, 1);

        var widest = exitRadius + length * spread;
        var center = exit + axis * (length * 0.5f);
        var bound = MathF.Sqrt(length * length * 0.25f + widest * widest);

        var core = new float3((float)style.Core[0], (float)style.Core[1], (float)style.Core[2]);
        // Dense air keeps the afterburning sheath hot and opaque, so a sea-level plume reads brighter than its vacuum core.
        var emission = core * (float)(style.Brightness * CoreLuminance * throttle / (2 * exitRadius)) * (1 + 0.7f * (1 - thin));

        // Expanded into near vacuum the exhaust is too thin to darken anything; what little it holds scatters sunlight brightly.
        var albedo = float3.Lerp(new float3((float)style.Albedo[0], (float)style.Albedo[1], (float)style.Albedo[2]), new float3(0.85f), thin);
        var soot = (float)style.Soot * throttle / exitRadius * (1 - 0.9f * thin);

        return new VolumePrimitive {

            Kind = new int4((int)VolumeKind.Plume, 32, 2, 0),
            Position = new float4(center, bound),
            Axis = new float4(axis, length),
            Medium = new float4(albedo, soot),
            Emission = new float4(emission, 0.3f),
            Params0 = new float4(exit, exitRadius),
            Params1 = new float4(spread, diamondSpacing, diamonds, thin),
            Params2 = new float4((float)style.Sheath[0], (float)style.Sheath[1], (float)style.Sheath[2], (float)style.CoreWhiteness),

        };

    }

}
