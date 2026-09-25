using System;

using MaxQ.Sim.Orbits;

namespace MaxQ.Sim.Bodies;

/// <summary>The 1/5-scale Earth-Moon system: radii and distances divided by five, surface gravity kept real.</summary>
public static class SolarSystem {

    public static (CelestialBody Terra, CelestialBody Selene) Create() {

        const double terraRadius = 1_274_200.0;
        const double seleneRadius = 347_420.0;

        CelestialBody terra = new CelestialBody("Terra", 9.81 * terraRadius * terraRadius, terraRadius, 86_400.0);

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
        CelestialBody selene = new CelestialBody("Selene", 1.625 * seleneRadius * seleneRadius, seleneRadius, seleneOrbit.Period, terra, seleneOrbit);

        return (terra, selene);

    }

}
