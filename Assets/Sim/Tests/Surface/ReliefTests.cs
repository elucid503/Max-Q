using System;

using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Surface;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Surface;

public sealed class ReliefTests {

    [Test]
    public void NoiseIsBoundedAndContinuous() {

        Random random = new Random(7);

        for (int i = 0; i < 10_000; i++) {

            double x = random.NextDouble() * 2e6 - 1e6;
            double y = random.NextDouble() * 2e6 - 1e6;
            double z = random.NextDouble() * 2e6 - 1e6;
            double n = Relief.Noise(x, y, z, 3u);

            Assert.That(n, Is.InRange(-1.5, 1.5));
            Assert.That(Relief.Noise(x + 1e-7, y, z, 3u), Is.EqualTo(n).Within(1e-5));

        }

    }

    [Test]
    public void DetailGrowsWithSlopeAndFadesWithFootprint() {

        Vector3d p = new Vector3d(612_345.6, -998_765.4, 402_020.2);

        double flat = 0.0;
        double steep = 0.0;

        for (int i = 0; i < 200; i++) {

            Vector3d q = p + new Vector3d(i * 37.0, i * 11.0, 0.0);

            flat += Math.Abs(Relief.Detail(q, 0.0, 0.0));
            steep += Math.Abs(Relief.Detail(q, 0.0, 0.4));

        }

        Assert.That(steep, Is.GreaterThan(flat * 20.0));
        Assert.That(Relief.Detail(p, 600.0, 0.4), Is.EqualTo(0.0));

    }

    [Test]
    public void CubeFacesAreOrthonormalAndCoverTheirQuadrant() {

        for (int face = 0; face < 6; face++) {

            Vector3d n = CubeFace.Normal(face);
            Vector3d r = CubeFace.Right(face);
            Vector3d u = CubeFace.Up(face);

            Assert.That(Vector3d.Dot(n, r), Is.Zero);
            Assert.That(Vector3d.Dot(n, u), Is.Zero);
            Assert.That(Vector3d.Dot(r, u), Is.Zero);
            Assert.That(Vector3d.Distance(CubeFace.Direction(face, 0.0, 0.0), n), Is.LessThan(1e-15));
            Assert.That(Vector3d.Dot(CubeFace.Direction(face, 1.0, 1.0), (n + r + u).Normalized), Is.EqualTo(1.0).Within(1e-12));

        }

    }

    [Test]
    public void BodyFixedFrameRoundTrips() {

        (CelestialBody terra, CelestialBody selene) = SolarSystem.Create();
        Vector3d v = new Vector3d(1.0, 2.0, 3.0);

        foreach (CelestialBody body in new[] { terra, selene }) {

            Vector3d back = body.FromBodyFixed(body.ToBodyFixed(v, 12_345.0), 12_345.0);

            Assert.That(Vector3d.Distance(back, v), Is.LessThan(1e-12));

        }

    }

}
