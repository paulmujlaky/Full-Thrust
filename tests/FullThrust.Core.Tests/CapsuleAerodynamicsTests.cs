using Brutal.Numerics;
using FullThrust.Core.Craft;
using FullThrust.Core.Orbits;
using FullThrust.Core.Parts;
using FullThrust.Core.Space;

namespace FullThrust.Core.Tests;

public class CapsuleAerodynamicsTests {

    [Fact]
    public void CapsuleTrimsShieldFirstAtAModestAngle() {

        var catalog = TestData.Parts();
        var pod = catalog["pod-concord"];
        var part = new Part {

            Definition = pod,
            LocalRotation = doubleQuat.Identity,

            Aero = new PartAero { TopArea = pod.FrontalArea, BottomArea = pod.FrontalArea, SideArea = 2 * pod.Radius * pod.Length },

        };

        var center = CraftAssembly.DryCenter(in part);
        var aero = new AerodynamicsSystem();
        var trim = double.NaN;
        var previous = 0.0;

        for (var degrees = -40; degrees <= 40; degrees++) {

            var moment = PitchMoment(aero, in part, center, degrees);

            if (degrees > -40 && Math.Sign(moment) != Math.Sign(previous)) {

                // Restoring means the moment turns the shield towards the flow, so it rises through trim.
                Assert.True(moment > previous);
                trim = degrees;

            }

            previous = moment;

        }

        Assert.InRange(Math.Abs(trim), 4, 25);

        // Far off trim the moment still drives the shield back into the flow.
        Assert.True(PitchMoment(aero, in part, center, 80) > 0);
        Assert.True(PitchMoment(aero, in part, center, -80) < 0);

    }

    [Fact]
    public void TumblingCapsuleSettlesShieldFirstDuringEntry() {

        using var harness = new FlightHarness();

        var earth = harness.Earth;
        var state = OrbitScenarios.Entry(earth.GravitationalParameter, earth.Radius + 90_000, 7_600, -1.5 * Math.PI / 180);
        var preset = harness.Rocket("concord-return");

        // Nose-first is the wrong way round for a capsule.
        harness.Focus.Craft = CraftPlacement.InOrbit(harness.System, earth, state, 0, Attitude.Prograde).Spawn(harness.World, preset);

        var craft = harness.Focus.Craft;

        // Well into the dense atmosphere, past the worst of the heating.
        harness.Run(280);

        var least = 180.0;
        var worst = 0.0;

        for (var i = 0; i < 20; i++) {

            harness.Run(0.25);

            ref readonly var flight = ref harness.Get<FlightState>(craft);

            var nose = harness.Get<Transform>(craft).Rotation * double3.UnitY;
            var flow = double3.Normalize(flight.AirVelocity);
            var angle = Math.Acos(Math.Clamp(-double3.Dot(nose, flow), -1, 1)) * 180 / Math.PI;

            least = Math.Min(least, angle);
            worst = Math.Max(worst, angle);

        }

        // Settled at a lifting trim, not pinned straight into the flow.
        Assert.InRange(least, 3, 25);
        Assert.InRange(worst, 3, 25);

    }

    // Pitch moment about the centre of mass with the flow tilted by the given angle off the shield axis, positive about +Z.
    private static double PitchMoment(AerodynamicsSystem aero, in Part part, double3 center, double degrees) {

        var alpha = degrees * Math.PI / 180;
        var air = new double3(Math.Sin(alpha), -Math.Cos(alpha), 0) * 2000;
        var lever = part.LocalPosition - center;
        var force = aero.PartForce(in part, doubleQuat.Identity, center, air, 2000, 0.01, 6, ref lever);

        return double3.Cross(lever, force).Z;

    }

}
