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
        Vector3d uphill = Vector3d.Cross(p.Normalized, Vector3d.UnitZ).Normalized;

        double flat = 0.0;
        double steep = 0.0;

        for (int i = 0; i < 200; i++) {

            Vector3d q = p + new Vector3d(i * 37.0, i * 11.0, 0.0);

            flat += Math.Abs(Relief.Detail(q, 0.0, Vector3d.Zero));
            steep += Math.Abs(Relief.Detail(q, 0.0, uphill * 0.4));

        }

        Assert.That(steep, Is.GreaterThan(flat * 20.0));
        Assert.That(Relief.Detail(p, 600.0, uphill * 0.4), Is.EqualTo(0.0));

    }

    [Test]
    public void DetailIsContinuousWhereverTheSlopeTurns() {

        Random random = new Random(11);
        Vector3d p = new Vector3d(612_345.6, -998_765.4, 402_020.2);
        Vector3d up = p.Normalized;
        Vector3d east = Vector3d.Cross(Vector3d.UnitZ, up).Normalized;
        Vector3d north = Vector3d.Cross(up, east);

        for (int i = 0; i < 2_000; i++) {

            // Slopes from level through steep, pointing every way, including through zero where gullies change direction.
            double angle = random.NextDouble() * 2.0 * Math.PI;
            double slope = random.NextDouble() * random.NextDouble() * 0.6;
            Vector3d gradient = (east * Math.Cos(angle) + north * Math.Sin(angle)) * slope;
            Vector3d q = p + east * (random.NextDouble() * 5_000.0) + north * (random.NextDouble() * 5_000.0);
            Vector3d nudge = east * 1e-4;

            double here = Relief.Detail(q, 0.0, gradient);

            Assert.That(Relief.Detail(q + nudge, 0.0, gradient), Is.EqualTo(here).Within(0.02));
            Assert.That(Relief.Detail(q, 0.0, gradient + east * 1e-5), Is.EqualTo(here).Within(0.02));

        }

    }

    [Test]
    public void GulliesRunDownhill() {

        Vector3d p = new Vector3d(612_345.6, -998_765.4, 402_020.2) / 64.0;
        Vector3d up = p.Normalized;
        Vector3d downhill = Vector3d.Cross(Vector3d.UnitZ, up).Normalized;
        Vector3d across = Vector3d.Cross(up, downhill);

        double along = 0.0;
        double sideways = 0.0;

        for (int i = 0; i < 500; i++) {

            Vector3d q = p + across * (i * 0.37);
            double here = Relief.Gullies(q, across, 1u, out _);

            along += Math.Abs(Relief.Gullies(q + downhill * 0.1, across, 1u, out _) - here);
            sideways += Math.Abs(Relief.Gullies(q + across * 0.1, across, 1u, out _) - here);

        }

        Assert.That(sideways, Is.GreaterThan(along * 3.0));

    }

    [Test]
    public void StrataCutCliffsIntoSteepGroundOnly() {

        Vector3d p = new Vector3d(612_345.6, -998_765.4, 402_020.2);
        Vector3d along = Vector3d.Cross(p.Normalized, Vector3d.UnitZ).Normalized;
        double steepest = 0.0;
        double previous = Relief.Strata(p, 0.0, 0.7, 0.0);

        // Up a uniform 0.7 slope, one metre at a time: the bands steepen it into cliffs between gentler benches.
        for (int i = 1; i < 2_000; i++) {

            double height = Relief.Strata(p + along * i, 0.7 * i, 0.7, 0.0);

            steepest = Math.Max(steepest, height - previous);
            previous = height;

        }

        Assert.That(steepest, Is.GreaterThan(0.7 * 2.0));
        Assert.That(Relief.Strata(p, 123.4, 0.1, 0.0), Is.EqualTo(123.4));
        Assert.That(Relief.Strata(p, 123.4, 0.7, 500.0), Is.EqualTo(123.4));

    }

    [Test]
    public void StrataAreContinuous() {

        Random random = new Random(5);
        Vector3d p = new Vector3d(612_345.6, -998_765.4, 402_020.2);

        for (int i = 0; i < 5_000; i++) {

            Vector3d q = p + new Vector3d(random.NextDouble(), random.NextDouble(), random.NextDouble()) * 5_000.0;
            double height = random.NextDouble() * 2_000.0;
            double slope = random.NextDouble() * 1.2;
            double here = Relief.Strata(q, height, slope, 0.0);

            Assert.That(Relief.Strata(q, height + 1e-6, slope, 0.0), Is.EqualTo(here).Within(1e-3));
            Assert.That(Relief.Strata(q + new Vector3d(1e-6, 0.0, 0.0), height, slope, 0.0), Is.EqualTo(here).Within(1e-3));
            Assert.That(Relief.Strata(q, height, slope + 1e-6, 0.0), Is.EqualTo(here).Within(1e-3));

        }

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
