using System;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Orbits;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Orbits;

public sealed class OrbitTests {

    private const double Mu = 1.5927e13;

    private static void AssertClose(Vector3d expected, Vector3d actual, double tolerance) {

        Assert.That(Vector3d.Distance(expected, actual), Is.LessThan(tolerance), $"expected {expected}, got {actual}");

    }

    [TestCase(1_500_000.0, 0.0, 0.0, 3_300.0, 0.0)]
    [TestCase(1_500_000.0, 0.0, 0.0, 3_700.0, 900.0)]
    [TestCase(1_400_000.0, 200_000.0, -50_000.0, 1_200.0, 4_500.0)]
    [TestCase(1_400_000.0, 0.0, 0.0, 0.0, 6_000.0)]
    public void StateVectorsRoundTrip(double x, double y, double z, double vx, double vy) {

        Vector3d r = new Vector3d(x, y, z);
        Vector3d v = new Vector3d(vx, vy, 150.0);

        (Vector3d r2, Vector3d v2) = Orbit.FromStateVectors(r, v, Mu, 100.0).StateAt(100.0);

        AssertClose(r, r2, 1e-4);
        AssertClose(v, v2, 1e-7);

    }

    [TestCase(3_700.0)]
    [TestCase(6_000.0)]
    public void EnergyAndMomentumAreConserved(double speed) {

        Vector3d r0 = new Vector3d(1_400_000.0, 0.0, 0.0);
        Vector3d v0 = new Vector3d(300.0, speed, 800.0);
        Orbit orbit = Orbit.FromStateVectors(r0, v0, Mu, 0.0);

        double energy = v0.LengthSquared / 2.0 - Mu / r0.Length;
        Vector3d h = Vector3d.Cross(r0, v0);

        foreach (double t in new[] { 37.0, 1_234.5, 98_765.0 }) {

            (Vector3d r, Vector3d v) = orbit.StateAt(t);

            Assert.That(v.LengthSquared / 2.0 - Mu / r.Length, Is.EqualTo(energy).Within(Math.Abs(energy) * 1e-10));
            AssertClose(h, Vector3d.Cross(r, v), h.Length * 1e-10);

        }

    }

    [Test]
    public void TimeAtTrueAnomalyMatchesPropagation() {

        Orbit orbit = Orbit.FromStateVectors(new Vector3d(1_400_000.0, 0.0, 0.0), new Vector3d(0.0, 3_900.0, 0.0), Mu, 0.0);

        double t = orbit.TimeAtTrueAnomaly(2.0, 0.0).Value;

        Assert.That(orbit.TrueAnomalyAt(t), Is.EqualTo(2.0).Within(1e-9));

    }

}
