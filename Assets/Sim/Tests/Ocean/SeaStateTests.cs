using System;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Ocean;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Ocean;

public sealed class SeaStateTests {

    private const int January = 1;
    private const int June = 6;

    private static readonly SeaState Climate = new SeaState();

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
    public void TheDoldrumsAreCalmerThanTheTrades() {

        Assert.That(Climate.At(At(8.0, -30.0), June).WindSpeed, Is.LessThan(0.7 * Climate.At(At(20.0, -30.0), June).WindSpeed));

    }

    [Test]
    public void TradeWindsBlowWestwardAndWesterliesEastward() {

        Assert.That(Climate.At(At(15.0, -40.0), June).WindEast, Is.LessThan(-3.0));
        Assert.That(Climate.At(At(-15.0, -120.0), June).WindEast, Is.LessThan(-3.0));
        Assert.That(Climate.At(At(-50.0, 100.0), June).WindEast, Is.GreaterThan(4.0));

    }

    [Test]
    public void TheWinterHemisphereIsWindier() {

        Vector3d northAtlantic = At(50.0, -30.0);

        Assert.That(Climate.At(northAtlantic, January).WindSpeed, Is.GreaterThan(Climate.At(northAtlantic, June).WindSpeed));
        Assert.That(Climate.At(northAtlantic, January).SwellHeight, Is.GreaterThan(1.5 * Climate.At(northAtlantic, June).SwellHeight));

    }

    [Test]
    public void SouthernSwellRunsNorthAcrossTheTropics() {

        SeaConditions equator = Climate.At(At(0.0, -140.0), June);

        Assert.That(equator.SwellNorth, Is.GreaterThan(0.5));
        Assert.That(equator.SwellHeight, Is.GreaterThan(0.5));
        Assert.That(equator.SwellPeriod, Is.GreaterThan(13.0));

    }

    [Test]
    public void TheArcticIsFrozenInJuneAndTheTropicsAreNot() {

        Assert.That(Climate.At(At(85.0, 0.0), June).Ice, Is.GreaterThan(0.8));
        Assert.That(Climate.At(At(-75.0, 0.0), June).Ice, Is.GreaterThan(0.8));
        Assert.That(Climate.At(At(0.0, -150.0), June).Ice, Is.EqualTo(0.0));

    }

    [Test]
    public void ConditionsAreContinuous() {

        for (double latitude = -89.0; latitude <= 89.0; latitude += 0.25) {

            SeaConditions here = Climate.At(At(latitude, 179.999), June);
            SeaConditions next = Climate.At(At(latitude + 0.05, -179.999), June);

            Assert.That(next.WindSpeed, Is.EqualTo(here.WindSpeed).Within(0.1), $"at {latitude}");
            Assert.That(next.SeaHeight, Is.EqualTo(here.SeaHeight).Within(0.05), $"at {latitude}");
            Assert.That(next.SwellHeight, Is.EqualTo(here.SwellHeight).Within(0.05), $"at {latitude}");
            Assert.That(next.SwellEast * here.SwellEast + next.SwellNorth * here.SwellNorth, Is.GreaterThan(0.99), $"at {latitude}");

        }

    }

}
