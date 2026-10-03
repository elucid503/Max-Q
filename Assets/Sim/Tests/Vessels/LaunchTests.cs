using System;
using System.Collections.Generic;
using System.Linq;

using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Orbits;
using MaxQ.Sim.Vessels;
using MaxQ.Sim.Vessels.Motion;
using MaxQ.Sim.Vessels.Parts;
using MaxQ.Sim.Vessels.Propulsion;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Vessels;

public sealed class LaunchTests {

    private static readonly Controls Full = new Controls(1.0, Vector3d.Zero, Vector3d.Zero, Hold.Attitude);

    private CelestialBody _terra;

    [SetUp]
    public void SetUp() => (_terra, _) = SolarSystem.Create();

    // A two-stage stack in round numbers: seven sea-level engines under a long tank, an interstage round a nested vacuum
    // engine, and the reference upper stage and capsule.
    private static List<Part> Launcher() {

        List<Vector3d> nozzles = new List<Vector3d> { Vector3d.Zero };

        for (int i = 0; i < 6; i++) {

            nozzles.Add(new Vector3d(1.4 * Math.Cos(i * Math.PI / 3.0), 1.4 * Math.Sin(i * Math.PI / 3.0), 0.0));

        }

        Engine first = new Engine {

            Name = "Sea-level engine",
            Nozzles = nozzles,
            Thrust = 914_000.0,
            SpecificImpulse = 311.0,
            MixtureRatio = 2.36,
            MinimumThrottle = 0.4,
            IgnitionDelaySeconds = 0.5,
            SpoolUpSeconds = 0.5,
            SpoolDownSeconds = 0.25,
            GimbalRange = 5.0 * Math.PI / 180.0,
            GimbalRate = 20.0 * Math.PI / 180.0,
            Mass = 470.0,
            Length = 2.9,
            ExitRadius = 0.4656,
            CentreOfMassHeight = 1.8,

        };

        Engine second = ReferenceCraft.Engine();

        return new List<Part> {

            first,
            new Tank { Name = "First stage tank", Radius = 1.85, BarrelLength = 26.0, DomeRatio = 0.7, MixtureRatio = 2.36, OxidiserDensity = 1_141.0,
                FuelDensity = 820.0, WallArealDensity = 16.0 },
            new Skirt { Name = "Interstage", BottomRadius = 1.85, TopRadius = 1.85, Length = 6.0, ArealDensity = 18.0 },
            new Decoupler { Name = "Stage separation", Radius = 1.85, Length = 0.15, Mass = 250.0, Impulse = 30_000.0 },
            new Engine {

                Name = second.Name, Nested = true, Thrust = second.Thrust, SpecificImpulse = second.SpecificImpulse, MixtureRatio = second.MixtureRatio,
                MinimumThrottle = second.MinimumThrottle, IgnitionDelaySeconds = second.IgnitionDelaySeconds, SpoolUpSeconds = second.SpoolUpSeconds,
                SpoolDownSeconds = second.SpoolDownSeconds, GimbalRange = second.GimbalRange, GimbalRate = second.GimbalRate, Mass = second.Mass,
                Length = second.Length, ExitRadius = second.ExitRadius, CentreOfMassHeight = second.CentreOfMassHeight,

            },
            ReferenceCraft.Tank(),
            ReferenceCraft.StageRcs(),
            new Skirt { Name = "Adapter", BottomRadius = 1.85, TopRadius = 1.85, Length = 0.5, ArealDensity = 12.0 },
            new Decoupler { Name = "Separation ring", Radius = 1.85, Length = 0.15, Mass = 120.0, Impulse = 2_000.0 },
            ReferenceCraft.Capsule(),

        };

    }

    // On a pad on the equator at the prime meridian, nose up, body X south and Y east.
    private Vessel OnPad() => new Vessel("Launcher", Launcher(), _terra, 0.0, Vector3d.UnitX * (_terra.Radius + 10.0),
        QuaternionD.FromBasis(-Vector3d.UnitZ, Vector3d.UnitY, Vector3d.UnitX));

    private static void Fly(Vessel vessel, double seconds, Controls controls) => vessel.Advance(vessel.Time + seconds, controls);

    private static Vector3d Nose(Vessel vessel) => vessel.State.Attitude.Rotate(Vector3d.UnitZ);

    [Test]
    public void AirThinsByEveryScaleHeight() {

        Air air = _terra.Air;

        Assert.That(air.PressureAt(0.0), Is.EqualTo(101_325.0));
        Assert.That(air.PressureAt(air.ScaleHeight) / air.PressureAt(0.0), Is.EqualTo(Math.Exp(-1.0)).Within(1e-12));
        Assert.That(air.DensityAt(0.0), Is.EqualTo(1.29).Within(0.02));
        Assert.That(air.SpeedOfSound, Is.EqualTo(331.0).Within(2.0));
        Assert.That(air.PressureAt(air.Top + 1.0), Is.EqualTo(0.0));

    }

    [Test]
    public void ConicsEndWhereTheyMeetTheAir() {

        Vector3d position = new Vector3d(_terra.Radius + 200_000.0, 0.0, 0.0);
        Orbit falling = Orbit.FromStateVectors(position, new Vector3d(-100.0, 1_000.0, 0.0), _terra.Mu, 0.0);
        Patch patch = Trajectory.NextPatch(_terra, falling, 0.0);

        Assert.That(patch.End, Is.EqualTo(PatchEnd.Atmosphere));
        Assert.That(patch.Orbit.StateAt(patch.EndTime).Position.Length, Is.EqualTo(_terra.Radius + _terra.Air.Top).Within(1.0));

    }

    [Test]
    public void NestedEngineHangsInsideTheInterstage() {

        Vessel vessel = OnPad();
        Skirt interstage = vessel.Parts.OfType<Skirt>().First();
        Engine nested = vessel.Engines[1];

        Assert.That(nested.Station, Is.GreaterThan(interstage.Station));
        Assert.That(nested.Station + nested.Length, Is.EqualTo(interstage.Station + interstage.Length + 0.15).Within(1e-9));

    }

    [Test]
    public void ClampsHoldUntilTheEnginesCanLift() {

        Vessel vessel = OnPad();
        Vector3d start = _terra.ToBodyFixed(vessel.State.Position, vessel.Time);

        Fly(vessel, 100.0, default);
        Fly(vessel, 0.6, Full);

        Assert.That(vessel.IsHeld, Is.True);
        Assert.That(vessel.Engines[0].Output, Is.LessThan(vessel.MassProperties.Mass * _terra.SurfaceGravity));
        Assert.That(Vector3d.Distance(_terra.ToBodyFixed(vessel.State.Position, vessel.Time), start), Is.LessThan(0.01));
        Assert.That(Vector3d.Distance(vessel.State.Velocity, _terra.AirVelocityAt(vessel.State.Position)), Is.LessThan(1e-9));

        Fly(vessel, 5.0, Full);

        Assert.That(vessel.IsHeld, Is.False);
        Assert.That(vessel.HasImpacted, Is.False);
        Assert.That(vessel.Engines[1].Phase, Is.EqualTo(EnginePhase.Off));
        Assert.That(vessel.Tanks[1].Fill, Is.EqualTo(1.0));
        Assert.That(vessel.Altitude, Is.GreaterThan(start.Length - _terra.Radius + 5.0));

    }

    [Test]
    public void LowThrottleNeverLetsGo() {

        Vessel vessel = OnPad();

        Fly(vessel, 10.0, new Controls(0.4, Vector3d.Zero, Vector3d.Zero));

        Assert.That(vessel.Engines[0].Phase, Is.EqualTo(EnginePhase.Running));
        Assert.That(vessel.IsHeld, Is.True);

    }

    [Test]
    public void TheAirTakesThrustAtTheExit() {

        Vessel vessel = OnPad();

        Fly(vessel, 3.0, new Controls(1.0, Vector3d.Zero, Vector3d.Zero));

        Engine engine = vessel.Engines[0];
        double vacuum = engine.Count * engine.Chamber * engine.Thrust;

        Assert.That(engine.Output, Is.EqualTo(vacuum - engine.Count * engine.AmbientPressure * engine.ExitArea).Within(1e-6 * vacuum));
        Assert.That(engine.MassFlow, Is.EqualTo(vacuum / (engine.SpecificImpulse * Engine.StandardGravity)).Within(1e-9 * vacuum));

    }

    [Test]
    public void StabilityAssistHoldsTheClimbThroughTheAir() {

        Vessel vessel = OnPad();

        Fly(vessel, 60.0, Full);

        Vector3d up = vessel.State.Position.Normalized;

        Assert.That(vessel.IsHeld, Is.False);
        Assert.That(vessel.DynamicPressure, Is.GreaterThan(1_000.0));
        Assert.That(Math.Acos(Math.Min(Vector3d.Dot(Nose(vessel), up), 1.0)), Is.LessThan(2.0 * Math.PI / 180.0));
        Assert.That(vessel.State.AngularVelocity.Length, Is.LessThan(0.01));

    }

    [Test]
    public void OuterEnginesRollTheStack() {

        Vessel vessel = OnPad();

        Fly(vessel, 5.0, Full);
        Fly(vessel, 2.0, new Controls(1.0, Vector3d.UnitZ, Vector3d.Zero));

        Engine cluster = vessel.Engines[0];
        Vector3d outer = cluster.Nozzles[1];

        Assert.That(cluster.Deflections[0].Length, Is.LessThan(1e-12));
        Assert.That(Vector3d.Dot(cluster.Deflections[1], new Vector3d(-outer.Y, outer.X, 0.0)), Is.GreaterThan(0.0));
        Assert.That(vessel.State.AngularVelocity.Z, Is.GreaterThan(0.01));

    }

    [Test]
    public void DragSlowsAFallThroughTheAir() {

        CelestialBody airless = new CelestialBody("Airless", _terra.Mu, _terra.Radius, _terra.RotationPeriodSeconds);

        Vessel Fall(CelestialBody body) {

            Vector3d position = new Vector3d(body.Radius + 30_000.0, 0.0, 0.0);
            Orbit falling = Orbit.FromStateVectors(position, new Vector3d(-1_000.0, 500.0, 0.0), body.Mu, 0.0);
            Vessel vessel = new Vessel("Falling", Launcher(), body, falling, 0.0, QuaternionD.FromBasis(Vector3d.UnitZ, Vector3d.UnitY, -Vector3d.UnitX));

            Fly(vessel, 10.0, default);

            return vessel;

        }

        Vessel through = Fall(_terra);
        Vessel past = Fall(airless);

        Assert.That(through.Current, Is.Null);
        Assert.That(through.DynamicPressure, Is.GreaterThan(10_000.0));
        Assert.That(through.State.Velocity.Length, Is.LessThan(past.State.Velocity.Length - 2.0));

    }

    [Test]
    public void CrossflowDampsATurn() {

        (Vector3d _, Vector3d torque) = Aerodynamics.Forces(new Vector3d(0.0, 0.0, 300.0), new Vector3d(0.1, 0.0, 0.0), 1.0, 330.0, 10.0, 0.0, 40.0, 2.0);

        Assert.That(torque.X, Is.LessThan(0.0));

    }

}
