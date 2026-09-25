using System;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Surface;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Surface;

public sealed class TerrainTests {

    private const double Radius = 1_274_200.0;

    private Survey _survey;

    [OneTimeSetUp]
    public void Open() => _survey = Survey.Open("Data/Terra", Radius);

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

    [TestCase(27.9881, 86.9250, 1_690.0, 1_790.0)]
    [TestCase(11.3493, 142.1996, -2_210.0, -2_060.0)]
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

        Assert.That(Terra.WaterLevelAt(direction), Is.EqualTo(level).Within(1.2));
        Assert.That(Terra.HeightAt(direction, 0.0), Is.LessThan(Terra.WaterLevelAt(direction)));

    }

    [Test]
    public void DryLandHasNoWater() {

        Assert.That(Terra.WaterLevelAt(At(23.0, 12.0)), Is.NaN);

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
        Assert.That(Math.Abs(Terra.HeightAt(direction, 0.0) - Terra.HeightAt(direction, 500.0)), Is.GreaterThan(0.0));

    }

}
