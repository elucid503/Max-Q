using MaxQ.Sim.Numerics;
using MaxQ.Sim.Vessels.Motion;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Vessels;

public sealed class RigidBodyTests {

    private const double Axial = 2.0;
    private const double Transverse = 5.0;

    [Test]
    public void TwoPointMassesCentreBetweenThem() {

        MassProperties sum = default(MassProperties).Add(1.0, -1.0, 0.0, 0.0).Add(1.0, 1.0, 0.0, 0.0);

        Assert.That(sum.CentreOfMass, Is.EqualTo(0.0).Within(1e-12));
        Assert.That(sum.Transverse, Is.EqualTo(2.0).Within(1e-12));

    }

    [Test]
    public void ExactCoastMatchesIntegratedTumble() {

        Vector3d spin = new Vector3d(0.3, -0.2, 1.0);
        RigidState state = new RigidState(new Vector3d(1e9, 0.0, 0.0), Vector3d.Zero, QuaternionD.Identity, spin);

        for (int i = 0; i < 10_000; i++) {

            state = RigidBody.Step(state, 0.0, 1.0, Axial, Transverse, Vector3d.Zero, Vector3d.Zero, 1e-3);

        }

        (QuaternionD attitude, Vector3d coastSpin) = RigidBody.Coast(QuaternionD.Identity, spin, Axial, Transverse, 10.0);

        foreach (Vector3d axis in new[] { Vector3d.UnitX, Vector3d.UnitZ }) {

            Assert.That(Vector3d.Distance(attitude.Rotate(axis), state.Attitude.Rotate(axis)), Is.LessThan(1e-8));

        }

        Assert.That(Vector3d.Distance(coastSpin, state.AngularVelocity), Is.LessThan(1e-8));

    }

    [Test]
    public void TorqueSpinsUpAboutItsAxis() {

        RigidState state = new RigidState(new Vector3d(1e9, 0.0, 0.0), Vector3d.Zero, QuaternionD.Identity, Vector3d.Zero);

        for (int i = 0; i < 100; i++) {

            state = RigidBody.Step(state, 0.0, 1.0, Axial, Transverse, Vector3d.Zero, new Vector3d(0.0, 0.0, 4.0), 0.01);

        }

        Assert.That(state.AngularVelocity.Z, Is.EqualTo(2.0).Within(1e-9));

    }

}
