using System;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Ocean;
using MaxQ.Sim.Surface;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Ocean;

public sealed class SeaStateTests {

    private const int June = 6;

    private Survey _survey;

    [OneTimeSetUp]
    public void Open() => _survey = Survey.Open("Data/Terra", 1_274_200.0);

    [OneTimeTearDown]
    public void Close() => _survey?.Dispose();

    private SeaState Climate {

        get {

            if (_survey == null) {

                Assert.Ignore("Terra's sea state is not baked; run tools/terra.sh");

            }

            return _survey.SeaState;

        }

    }

    private static Vector3d At(double latitudeDegrees, double longitudeDegrees) {

        double lat = latitudeDegrees * Math.PI / 180.0;
        double lon = longitudeDegrees * Math.PI / 180.0;

        return new Vector3d(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));

    }

    [Test]
    public void TheSouthernOceanInWinterIsRougherThanTheTrades() {

        SeaConditions south = Climate.At(At(-50.0, 100.0), June);
        SeaConditions trades = Climate.At(At(15.0, -40.0), June);

        Assert.That(south.WindSpeed, Is.GreaterThan(trades.WindSpeed));
        Assert.That(south.SeaHeight + south.SwellHeight, Is.GreaterThan(1.5 * (trades.SeaHeight + trades.SwellHeight)));
        Assert.That(south.SwellPeriod, Is.GreaterThan(11.0));

    }

    [Test]
    public void TradeWindsBlowWestward() {

        SeaConditions trades = Climate.At(At(15.0, -40.0), June);

        Assert.That(trades.WindEast, Is.LessThan(-4.0));
        Assert.That(trades.SeaEast, Is.LessThan(-0.7));

    }

    [Test]
    public void WesterliesBlowEastward() {

        Assert.That(Climate.At(At(-50.0, 100.0), June).WindEast, Is.GreaterThan(4.0));

    }

    [Test]
    public void TheArcticIsFrozenInJuneAndTheTropicsAreNot() {

        Assert.That(Climate.At(At(85.0, 0.0), June).Ice, Is.GreaterThan(0.8));
        Assert.That(Climate.At(At(0.0, -150.0), June).Ice, Is.EqualTo(0.0));

    }

    [Test]
    public void ConditionsAreContinuousAcrossTheDateLine() {

        foreach (double latitude in new[] { -60.0, -20.0, 0.0, 30.0, 55.0 }) {

            SeaConditions west = Climate.At(At(latitude, 179.999), June);
            SeaConditions east = Climate.At(At(latitude, -179.999), June);

            Assert.That(east.WindSpeed, Is.EqualTo(west.WindSpeed).Within(0.05), $"at {latitude}");
            Assert.That(east.SeaHeight, Is.EqualTo(west.SeaHeight).Within(0.02), $"at {latitude}");

        }

    }

}
