using System;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Surface;

namespace MaxQ.Game.Planet.Ground;

/// <summary>What covers the ground at a place, from its climate: the share of it that grows plants, the share of those
/// that are forest, how arid it is, and how much lies under snow. The year's temperature falls with latitude and height
/// (a June day's for the snow, whose summer is the north's), the survey's rain belts bring the moisture, and the cooler
/// the summer the less of it plants need. Noise breaks up every border into woods and clearings. Patches bake it into their
/// cover texture for the ground's materials (Biome.hlsl), and the plants strewn on them read it here, so trees stand where
/// the ground is forest.</summary>
internal readonly struct Cover {

    public readonly float Vegetation;
    public readonly float Forest;
    public readonly float Arid;
    public readonly float Snow;

    /// <summary>How warm the year runs, 0 at -5 C to 1 at 25 C; Warmth in Biome.hlsl finds the same, less the noise.</summary>
    public readonly float Warmth;

    // Noise that shifts the climate's borders, over tens of kilometres, and lays out woods and clearings, from two
    // kilometres down to a hundred metres.
    private const double ClimateWavelength = 32_000.0;
    private const double WoodsWavelength = 2_000.0;
    private const int Octaves = 5;

    // Metres inland the beaches reach, narrow and wide, by the sea and by lakes; match Biome.hlsl.
    private const double SeaBeachNarrow = 12.0;
    private const double SeaBeachWide = 60.0;
    private const double LakeBeachNarrow = 4.0;
    private const double LakeBeachWide = 40.0;

    /// <summary>Metres: the shortest wavelength the cover varies over.</summary>
    public const double Finest = WoodsWavelength / (1 << (Octaves - 1));

    private Cover(double vegetation, double forest, double arid, double snow, double warmth) {

        Vegetation = (float)vegetation;
        Forest = (float)forest;
        Arid = (float)arid;
        Snow = (float)snow;
        Warmth = (float)warmth;

    }

    /// <summary>The cover at a unit body-fixed <paramref name="direction"/> whose ground stands <paramref name="height"/>
    /// metres over the reference radius, keeping no noise finer than <paramref name="footprint"/> metres.</summary>
    public static Cover At(Terrain terrain, Vector3d direction, double height, double footprint) {

        Vector3d position = direction * terrain.Radius;
        double climate = Fractal(position, ClimateWavelength, footprint, 31u);
        double woods = Fractal(position, WoodsWavelength, footprint, 41u);
        double sine = Math.Min(Math.Max(direction.Z, -1.0), 1.0);
        double altitude = Math.Max(height, 0.0) / Terrain.VerticalScale;

        double annual = -25.0 + 52.0 * Math.Sqrt(1.0 - sine * sine) - 0.0065 * altitude + 3.0 * climate;
        double summer = annual + 15.0 * Math.Abs(sine);
        double june = annual + 12.0 * sine;
        double warmth = Saturate((annual + 5.0) / 30.0);
        double humid = (terrain.MoistureAt(direction) + 0.08 * climate) / (0.1 + 0.9 * Saturate((summer - 5.0) / 25.0));

        double vegetation = Smooth(0.1, 0.3, humid) * Smooth(-2.0, 5.0, summer);
        double forest = Smooth(0.45, 0.8, humid + 0.3 * woods) * Smooth(7.0, 10.0, summer + 2.0 * woods);
        double arid = 1.0 - Smooth(0.05, 0.45, humid);
        double snow = Smooth(0.0, -4.0, june + 2.0 * woods);

        // Seen from afar the ground keeps the share of bare crag its finer relief would show close by, so ranges read as
        // rock and broken snow rather than as hills grassed or whitened to their crests.
        terrain.Crags(direction, footprint, out double bare, out double shed);

        vegetation *= 1.0 - bare;
        snow *= 1.0 - shed;

        return new Cover(vegetation, forest, arid, snow, warmth);

    }

    /// <summary>How warm the year runs at a unit body-fixed <paramref name="direction"/> whose ground stands
    /// <paramref name="height"/> metres up, as Warmth in Biome.hlsl finds it, without the noise.</summary>
    public static double WarmthAt(Vector3d direction, double height) {

        double sine = Math.Min(Math.Max(direction.Z, -1.0), 1.0);

        return Saturate((-20.0 + 52.0 * Math.Sqrt(1.0 - sine * sine) - 0.0065 * Math.Max(height, 0.0) / Terrain.VerticalScale) / 30.0);

    }

    /// <summary>Metres inland a beach reaches on a shore whose water stands <paramref name="level"/> metres up (lakes
    /// stand above the sea), under a climate; BeachWidth in Biome.hlsl finds the same.</summary>
    public static double BeachWidth(double level, double arid, double warmth) {

        double sandy = Saturate(Math.Max(arid, (warmth - 0.6) / 0.3));
        double sea = SeaBeachNarrow + (SeaBeachWide - SeaBeachNarrow) * sandy;
        double lake = LakeBeachNarrow + (LakeBeachWide - LakeBeachNarrow) * arid;

        return sea + (lake - sea) * Saturate(level / 5.0);

    }

    // Octaves of gradient noise in about [-1, 1], halving from wavelength, fading out as they near the footprint.
    private static double Fractal(Vector3d position, double wavelength, double footprint, uint seed) {

        double sum = 0.0;
        double amplitude = 0.5;

        for (int octave = 0; octave < Octaves; octave++) {

            double weight = footprint <= 0.0 ? 1.0 : Saturate(wavelength / (2.0 * footprint) - 1.0);
            Vector3d p = position / wavelength;

            sum += weight * amplitude * Relief.Noise(p.X, p.Y, p.Z, seed + (uint)octave);
            amplitude *= 0.5;
            wavelength *= 0.5;

        }

        return sum;

    }

    private static double Smooth(double from, double to, double x) {

        double t = Saturate((x - from) / (to - from));

        return t * t * (3.0 - 2.0 * t);

    }

    private static double Saturate(double x) => Math.Min(Math.Max(x, 0.0), 1.0);

}
