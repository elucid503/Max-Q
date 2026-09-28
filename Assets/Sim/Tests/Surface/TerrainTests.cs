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

        Assert.That(Terra.WaterLevelAt(direction, 0.0), Is.EqualTo(level).Within(1.2));
        Assert.That(Terra.HeightAt(direction, 0.0), Is.LessThan(Terra.WaterLevelAt(direction, 0.0)));

    }

    [Test]
    public void DryLandHasNoWater() {

        Assert.That(Terra.WaterLevelAt(At(23.0, 12.0), 0.0), Is.NaN);

    }

    // Real-world surface levels (m) along rivers, from their gauges; the bake takes them from the survey's valleys.
    [TestCase("Rhine at Basel", 47.560, 7.590, 230.0, 262.0)]
    [TestCase("Rhine at Mainz", 50.000, 8.270, 70.0, 95.0)]
    [TestCase("Rhine at Cologne", 50.940, 6.965, 30.0, 55.0)]
    [TestCase("Mississippi at St Louis", 38.630, -90.180, 105.0, 140.0)]
    [TestCase("Amazon at Manaus", -3.140, -59.900, 5.0, 40.0)]
    public void RiversRunAtTheirValleyLevels(string river, double latitude, double longitude, double low, double high) {

        RiverPoint point = NearestRiver(latitude, longitude);

        Assert.That(point.Level, Is.InRange(low, high), river);
        Assert.That(Terra.WaterLevelAt(point.Centre, 0.0), Is.EqualTo(point.Level * Terrain.VerticalScale).Within(1.0), river);
        Assert.That(Terra.HeightAt(point.Centre, 0.0), Is.LessThan(Terra.WaterLevelAt(point.Centre, 0.0)), river);

    }

    [Test]
    public void RiversNeverRiseDownstream() {

        double basel = NearestRiver(47.560, 7.590).Level;
        double mainz = NearestRiver(50.000, 8.270).Level;
        double cologne = NearestRiver(50.940, 6.965).Level;

        Assert.That(mainz, Is.LessThan(basel));
        Assert.That(cologne, Is.LessThan(mainz));

    }

    [Test]
    public void RiversFlowDownstreamMidChannel() {

        RiverPoint point = NearestRiver(50.940, 6.965);
        Vector3d flow = Terra.FlowAt(point.Centre);

        Assert.That(flow.Length, Is.GreaterThan(0.5));
        Assert.That(Vector3d.Dot(flow.Normalized, point.Downstream), Is.GreaterThan(0.99));

    }

    [Test]
    public void RiversRunFastWhereTheyFallSteeply() {

        RiverPoint pool = new RiverPoint { Speed = 1.2, Depth = 0.8, Fall = 0.0 };
        RiverPoint drop = pool with { Fall = 0.01 / Terra.HorizontalScale };

        // Four real metres deep falling one in a hundred: Manning's law puts the current near seven metres a second.
        Assert.That(Terra.RiverSpeed(pool), Is.EqualTo(1.2));
        Assert.That(Terra.RiverSpeed(drop), Is.InRange(6.5, 8.0));

        // The lowland Rhine falls too gently to run faster than its reach's mean.
        RiverPoint cologne = NearestRiver(50.940, 6.965);

        Assert.That(Terra.RiverSpeed(cologne), Is.LessThan(1.3 * cologne.Speed));

    }

    [Test]
    public void ShoreDistanceIsNegativeOffshoreAndPositiveInland() {

        Assert.That(Terra.ShoreDistanceAt(At(0.0, -150.0)), Is.LessThan(-20_000.0));
        Assert.That(Terra.ShoreDistanceAt(At(23.0, 12.0)), Is.GreaterThan(20_000.0));
        Assert.That(Terra.ShoreDistanceAt(At(38.7, -9.6)), Is.LessThan(0.0));
        Assert.That(Terra.ShoreDistanceAt(At(40.4, -3.7)), Is.GreaterThan(0.0));

    }

    [Test]
    public void FetchFollowsTheOpenWater() {

        Assert.That(Terra.FetchAt(At(-55.0, 0.0), 1.5 * Math.PI), Is.GreaterThan(500_000.0));
        Assert.That(Terra.FetchAt(At(23.0, 12.0), 0.0), Is.EqualTo(0.0));

        // Lake Geneva runs east-west: long fetch along it, short across it, and nothing like the open sea.
        Vector3d geneva = At(46.45, 6.5);

        Assert.That(Terra.FetchAt(geneva, 0.5 * Math.PI), Is.InRange(8_000.0, 100_000.0));
        Assert.That(Terra.FetchAt(geneva, 0.0), Is.LessThan(Terra.FetchAt(geneva, 0.5 * Math.PI)));

    }

    // The biggest river by a town, found by searching a few kilometres around it, and the point nearest its centreline.
    private RiverPoint NearestRiver(double latitude, double longitude) {

        RiverPoint best = default;
        bool found = false;

        for (int i = -20; i <= 20; i++) {

            for (int j = -20; j <= 20; j++) {

                if (Terra.RiverAt(At(latitude + i * 0.002, longitude + j * 0.002), out RiverPoint point) &&
                    (!found || point.HalfWidth > best.HalfWidth || (point.HalfWidth == best.HalfWidth && point.Distance < best.Distance))) {

                    best = point;
                    found = true;

                }

            }

        }

        Assert.That(found, Is.True, $"no river near {latitude}, {longitude}");

        return best;

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

    // Walks the ground in half-metre steps across a shore or a river bank: no step may rise more than a cliff band's face
    // would, so the mesh never samples a wall. Low shores, where the old clamp stood a ledge at the waterline.
    [TestCase("Lake Geneva, Morges", 46.505, 6.49, 46.50, 6.50)]
    [TestCase("Lake Balaton, Siofok", 46.915, 18.04, 46.895, 18.06)]
    [TestCase("North Sea, Zandvoort", 52.37, 4.50, 52.37, 4.55)]
    [TestCase("Rhine at Mainz", 49.995, 8.26, 50.005, 8.28)]
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
