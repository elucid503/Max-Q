using System;

using MaxQ.Sim.Numerics;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Numerics;

public sealed class QuaternionDTests {

    [Test]
    public void QuarterTurnAboutZTakesXToY() {

        Vector3d turned = QuaternionD.AxisAngle(Vector3d.UnitZ, 0.5 * Math.PI).Rotate(Vector3d.UnitX);

        Assert.That(Vector3d.Distance(turned, Vector3d.UnitY), Is.LessThan(1e-12));

    }

    [Test]
    public void ProductAppliesRightThenLeft() {

        QuaternionD aboutZ = QuaternionD.AxisAngle(Vector3d.UnitZ, 0.5 * Math.PI);
        QuaternionD aboutX = QuaternionD.AxisAngle(Vector3d.UnitX, 0.5 * Math.PI);

        // X turns to Y about Z, then Y turns to Z about X.
        Vector3d turned = (aboutX * aboutZ).Rotate(Vector3d.UnitX);

        Assert.That(Vector3d.Distance(turned, Vector3d.UnitZ), Is.LessThan(1e-12));

    }

    [Test]
    public void InverseRotateUndoesRotate() {

        QuaternionD q = QuaternionD.FromRotationVector(new Vector3d(0.3, -1.1, 0.7));
        Vector3d v = new Vector3d(2.0, -5.0, 1.5);

        Assert.That(Vector3d.Distance(q.InverseRotate(q.Rotate(v)), v), Is.LessThan(1e-12));

    }

}
