using System;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Surface;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Surface;

public sealed class TerrainTests {

    private const double Radius = 1_274_200.0;

    private Survey _survey;

    [OneTimeSetUp]
    public void Open() => _survey = Survey.Terra("Data/Terra", Radius);

    [OneTimeTearDown]
    public void Close() => _survey?.Dispose();

    private Terrain Terra {

        get {

            if (_survey == null) {

                Assert.Ignore("Terra's survey is not baked; run tools/terra.sh");

            }

            return _survey.Terrain;

        }

    }

    private static Vector3d At(double latitudeDegrees, double longitudeDegrees) {

        double lat = latitudeDegrees * Math.PI / 180.0;
        double lon = longitudeDegrees * Math.PI / 180.0;

        return new Vector3d(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));

    }

    [TestCase(27.9881, 86.9250, 1_000.0, 2_000.0)]
    [TestCase(11.3493, 142.1996, -2_250.0, -1_600.0)]
    [TestCase(23.0, 12.0, 60.0, 300.0)]
    public void HeightsMatchTheRealWorldAtOneFifth(double latitude, double longitude, double low, double high) {

        Assert.That(Terra.HeightAt(At(latitude, longitude), 0.0), Is.InRange(low, high));

    }

    [TestCase(0.0, -150.0, 0.0)]
    [TestCase(42.0, 50.5, -28.0 * Terrain.VerticalScale)]
    [TestCase(-15.9, -69.4, 3_812.0 * Terrain.VerticalScale)]
    [TestCase(41.8, -87.0, 176.0 * Terrain.VerticalScale)]
    public void WaterStandsAtItsSurfaceLevel(double latitude, double longitude, double level) {

        Vector3d direction = At(latitude, longitude);

        Assert.That(Terra.WaterLevelAt(direction, 0.0), Is.EqualTo(level).Within(1.2));
        Assert.That(Terra.HeightAt(direction, 0.0), Is.LessThan(Terra.WaterLevelAt(direction, 0.0)));

    }

    [Test]
    public void DryLandHasNoWater() {

        Assert.That(Terra.WaterLevelAt(At(23.0, 12.0), 0.0), Is.NaN);

    }

    [Test]
    public void ShoreDistanceIsNegativeOffshoreAndPositiveInland() {

        Assert.That(Terra.ShoreDistanceAt(At(0.0, -150.0)), Is.LessThan(-20_000.0));
        Assert.That(Terra.ShoreDistanceAt(At(23.0, 12.0)), Is.GreaterThan(20_000.0));
        Assert.That(Terra.ShoreDistanceAt(At(38.7, -9.6)), Is.LessThan(0.0));
        Assert.That(Terra.ShoreDistanceAt(At(40.4, -3.7)), Is.GreaterThan(0.0));

    }

    [Test]
    public void MoistureFollowsTheRainBelts() {

        Assert.That(Terra.MoistureAt(At(0.0, 20.0)), Is.GreaterThan(0.7), "Congo");
        Assert.That(Terra.MoistureAt(At(-3.0, -60.0)), Is.GreaterThan(0.6), "Amazon");
        Assert.That(Terra.MoistureAt(At(48.8, 2.3)), Is.GreaterThan(0.4), "Paris");
        Assert.That(Terra.MoistureAt(At(23.0, 12.0)), Is.LessThan(0.2), "Sahara");
        Assert.That(Terra.MoistureAt(At(-25.0, 133.0)), Is.LessThan(0.25), "Australia");

    }

    [TestCase(0.0)]
    [TestCase(1_000.0)]
    public void GroundIsContinuousAcrossTheDateLineAndPoles(double footprint) {

        foreach (double latitude in new[] { -70.0, -12.5, 0.0, 33.3, 65.0 }) {

            double west = Terra.HeightAt(At(latitude, 179.999_999), footprint);
            double east = Terra.HeightAt(At(latitude, -179.999_999), footprint);

            Assert.That(east, Is.EqualTo(west).Within(0.05), $"date line at {latitude}");

        }

        foreach (double pole in new[] { 89.999_999, -89.999_999 }) {

            Assert.That(Terra.HeightAt(At(pole, 0.0), footprint), Is.EqualTo(Terra.HeightAt(At(pole, 180.0), footprint)).Within(0.05), $"pole {pole}");

        }

    }

    // Walks the ground in half-metre steps across low shores: no step may rise more than the gentle ground would, so the
    // coast's wander and the relief's fade toward the waterline never stand a wall.
    [TestCase("Lake Geneva, Morges", 46.505, 6.49, 46.50, 6.50)]
    [TestCase("Lake Balaton, Siofok", 46.915, 18.04, 46.895, 18.06)]
    [TestCase("North Sea, Zandvoort", 52.37, 4.50, 52.37, 4.55)]
    public void GroundIsContinuousAcrossShores(string place, double latitude0, double longitude0, double latitude1, double longitude1) {

        Vector3d from = At(latitude0, longitude0);
        Vector3d to = At(latitude1, longitude1);
        int steps = (int)((to - from).Length * Terra.Radius / 0.5);
        double previous = Terra.HeightAt(from, 0.0);
        double worst = 0.0;

        for (int i = 1; i <= steps; i++) {

            double height = Terra.HeightAt((from + (to - from) * ((double)i / steps)).Normalized, 0.0);

            worst = Math.Max(worst, Math.Abs(height - previous));
            previous = height;

        }

        Assert.That(worst, Is.LessThan(0.25), place);

    }

    [Test]
    public void WaterLevelsReachPastTheirShores() {

        // Around a lake's shore the level holds, so the sheet reaches into the bank and the ground crosses it there.
        Vector3d lake = At(46.45, 6.55);
        double level = Terra.WaterLevelAt(lake, 0.0);

        for (int i = 0; i <= 40; i++) {

            Vector3d north = At(46.45 + i * 0.002, 6.55);

            Assert.That(Terra.WaterLevelAt(north, 0.0), Is.EqualTo(level), $"step {i}");

        }

        Assert.That(Terra.HeightAt(At(46.53, 6.55), 0.0), Is.GreaterThan(level));

    }

    [Test]
    public void GroundIsContinuousOnTheSmallestScale() {

        Vector3d a = At(46.55, 7.98);
        Vector3d b = (a + new Vector3d(1e-9, 0.0, 0.0)).Normalized;

        Assert.That(Terra.HeightAt(b, 0.0), Is.EqualTo(Terra.HeightAt(a, 0.0)).Within(0.01));

    }

    [Test]
    public void CoarseFootprintsDropTheRelief() {

        Vector3d direction = At(46.55, 7.98);

        Assert.That(Terra.HeightAt(direction, 1_000.0), Is.EqualTo(Terra.HeightAt(direction, 1_000.0)));
        Assert.That(Math.Abs(Terra.HeightAt(direction, 0.0) - Terra.HeightAt(direction, 5_000.0)), Is.GreaterThan(0.0));

    }

}
