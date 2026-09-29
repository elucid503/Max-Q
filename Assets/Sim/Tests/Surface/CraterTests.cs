using System;

using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Surface;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Surface;

public sealed class CraterTests {

    [Test]
    public void SeleneStandsOnLolaHeightsWithMariaAndNoWater() {

        using Survey survey = Survey.Selene("Data/Selene", SolarSystem.SeleneRadius);

        if (survey == null) {

            Assert.Ignore("Selene's survey is not baked; run tools/selene.sh");

        }

        Terrain selene = survey.Terrain;
        Vector3d hadley = At(26.13, 3.63);

        // LOLA puts the Apollo 15 site 1.9 km under the reference radius; a fifth of that, give or take the craters.
        Assert.That(selene.HeightAt(hadley, 0.0), Is.InRange(-382.0 - Craters.Deepest, -382.0 + 60.0));
        selene.RegolithAt(At(28.0, 17.5), 0.0, out double serenitatis, out _);
        selene.RegolithAt(At(-20.0, 10.0), 0.0, out double highland, out _);
        selene.RegolithAt(At(-43.3, -11.2), 0.0, out _, out double tycho);

        Assert.That(serenitatis, Is.GreaterThan(0.9));
        Assert.That(highland, Is.LessThan(0.1));
        Assert.That(tycho, Is.GreaterThan(0.3));
        Assert.That(selene.WaterLevelAt(hadley, 0.0), Is.NaN);

    }

    private static Vector3d At(double latitudeDegrees, double longitudeDegrees) {

        double lat = latitudeDegrees * Math.PI / 180.0;
        double lon = longitudeDegrees * Math.PI / 180.0;

        return new Vector3d(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));

    }

    [Test]
    public void FreshBowlsSinkBelowSharpRims() {

        const double radius = 100.0;

        Assert.That(Craters.Profile(0.0, radius, 0.0), Is.EqualTo(-40.0).Within(1e-9));
        Assert.That(Craters.Profile(1.0, radius, 0.0), Is.EqualTo(8.0).Within(1e-9));
        Assert.That(Craters.Profile(2.2, radius, 0.0), Is.EqualTo(0.0).Within(1e-9));

    }

    [Test]
    public void AgeWearsCratersShallow() {

        Assert.That(Math.Abs(Craters.Profile(0.0, 100.0, 1.0)), Is.LessThan(0.2 * Math.Abs(Craters.Profile(0.0, 100.0, 0.0))));

    }

    [Test]
    public void CratersPitTheGroundAndCoarseFootprintsSmoothThemAway() {

        Vector3d start = new Vector3d(0.3, 0.5, 0.8).Normalized * SolarSystem.SeleneRadius;
        Vector3d east = Vector3d.Cross(Vector3d.UnitZ, start).Normalized;
        double lowest = 0.0;
        double highest = 0.0;
        double coarsest = 0.0;

        for (int i = 0; i < 4_000; i++) {

            Vector3d position = (start + east * (i * 2.0)).Normalized * SolarSystem.SeleneRadius;
            double height = Craters.Detail(position, 0.0, 0.0, out double freshness);

            Assert.That(freshness, Is.InRange(0.0, 1.0));

            lowest = Math.Min(lowest, height);
            highest = Math.Max(highest, height);
            coarsest = Math.Max(coarsest, Math.Abs(Craters.Detail(position, 2_000.0, 0.0, out _)));

        }

        Assert.That(lowest, Is.LessThan(-5.0));
        Assert.That(highest - lowest, Is.LessThan(Craters.Deepest + 60.0));
        Assert.That(coarsest, Is.EqualTo(0.0));

    }

}
