using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Surface;

/// <summary>Procedural ground detail below the survey's resolution, scaled by how rough the survey says the land is.</summary>
public static class Relief {

    // The survey carries everything above ~185 m; detail starts just below that and halves down to a metre.
    private const double LongestWavelength = 256.0;
    private const int Octaves = 9;

    // Amplitude per octave; 0.58 is a Hurst exponent near 0.78, which is what measured relief shows.
    private const double Gain = 0.58;

    // Amplitude over wavelength at the longest octave: a floor for plains plus a share of the survey slope.
    private const double FlatRatio = 0.0006;
    private const double SlopeRatio = 0.18;

    // Slopes over which ground goes from rolling to ridged.
    private const double RidgeStartSlope = 0.04;
    private const double RidgeFullSlope = 0.25;

    /// <summary>Detail height in metres at body-fixed <paramref name="position"/>, keeping only wavelengths the
    /// <paramref name="footprint"/> (sample spacing, metres) can carry; a zero footprint keeps them all.</summary>
    public static double Detail(Vector3d position, double footprint, double slope) {

        double amplitude = LongestWavelength * (FlatRatio + SlopeRatio * slope);
        double ridged = Saturate((slope - RidgeStartSlope) / (RidgeFullSlope - RidgeStartSlope));

        double wavelength = LongestWavelength;
        double sum = 0.0;

        for (int octave = 0; octave < Octaves; octave++) {

            double weight = footprint <= 0.0 ? 1.0 : Saturate(wavelength / footprint - 1.0);

            if (weight <= 0.0) {

                break;

            }

            double offset = octave * 31.7;
            double n = Noise(position.X / wavelength + offset, position.Y / wavelength - offset, position.Z / wavelength + offset * 0.5, (uint)octave);
            double ridge = 1.0 - Math.Abs(n);

            sum += weight * amplitude * (n + ridged * (2.0 * ridge * ridge - 0.9 - n));

            amplitude *= Gain;
            wavelength *= 0.5;

        }

        return sum;

    }

    /// <summary>Gradient noise in roughly [-1, 1], continuous in value and slope.</summary>
    public static double Noise(double x, double y, double z, uint seed) {

        double fx = Math.Floor(x);
        double fy = Math.Floor(y);
        double fz = Math.Floor(z);

        int ix = (int)fx;
        int iy = (int)fy;
        int iz = (int)fz;

        double u = x - fx;
        double v = y - fy;
        double w = z - fz;

        double su = Fade(u);
        double sv = Fade(v);
        double sw = Fade(w);

        double x00 = Lerp(Gradient(Hash(ix, iy, iz, seed), u, v, w), Gradient(Hash(ix + 1, iy, iz, seed), u - 1.0, v, w), su);
        double x10 = Lerp(Gradient(Hash(ix, iy + 1, iz, seed), u, v - 1.0, w), Gradient(Hash(ix + 1, iy + 1, iz, seed), u - 1.0, v - 1.0, w), su);
        double x01 = Lerp(Gradient(Hash(ix, iy, iz + 1, seed), u, v, w - 1.0), Gradient(Hash(ix + 1, iy, iz + 1, seed), u - 1.0, v, w - 1.0), su);
        double x11 = Lerp(Gradient(Hash(ix, iy + 1, iz + 1, seed), u, v - 1.0, w - 1.0), Gradient(Hash(ix + 1, iy + 1, iz + 1, seed), u - 1.0, v - 1.0, w - 1.0), su);

        return Lerp(Lerp(x00, x10, sv), Lerp(x01, x11, sv), sw);

    }

    internal static double Saturate(double x) => Math.Min(Math.Max(x, 0.0), 1.0);

    private static double Fade(double t) => t * t * t * (t * (t * 6.0 - 15.0) + 10.0);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static uint Hash(int x, int y, int z, uint seed) {

        uint h = seed * 0x9E3779B9u;

        h ^= (uint)x * 0x8DA6B343u;
        h ^= (uint)y * 0xD8163841u;
        h ^= (uint)z * 0xCB1AB31Fu;
        h = (h ^ (h >> 16)) * 0x7FEB352Du;
        h = (h ^ (h >> 15)) * 0x846CA68Bu;

        return h ^ (h >> 16);

    }

    // Perlin's twelve edge gradients, four repeated to make sixteen.
    private static double Gradient(uint hash, double x, double y, double z) => (hash & 15u) switch {

        0u => x + y,
        1u => -x + y,
        2u => x - y,
        3u => -x - y,
        4u => x + z,
        5u => -x + z,
        6u => x - z,
        7u => -x - z,
        8u => y + z,
        9u => -y + z,
        10u => y - z,
        11u => -y - z,
        12u => x + y,
        13u => -x + y,
        14u => -y + z,
        _ => -y - z,

    };

}
