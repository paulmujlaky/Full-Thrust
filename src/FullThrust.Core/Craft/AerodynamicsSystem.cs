using Brutal.Numerics;
using FullThrust.Core.Ecs;
using FullThrust.Core.Parts;
using FullThrust.Core.Simulation;
using FullThrust.Core.Space;

namespace FullThrust.Core.Craft;

// Drag acts at each part, so where area sits relative to the centre of mass decides stability.
public sealed class AerodynamicsSystem : ISystem {

    // Cross-flow drag of a cylinder broadside to the wind.
    public double SideDragCoefficient { get; set; } = 1.2;

    public void Update(World world, in SimTime time) {

        var flights = world.Store<FlightState>();
        var masses = world.Store<CraftMass>();
        var forces = world.Store<CraftForces>();
        var transforms = world.Store<Transform>();
        var velocities = world.Store<Velocity>();

        foreach (var row in world.Query<Part>()) {

            ref readonly var part = ref row.C1;

            var craft = part.Craft;

            ref var flight = ref flights.Get(craft);

            if (flight.Mode != FlightMode.Physics || flight.Ambient.Density <= 0) {

                continue;

            }

            var rotation = transforms.Get(craft).Rotation;
            var center = masses.Get(craft).CenterOfMass;
            var lever = rotation * (part.LocalPosition - center);
            var air = flight.AirVelocity + double3.Cross(velocities.Get(craft).Angular, lever);
            var speed = air.Length();

            if (speed < 0.1) {

                continue;

            }

            var angular = velocities.Get(craft).Angular;
            var drag = PartForce(in part, rotation, center, air, speed, flight.Ambient.Density, flight.Mach, ref lever);

            ref var total = ref forces.Get(craft);

            total.AddAt(drag, lever);
            flight.Drag += drag.Length();

            if (part.Definition.HeatShield is { } shield) {

                total.Torque += PitchDamping(in part, shield, rotation, angular, speed, flight.Ambient.Density);

            }

        }

    }

    // The lever arrives at the part centre and leaves at the point the force acts through.
    public double3 PartForce(in Part part, doubleQuat craftRotation, double3 centerOfMass, double3 air, double speed, double density, double mach, ref double3 lever) {

        if (part.Definition.HeatShield is { } shield && part.Aero.BottomArea > 0.5 * part.Definition.FrontalArea) {

            var rotation = craftRotation * part.LocalRotation;
            var axis = rotation * double3.UnitY;

            if (double3.Dot(air, axis) < 0) {

                var focus = part.LocalPosition + part.LocalRotation * new double3(0, shield.CurvatureRadius - part.Definition.Length / 2, 0);

                lever = craftRotation * (focus - centerOfMass);

                return HeatShieldForce(shield, part.Aero.BottomArea, axis, air, speed, density);

            }

        }

        return DragForce(in part, craftRotation, air, speed, density, mach);

    }

    // Mostly axial, so the resultant tilts off the flow and the capsule trims where its line passes through the centre of mass.
    public static double3 HeatShieldForce(HeatShieldDefinition shield, double area, double3 axis, double3 air, double speed, double density) {

        var flow = air / speed;
        var cosine = -double3.Dot(flow, axis);
        var across = flow + axis * cosine;
        var q = 0.5 * density * speed * speed;

        return (axis * cosine - across * shield.NormalForce) * (q * area * shield.AxialDrag);

    }

    // Opposes pitch and yaw rates (Cmq); roll about the symmetry axis is left alone.
    public static double3 PitchDamping(in Part part, HeatShieldDefinition shield, doubleQuat craftRotation, double3 angular, double speed, double density) {

        var axis = craftRotation * (part.LocalRotation * double3.UnitY);
        var rate = angular - axis * double3.Dot(angular, axis);
        var diameter = 2 * part.Definition.Radius;

        return rate * (-0.25 * density * speed * part.Definition.FrontalArea * diameter * diameter * shield.PitchDamping);

    }

    public double3 DragForce(in Part part, doubleQuat craftRotation, double3 air, double speed, double density, double mach) {

        var axis = craftRotation * double3.UnitY;
        var cosine = double3.Dot(air, axis) / speed;

        // Moving nose-first means the top faces are the ones meeting the flow.
        var axialArea = cosine >= 0 ? part.Aero.TopArea : part.Aero.BottomArea;
        var cd = part.Definition.DragCoefficient;

        var area = cd * axialArea * Math.Abs(cosine) + SideDragCoefficient * part.Aero.SideArea * (1 - cosine * cosine);
        var q = 0.5 * density * speed * speed;

        return air * (-q * area * MachFactor(mach) / speed);

    }

    // Wave drag rises sharply through the transonic regime and eases off at high Mach.
    public static double MachFactor(double mach) {

        if (mach < 0.8) {

            return 1.0;

        }

        if (mach < 1.2) {

            return 1.0 + 1.2 * (mach - 0.8) / 0.4;

        }

        if (mach < 2.0) {

            return 2.2 - 0.6 * (mach - 1.2) / 0.8;

        }

        if (mach < 5.0) {

            return 1.6 - 0.4 * (mach - 2.0) / 3.0;

        }

        return 1.2;

    }

}
