using System;

using MaxQ.Sim.Orbits;
using MaxQ.Sim.Surface;

namespace MaxQ.Sim.Bodies;

/// <summary>The 1/5-scale Earth-Moon system: radii and distances divided by five, surface gravity kept real.</summary>
public static class SolarSystem {

    public const double TerraRadius = 1_274_200.0;
    public const double SeleneRadius = 347_420.0;

    /// <summary>Terra's air keeps Earth's scale height and pressure unscaled, as its sky does: it thins by e every 8 km and
    /// is taken as gone 100 km up.</summary>
    public const double AirScaleHeight = 8_000.0;
    public const double AirTop = 100_000.0;

    public static (CelestialBody Terra, CelestialBody Selene) Create(Terrain? terraTerrain = null, Terrain? seleneTerrain = null) {

        const double terraRadius = TerraRadius;
        const double seleneRadius = SeleneRadius;

        CelestialBody terra = new CelestialBody("Terra", 9.81 * terraRadius * terraRadius, terraRadius, 86_400.0, terrain: terraTerrain,
            air: new Air(101_325.0, AirScaleHeight, AirTop, 9.81));

        Orbit seleneOrbit = Orbit.FromElements(

            mu: terra.Mu,
            semiMajorAxis: 76_880_000.0,
            eccentricity: 0.0549,
            inclination: 5.145 * Math.PI / 180.0,
            ascendingNode: 0.0,
            argumentOfPeriapsis: 0.0,
            meanAnomalyAtEpoch: 0.0,
            epoch: 0.0

        );

        // Tidally locked: one rotation per orbit.
        CelestialBody selene = new CelestialBody("Selene", 1.625 * seleneRadius * seleneRadius, seleneRadius, seleneOrbit.Period, terra, seleneOrbit, seleneTerrain);

        return (terra, selene);

    }

}
