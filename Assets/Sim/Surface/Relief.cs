using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Surface;

/// <summary>Procedural ground detail below the survey's resolution: gullies that run down the survey's slopes and branch
/// off one another, over gently rolling ground whose roughness follows how steep the survey says the land is.</summary>
public static class Relief {

    // The survey carries everything above ~185 m; detail starts just below that and halves down to a metre.
    private const double LongestWavelength = 256.0;
    private const int Octaves = 9;

    // Gullies are cut at the longer octaves only; below a few metres rolling noise alone roughens the ground.
    private const int GullyOctaves = 7;

    // Amplitude per octave; 0.58 is a Hurst exponent near 0.78, which is what measured relief shows.
    private const double Gain = 0.58;

    // Amplitude over wavelength at the longest octave: a floor for plains plus a share of the survey slope, split
    // between rolling ground and gullies.
    private const double FlatRatio = 0.0006;
    private const double SlopeRatio = 0.18;
    private const double RollingShare = 0.35;

    // Slope below which gullies widen out and fade, so their direction never jumps where the ground levels off.
    private const double GullySlope = 0.08;

    // How strongly each octave's gullies turn the next octave's, so small gullies branch off the walls of large ones.
    private const double Branching = 1.5;

    // Gully cells: points jittered this far from each cell's centre, blended within this radius (in cells). With these
    // values the 27 cells around a point always include every one within reach, and at least one.
    private const double Jitter = 0.15;
    private const double Reach = 1.25;

    /// <summary>Detail height in metres at body-fixed <paramref name="position"/>, keeping only wavelengths the
    /// <paramref name="footprint"/> (sample spacing, metres) can carry; a zero footprint keeps them all.
    /// <paramref name="gradient"/> is the survey's uphill slope there, rise over run along the ground.</summary>
    public static double Detail(Vector3d position, double footprint, Vector3d gradient) {

        Vector3d up = position.Normalized;
        double slope = gradient.Length;
        double rolling = LongestWavelength * (FlatRatio + RollingShare * SlopeRatio * slope);
        double eroded = LongestWavelength * (1.0 - RollingShare) * SlopeRatio * slope;

        double wavelength = LongestWavelength;
        double sum = 0.0;

        for (int octave = 0; octave < Octaves; octave++) {

            double weight = footprint <= 0.0 ? 1.0 : Saturate(wavelength / footprint - 1.0);

            if (weight <= 0.0) {

                break;

            }

            Vector3d p = position / wavelength;
            double offset = octave * 31.7;

            sum += weight * rolling * Noise(p.X + offset, p.Y - offset, p.Z + offset * 0.5, (uint)octave);

            if (octave < GullyOctaves && eroded > 0.0) {

                Vector3d across = Vector3d.Cross(up, gradient) / Math.Max(gradient.Length, GullySlope);
                double gully = Gullies(p, across, (uint)octave, out Vector3d change);

                sum += weight * eroded * gully;
                gradient += change * (weight * eroded * Branching / wavelength);

            }

            rolling *= Gain;
            eroded *= Gain;
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

    /// <summary>Gullies in [-1, 1] at <paramref name="p"/> (in cells): each jittered cell point carries ridges and furrows
    /// across <paramref name="across"/>, one per cell at full length, and neighbouring cells blend, breaking the furrows
    /// into gullies of a cell or two. <paramref name="change"/> is the height's rate of change per cell.</summary>
    public static double Gullies(Vector3d p, Vector3d across, uint seed, out Vector3d change) {

        int ix = (int)Math.Floor(p.X);
        int iy = (int)Math.Floor(p.Y);
        int iz = (int)Math.Floor(p.Z);

        double reach2 = Reach * Reach;
        double total = 0.0;
        double sum = 0.0;
        double slope = 0.0;

        for (int k = -1; k <= 1; k++) {

            for (int j = -1; j <= 1; j++) {

                for (int i = -1; i <= 1; i++) {

                    uint hash = Hash(ix + i, iy + j, iz + k, seed ^ 0x5BD1E995u);
                    double ox = p.X - (ix + i + 0.5 + Offset(hash));
                    double oy = p.Y - (iy + j + 0.5 + Offset(hash >> 10));
                    double oz = p.Z - (iz + k + 0.5 + Offset(hash >> 20));
                    double d2 = ox * ox + oy * oy + oz * oz;

                    if (d2 >= reach2) {

                        continue;

                    }

                    double w = 1.0 - d2 / reach2;
                    double phase = 2.0 * Math.PI * (ox * across.X + oy * across.Y + oz * across.Z);

                    w *= w * w;
                    total += w;
                    sum += w * Math.Cos(phase);
                    slope -= w * Math.Sin(phase);

                }

            }

        }

        change = across * (2.0 * Math.PI * slope / total);

        return sum / total;

    }

    internal static double Saturate(double x) => Math.Min(Math.Max(x, 0.0), 1.0);

    private static double Offset(uint bits) => ((bits & 1023u) / 1023.0 * 2.0 - 1.0) * Jitter;

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
