using System;
using System.Collections.Generic;
using System.Linq;

using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Orbits;
using MaxQ.Sim.Vessels;
using MaxQ.Sim.Vessels.Parts;
using MaxQ.Sim.Vessels.Propulsion;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Vessels;

public sealed class VesselTests {

    private static readonly Controls Ullage = new Controls(0.0, Vector3d.Zero, Vector3d.UnitZ);
    private static readonly Controls Full = new Controls(1.0, Vector3d.Zero, Vector3d.Zero);
    private static readonly Controls Pitch = new Controls(0.0, Vector3d.UnitX, Vector3d.Zero);

    // Drifting far out from a body too light to pull, so every change of velocity is the vessel's own doing.
    private static Vessel Drifting() {

        CelestialBody space = new CelestialBody("Void", 1.0, 1.0, 1e9);
        Orbit drift = Orbit.FromStateVectors(new Vector3d(1e7, 0.0, 0.0), new Vector3d(0.0, 1.0, 0.0), space.Mu, 0.0);

        return new Vessel("Test", ReferenceCraft.Build(), space, drift, 0.0, QuaternionD.Identity);

    }

    private static void Hold(Vessel vessel, double seconds, Controls controls) => vessel.Advance(vessel.Time + seconds, controls);

    private static (Vector3d Force, Vector3d Torque) Net(IReadOnlyList<Thruster> thrusters, double centre, double[] duties) {

        Vector3d force = Vector3d.Zero;
        Vector3d torque = Vector3d.Zero;

        for (int i = 0; i < thrusters.Count; i++) {

            Vector3d push = thrusters[i].Direction * (-thrusters[i].Thrust * duties[i]);

            force += push;
            torque += Vector3d.Cross(thrusters[i].Position - centre * Vector3d.UnitZ, push);

        }

        return (force, torque);

    }

    [Test]
    public void ColdStartFailsWithoutUllage() {

        Vessel vessel = Drifting();

        Hold(vessel, 2.0, Full);

        Engine engine = vessel.Engines[0];

        Assert.That(engine.StartFailed, Is.True);
        Assert.That(engine.Phase, Is.EqualTo(EnginePhase.Off));
        Assert.That(engine.Output, Is.EqualTo(0.0));

    }

    [Test]
    public void UllageLetsTheEngineLight() {

        Vessel vessel = Drifting();

        Hold(vessel, 5.0, Ullage);

        Assert.That(vessel.Tanks[0].Settled, Is.GreaterThanOrEqualTo(Engine.IgnitionSettled));

        Hold(vessel, 3.0, Full);

        Assert.That(vessel.Engines[0].Phase, Is.EqualTo(EnginePhase.Running));
        Assert.That(vessel.Engines[0].Chamber, Is.GreaterThan(0.95));

    }

    [Test]
    public void FailedStartWaitsForTheThrottleToBeCut() {

        Vessel vessel = Drifting();

        Hold(vessel, 2.0, Full);
        Hold(vessel, 5.0, new Controls(1.0, Vector3d.Zero, Vector3d.UnitZ));

        Assert.That(vessel.Engines[0].Phase, Is.EqualTo(EnginePhase.Off));

        Hold(vessel, 0.1, Ullage);
        Hold(vessel, 3.0, Full);

        Assert.That(vessel.Engines[0].Phase, Is.EqualTo(EnginePhase.Running));

    }

    [Test]
    public void FullBurnMatchesTheRocketEquation() {

        Vessel vessel = Drifting();
        Engine engine = vessel.Engines[0];

        Hold(vessel, 5.0, Ullage);

        double initialMass = vessel.MassProperties.Mass;
        Vector3d initialVelocity = vessel.State.Velocity;

        while (!engine.Flameout && vessel.Time < 1_000.0) {

            Hold(vessel, 10.0, Full);

        }

        Hold(vessel, 2.0, Full);

        double expected = engine.SpecificImpulse * Engine.StandardGravity * Math.Log(initialMass / vessel.MassProperties.Mass);
        double gained = Vector3d.Distance(vessel.State.Velocity, initialVelocity);

        Assert.That(engine.Flameout, Is.True);
        Assert.That(vessel.Tanks[0].Oxidiser + vessel.Tanks[0].Fuel, Is.LessThan(1.0));
        Assert.That(gained, Is.EqualTo(expected).Within(0.002 * expected));
        Assert.That(expected, Is.InRange(5_000.0, 8_000.0));

    }

    [Test]
    public void RcsPitchIsPureTorque() {

        Vessel vessel = Drifting();
        IReadOnlyList<Thruster> thrusters = vessel.Parts.OfType<RcsBlock>().Single().Thrusters;
        double[] duties = new double[thrusters.Count];

        RcsMixer.Allocate(thrusters, vessel.MassProperties.CentreOfMass, Vector3d.UnitX, Vector3d.Zero, duties);

        (Vector3d force, Vector3d torque) = Net(thrusters, vessel.MassProperties.CentreOfMass, duties);

        Assert.That(torque.X, Is.GreaterThan(0.0));
        Assert.That(Math.Abs(torque.Y) + Math.Abs(torque.Z), Is.LessThan(0.05 * torque.X));
        Assert.That(force.Length, Is.LessThan(0.05 * 4.0 * 445.0));

    }

    [Test]
    public void RcsAxialTranslationIsPureForce() {

        Vessel vessel = Drifting();
        IReadOnlyList<Thruster> thrusters = vessel.Parts.OfType<RcsBlock>().Single().Thrusters;
        double[] duties = new double[thrusters.Count];

        RcsMixer.Allocate(thrusters, vessel.MassProperties.CentreOfMass, Vector3d.Zero, Vector3d.UnitZ, duties);

        (Vector3d force, Vector3d torque) = Net(thrusters, vessel.MassProperties.CentreOfMass, duties);

        Assert.That(force.Z, Is.EqualTo(4.0 * 445.0).Within(0.05 * 4.0 * 445.0));
        Assert.That(Math.Abs(force.X) + Math.Abs(force.Y), Is.LessThan(0.05 * force.Z));
        Assert.That(torque.Length, Is.LessThan(50.0));

    }

    [Test]
    public void StageRcsFliesTheStackUntilSeparation() {

        Vessel vessel = Drifting();
        Capsule capsule = vessel.Parts.OfType<Capsule>().Single();
        RcsBlock stageRcs = vessel.Parts.OfType<RcsBlock>().Single();

        Hold(vessel, 2.0, Pitch);

        Assert.That(vessel.State.AngularVelocity.X, Is.GreaterThan(0.0));
        Assert.That(stageRcs.Hydrazine, Is.LessThan(stageRcs.HydrazineCapacity));
        Assert.That(capsule.Hydrazine, Is.EqualTo(capsule.HydrazineCapacity));

        vessel.Stage();
        Hold(vessel, 1.0, Pitch);

        Assert.That(capsule.Hydrazine, Is.LessThan(capsule.HydrazineCapacity));

    }

    [Test]
    public void GimbalPitchesTheStackUnderThrust() {

        Vessel vessel = Drifting();

        Hold(vessel, 5.0, Ullage);
        Hold(vessel, 3.0, new Controls(1.0, Vector3d.UnitX, Vector3d.Zero));

        Assert.That(vessel.Engines[0].Deflection.Y, Is.GreaterThan(0.0));
        Assert.That(vessel.State.AngularVelocity.X, Is.GreaterThan(0.0));

    }

    [Test]
    public void StagingConservesMomentumAndPushesApart() {

        Vessel vessel = Drifting();
        double mass = vessel.MassProperties.Mass;
        Vector3d momentum = vessel.State.Velocity * mass;

        Vessel spent = vessel.Stage();

        Vector3d after = vessel.State.Velocity * vessel.MassProperties.Mass + spent.State.Velocity * spent.MassProperties.Mass;
        double impulse = spent.Parts.OfType<Decoupler>().Single().Impulse;
        double parting = impulse / vessel.MassProperties.Mass + impulse / spent.MassProperties.Mass;

        Assert.That(vessel.Parts.Single(), Is.InstanceOf<Capsule>());
        Assert.That(spent.Engines, Has.Count.EqualTo(1));
        Assert.That(vessel.MassProperties.Mass + spent.MassProperties.Mass, Is.EqualTo(mass).Within(1e-6));
        Assert.That(Vector3d.Distance(after, momentum), Is.LessThan(1e-6 * mass));
        Assert.That(vessel.State.Velocity.Z - spent.State.Velocity.Z, Is.EqualTo(parting).Within(1e-9));

    }

    [Test]
    public void CoastingSpinPersistsThroughWarp() {

        Vessel vessel = Drifting();

        Hold(vessel, 2.0, new Controls(0.0, Vector3d.UnitZ, Vector3d.Zero));

        double spin = vessel.State.AngularVelocity.Z;

        Hold(vessel, 100_000.0, default);

        Assert.That(spin, Is.GreaterThan(0.0));
        Assert.That(vessel.Current, Is.Not.Null);
        Assert.That(vessel.State.AngularVelocity.Z, Is.EqualTo(spin).Within(1e-12));

    }

}
