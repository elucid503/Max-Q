using System;
using System.Collections.Generic;

using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Orbits;
using MaxQ.Sim.Vessels;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Orbits;

public sealed class TrajectoryTests {

    private const double ParkingAltitude = 150_000.0;

    private CelestialBody _terra;
    private CelestialBody _selene;

    [SetUp]
    public void SetUp() => (_terra, _selene) = SolarSystem.Create();

    /// <summary>A Hohmann transfer from low Terra orbit whose apoapsis falls just short of Selene at arrival.</summary>
    private (Orbit Orbit, double Departure) Transfer(double arrival, double missDistance) {

        Vector3d target = _selene.PositionAt(arrival);
        Vector3d normal = _selene.Orbit.Normal;

        double rp = _terra.Radius + ParkingAltitude;
        double ra = target.Length - missDistance;
        double a = 0.5 * (rp + ra);

        double departure = arrival - Math.PI * Math.Sqrt(a * a * a / _terra.Mu);
        Vector3d perigee = -target.Normalized * rp;
        Vector3d velocity = Vector3d.Cross(normal, perigee).Normalized * Math.Sqrt(_terra.Mu * (2.0 / rp - 1.0 / a));

        return (Orbit.FromStateVectors(perigee, velocity, _terra.Mu, departure), departure);

    }

    private static void AssertContinuous(Patch from, Patch to) {

        double t = from.EndTime;

        Vector3d before = from.Body.PositionAt(t) + from.Orbit.StateAt(t).Position;
        Vector3d after = to.Body.PositionAt(t) + to.Orbit.StateAt(t).Position;
        Vector3d vBefore = from.Body.VelocityAt(t) + from.Orbit.StateAt(t).Velocity;
        Vector3d vAfter = to.Body.VelocityAt(t) + to.Orbit.StateAt(t).Velocity;

        Assert.That(Vector3d.Distance(before, after), Is.LessThan(1e-3));
        Assert.That(Vector3d.Distance(vBefore, vAfter), Is.LessThan(1e-6));

    }

    [Test]
    public void SeleneSoiIsLaplaceRadius() {

        Assert.That(_selene.SoiRadius, Is.EqualTo(13_245_000.0).Within(20_000.0));

    }

    [Test]
    public void TransferEncountersSeleneAndFliesBackOut() {

        (Orbit orbit, double departure) = Transfer(500_000.0, 3_000_000.0);

        List<Patch> patches = Trajectory.Predict(_terra, orbit, departure, 3);

        Assert.That(patches[0].End, Is.EqualTo(PatchEnd.Encounter));
        Assert.That(patches[0].NextBody, Is.SameAs(_selene));
        Assert.That(patches[1].Body, Is.SameAs(_selene));
        Assert.That(patches[1].Orbit.IsClosed, Is.False);
        Assert.That(patches[1].End, Is.EqualTo(PatchEnd.Escape));
        Assert.That(patches[2].Body, Is.SameAs(_terra));

        AssertContinuous(patches[0], patches[1]);
        AssertContinuous(patches[1], patches[2]);

    }

    [Test]
    public void DirectHitImpactsSelene() {

        (Orbit orbit, double departure) = Transfer(500_000.0, 0.0);

        List<Patch> patches = Trajectory.Predict(_terra, orbit, departure, 3);

        Assert.That(patches[1].Body, Is.SameAs(_selene));
        Assert.That(patches[1].End, Is.EqualTo(PatchEnd.Impact));

    }

    [Test]
    public void RetroBurnAtPeriapsisCaptures() {

        (Orbit orbit, double departure) = Transfer(500_000.0, 3_000_000.0);

        Patch flyby = Trajectory.Predict(_terra, orbit, departure, 2)[1];
        double periapsisTime = flyby.Orbit.TimeAtTrueAnomaly(0.0, flyby.StartTime).Value;

        double rp = flyby.Orbit.PeriapsisRadius;
        double circular = Math.Sqrt(_selene.Mu / rp);
        double speed = flyby.Orbit.StateAt(periapsisTime).Velocity.Length;

        List<Patch> planned = Trajectory.PredictWithManeuver(_terra, orbit, departure, new Maneuver(periapsisTime, circular - speed, 0.0, 0.0), 4);

        Assert.That(planned[1].End, Is.EqualTo(PatchEnd.Maneuver));
        Assert.That(planned[2].Body, Is.SameAs(_selene));
        Assert.That(planned[2].End, Is.EqualTo(PatchEnd.None));
        Assert.That(planned[2].Orbit.Eccentricity, Is.LessThan(1e-6));

    }

    [Test]
    public void VesselIsHandedToSeleneAsTimeAdvances() {

        (Orbit orbit, double departure) = Transfer(500_000.0, 3_000_000.0);

        Vessel vessel = new Vessel("Probe", _terra, orbit, departure);

        vessel.Advance(499_000.0);

        Assert.That(vessel.Body, Is.SameAs(_selene));
        Assert.That(Vector3d.Distance(vessel.PositionAt(499_000.0), _selene.PositionAt(499_000.0)), Is.LessThan(_selene.SoiRadius));

    }

}
