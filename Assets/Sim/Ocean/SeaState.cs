using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Ocean;

/// <summary>The wind and waves at a place in one month. Headings are (east, north) components of travel.</summary>
public readonly struct SeaConditions {

    /// <summary>Mean wind 10 m over the sea, m/s east and north; the wind's sea travels with it.</summary>
    public double WindEast { get; init; }

    public double WindNorth { get; init; }

    /// <summary>Mean wind speed, m/s; more than the mean wind's length, as the wind gusts and veers.</summary>
    public double WindSpeed { get; init; }

    /// <summary>Significant height (m) and peak period (s) of the sea the local wind raises.</summary>
    public double SeaHeight { get; init; }

    public double SeaPeriod { get; init; }

    /// <summary>Significant height (m), peak period (s) and unit heading of swell arriving from distant storms.</summary>
    public double SwellHeight { get; init; }

    public double SwellPeriod { get; init; }

    public double SwellEast { get; init; }

    public double SwellNorth { get; init; }

    /// <summary>Share of the sea covered by ice, 0 to 1.</summary>
    public double Ice { get; init; }

}

/// <summary>A plausible climatology of the open sea from the planet's circulation: trade winds either side of a
/// convergence that follows the sun, westerlies in the storm belts, polar easterlies, all stronger in the winter
/// hemisphere and varied by smooth noise over thousands of kilometres; swell spreading from the storm belts; sea ice by
/// latitude and season. Blittable, so Burst jobs read it too.</summary>
public readonly struct SeaState {

    private const double Degree = Math.PI / 180.0;

    // Peak winds of each belt, m/s, before the season strengthens them in winter by these shares.
    private const double Trades = 7.0;
    private const double NorthernWesterlies = 6.0;
    private const double SouthernWesterlies = 8.0;
    private const double PolarEasterlies = 4.0;
    private const double TradesWinter = 0.12;
    private const double WesterliesWinter = 0.25;

    // Storm belts' swell, m, and its period there, s; far from them its period lengthens by up to SwellAging as the
    // longer waves outrun the rest.
    private const double SouthernSwell = 3.0;
    private const double NorthernSwell = 2.0;
    private const double SouthernPeriod = 13.0;
    private const double NorthernPeriod = 11.0;
    private const double SwellAging = 3.0;

    // Noise lattice cells per unit of direction: features some two thousand kilometres across on the Earth.
    private const double NoiseScale = 3.0;

    /// <summary>Conditions at a unit body-fixed direction in <paramref name="month"/> (1 to 12), over open water.</summary>
    public SeaConditions At(Vector3d direction, int month) {

        double latitude = Math.Asin(Math.Min(Math.Max(direction.Z, -1.0), 1.0));
        double hemisphere = latitude >= 0.0 ? 1.0 : -1.0;
        double season = Math.Cos(2.0 * Math.PI * (month - 7.5) / 12.0);
        double winter = -season * Math.Tanh(latitude / (10.0 * Degree));

        // The trades converge on a line between the equator and ten degrees north; the higher belts follow the sun less.
        double tropical = latitude - (5.0 + 5.0 * season) * Degree;
        double polar = Math.Abs(latitude - 3.0 * Degree * season);
        double trades = Trades * (1.0 + TradesWinter * winter) * Bump(Math.Abs(tropical), 13.0, 10.0);
        double westerlies = (hemisphere > 0.0 ? NorthernWesterlies : SouthernWesterlies) * (1.0 + WesterliesWinter * winter) *
            Bump(polar, hemisphere > 0.0 ? 48.0 : 50.0, 10.0);
        double easterlies = PolarEasterlies * Bump(polar, 72.0, 7.0);
        double gusts = 2.0 + 4.0 * Bump(polar, 50.0, 15.0);

        double east = westerlies - trades - easterlies;
        double north = -0.5 * trades * Math.Tanh(tropical / (3.0 * Degree)) + hemisphere * (0.2 * westerlies - 0.3 * easterlies);
        double strength = 1.0 + 0.25 * Noise(direction, 1);
        double veer = 0.4 * Noise(direction, 2);
        double windEast = (east * Math.Cos(veer) - north * Math.Sin(veer)) * strength;
        double windNorth = (east * Math.Sin(veer) + north * Math.Cos(veer)) * strength;
        double windSpeed = Math.Sqrt(windEast * windEast + windNorth * windNorth + gusts * gusts * strength * strength);

        double southern = Swell(latitude, -52.0 * Degree, SouthernSwell * (1.0 + 0.25 * season), SouthernPeriod, out double southernPeriod,
            out double southernEast, out double southernNorth);
        double northern = Swell(latitude, 50.0 * Degree, NorthernSwell * (1.0 - 0.6 * season), NorthernPeriod, out double northernPeriod,
            out double northernEast, out double northernNorth);
        double energy = Math.Max(southern + northern, 1e-12);
        double swellVeer = 0.25 * Noise(direction, 3);
        double headingEast = southern * southernEast + northern * northernEast;
        double headingNorth = southern * southernNorth + northern * northernNorth;
        double heading = Math.Max(Math.Sqrt(headingEast * headingEast + headingNorth * headingNorth), 1e-12);

        // Pack ice reaches furthest toward the equator in late winter; its edge wanders by a few degrees.
        double edge = hemisphere > 0.0 ? 72.0 - 8.0 * Math.Cos(2.0 * Math.PI * (month - 3) / 12.0) : 66.0 - 6.0 * Math.Cos(2.0 * Math.PI * (month - 9) / 12.0);
        double ice = Smooth((Math.Abs(latitude) - edge * Degree + 3.0 * Degree * Noise(direction, 4)) / (6.0 * Degree));

        double seaHeight = Spectrum.WindSea(windSpeed, double.PositiveInfinity, out double seaPeriod);

        return new SeaConditions {

            WindEast = windEast,
            WindNorth = windNorth,
            WindSpeed = windSpeed,
            SeaHeight = seaHeight,
            SeaPeriod = seaPeriod,
            SwellHeight = Math.Sqrt(southern + northern) * (1.0 + 0.2 * Noise(direction, 5)),
            SwellPeriod = (southern * southernPeriod + northern * northernPeriod) / energy,
            SwellEast = (headingEast * Math.Cos(swellVeer) - headingNorth * Math.Sin(swellVeer)) / heading,
            SwellNorth = (headingEast * Math.Sin(swellVeer) + headingNorth * Math.Cos(swellVeer)) / heading,
            Ice = ice,

        };

    }

    // Swell energy (m^2) a storm belt at latitude belt raising height metres sends to latitude: fading over fifty degrees
    // toward the equator and on past it, over five poleward into the ice. It travels east with the storms and away from
    // the belt, its period lengthening with distance.
    private static double Swell(double latitude, double belt, double height, double period, out double arriving, out double east, out double north) {

        double distance = Math.Abs(latitude - belt);
        double reach = (latitude * belt > 0.0 && Math.Abs(latitude) > Math.Abs(belt) ? 5.0 : 50.0) * Degree;
        double away = Math.Tanh((latitude - belt) / (8.0 * Degree));
        double length = Math.Sqrt(0.25 + 0.75 * away * away);

        arriving = period + SwellAging * (1.0 - Math.Exp(-distance / (30.0 * Degree)));
        east = 0.5 / length;
        north = 0.866 * away / length;

        double h = height * Math.Exp(-distance / reach);

        return h * h;

    }

    // A Gaussian bump in latitude (radians) about centre, of width sigma (both degrees).
    private static double Bump(double latitude, double centre, double sigma) {

        double x = (latitude - centre * Degree) / (sigma * Degree);

        return Math.Exp(-0.5 * x * x);

    }

    private static double Smooth(double x) {

        double t = Math.Min(Math.Max(x, 0.0), 1.0);

        return t * t * (3.0 - 2.0 * t);

    }

    // Smooth noise over the sphere, -1 to 1, in two octaves; each seed its own.
    private static double Noise(Vector3d direction, int seed) {

        Vector3d p = direction * NoiseScale;

        return 1.3 * Value(p, seed) + 0.7 * Value(p * 2.03, seed + 16) - 1.0;

    }

    // Value noise on the integer lattice, 0 to 1.
    private static double Value(Vector3d p, int seed) {

        double fx = Math.Floor(p.X);
        double fy = Math.Floor(p.Y);
        double fz = Math.Floor(p.Z);
        int x = (int)fx;
        int y = (int)fy;
        int z = (int)fz;
        double tx = Smooth(p.X - fx);
        double ty = Smooth(p.Y - fy);
        double tz = Smooth(p.Z - fz);

        double Edge(int j, int k) => Hash(x, y + j, z + k, seed) + (Hash(x + 1, y + j, z + k, seed) - Hash(x, y + j, z + k, seed)) * tx;
        double Face(int k) => Edge(0, k) + (Edge(1, k) - Edge(0, k)) * ty;

        return Face(0) + (Face(1) - Face(0)) * tz;

    }

    // A number in [0, 1) fixed by a lattice point and seed.
    private static double Hash(int x, int y, int z, int seed) {

        uint h = (uint)(x * 374_761_393 + y * 668_265_263 + z * 1_440_662_683 + seed * 144_665_591);

        h = (h ^ (h >> 13)) * 1_274_126_177u;
        h ^= h >> 16;

        return h / 4_294_967_296.0;

    }

}
