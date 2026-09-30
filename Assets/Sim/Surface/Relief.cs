using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Surface;

/// <summary>Procedural ground detail below the survey's resolution: ridged ranges on its mountains, gullies that run down
/// its slopes between sharp-crested spurs and branch off one another, over gently rolling ground whose roughness follows
/// how steep the survey says the land is, and on steep ground, bands of cliff over aprons of scree. Regions tens of
/// kilometres across run from subdued to craggy, so no two ranges or plains look alike.</summary>
public static class Relief {

    // The survey carries everything above ~2 km; detail starts just below that and halves down to a metre.
    private const double LongestWavelength = 2_048.0;
    private const int Octaves = 12;

    // Gullies are cut at the longer octaves only; below a few metres rolling noise alone roughens the ground.
    private const int GullyOctaves = 7;

    // Amplitude per octave; 0.58 is a Hurst exponent near 0.78, which is what measured relief shows.
    private const double Gain = 0.58;

    // Amplitude over wavelength at the longest octave: a floor for plains plus a share of the survey slope, split
    // between rolling ground and gullies.
    private const double FlatRatio = 0.0006;
    private const double SlopeRatio = 0.24;
    private const double RollingShare = 0.3;

    // Slope below which gullies widen out and fade, so their direction never jumps where the ground levels off.
    private const double GullySlope = 0.08;

    // How strongly each octave's gullies turn the next octave's, so small gullies branch off the walls of large ones.
    private const double Branching = 1.5;

    // Gully cells: points jittered this far from each cell's centre, blended within this radius (in cells). With these
    // values the 27 cells around a point always include every one within reach, and at least one.
    private const double Jitter = 0.15;
    private const double Reach = 1.25;

    // Strata: bands this many metres thick, tilted and warped over these wavelengths, that weather into cliffs where the
    // survey is steeper than CliffSlopeStart. Within each band the cliff and the scree apron below it take CliffShare of
    // the band's height; the rest is a bench.
    private const double BandHeight = 40.0;
    private const double BandWarpWavelength = 3_000.0;
    private const double BandRippleWavelength = 700.0;
    private const double CliffSlopeStart = 0.22;
    private const double CliffSlopeFull = 0.7;
    private const double CliffShare = 0.45;
    private const double CliffStrength = 0.8;

    // Ruggedness: a factor from RuggedLeast to RuggedMost on the relief and the cliffs' slope, over regions this wide.
    private const double RuggedWavelength = 48_000.0;
    private const double RuggedLeast = 0.55;
    private const double RuggedMost = 1.8;

    // Ranges: ridged crests over the survey's mountains, too fine for its posts, from RangeWavelength down; amplitude over
    // wavelength, reached where the survey is steeper than RangeSlopeFull.
    private const double RangeWavelength = 8_192.0;
    private const int RangeOctaves = 2;
    private const double RangeRatio = 0.014;
    private const double RangeSlopeStart = 0.02;
    private const double RangeSlopeFull = 0.15;

    // The mean of a squared ridge, taken off so ranges raise crests and cut valleys without lifting the ground.
    private const double RidgeMean = 0.6;

    /// <summary>How craggy the region round body-fixed <paramref name="position"/> is: a factor on the relief, near 1.</summary>
    public static double Ruggedness(Vector3d position) {

        Vector3d p = position / RuggedWavelength;
        Vector3d q = position / (0.37 * RuggedWavelength);
        double n = 0.7 * Noise(p.X, p.Y, p.Z, 101u) + 0.3 * Noise(q.X + 5.3, q.Y, q.Z, 102u);

        return RuggedLeast * Math.Pow(RuggedMost / RuggedLeast, SmoothStep(-0.5, 0.5, n));

    }

    /// <summary>Detail height in metres at body-fixed <paramref name="position"/>, keeping only wavelengths the
    /// <paramref name="footprint"/> (sample spacing, metres) can carry; a zero footprint keeps them all.
    /// <paramref name="gradient"/> is the survey's uphill slope there, rise over run along the ground, and
    /// <paramref name="rugged"/> the region's <see cref="Ruggedness"/>.</summary>
    public static double Detail(Vector3d position, double footprint, Vector3d gradient, double rugged = 1.0) {

        Vector3d up = position.Normalized;
        double slope = gradient.Length;
        double rolling = rugged * LongestWavelength * (FlatRatio + RollingShare * SlopeRatio * slope);
        double eroded = rugged * LongestWavelength * (1.0 - RollingShare) * SlopeRatio * slope;

        double wavelength = LongestWavelength;
        double sum = Ranges(position, footprint, slope, rugged);

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

    // Ridged, warped crests: sharp where the noise crosses zero, so they wind into ranges and spurs rather than blobs.
    private static double Ranges(Vector3d position, double footprint, double slope, double rugged) {

        double amplitude = rugged * RangeRatio * RangeWavelength * SmoothStep(RangeSlopeStart, RangeSlopeFull, slope);
        double wavelength = RangeWavelength;
        double sum = 0.0;

        for (int octave = 0; octave < RangeOctaves && amplitude > 0.0; octave++) {

            double weight = footprint <= 0.0 ? 1.0 : Saturate(wavelength / footprint - 1.0);

            if (weight <= 0.0) {

                break;

            }

            Vector3d p = position / wavelength;
            Vector3d w = p * 0.5;
            double bend = 0.6 * Noise(w.X + 17.1, w.Y, w.Z, 111u + (uint)octave);
            double ridge = 1.0 - Math.Abs(Noise(p.X + bend, p.Y - bend, p.Z + 0.5 * bend, 121u + (uint)octave));

            sum += weight * amplitude * (ridge * ridge - RidgeMean);
            amplitude *= Gain;
            wavelength *= 0.5;

        }

        return sum;

    }

    /// <summary>Gradient noise in [-1, 1], continuous in value and slope.</summary>
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

    /// <summary>Cliff bands: <paramref name="height"/> (metres, at body-fixed <paramref name="position"/>) regraded into
    /// strata of benches, scree aprons and cliffs with sharp rims, as far as <paramref name="slope"/> (the survey's rise over
    /// run) is steep and <paramref name="footprint"/> can carry a band; a zero footprint keeps them all. Craggier regions
    /// (<paramref name="rugged"/>) break into cliffs on gentler slopes.</summary>
    public static double Strata(Vector3d position, double height, double slope, double footprint, double rugged = 1.0) {

        double strength = CliffStrength * SmoothStep(CliffSlopeStart, CliffSlopeFull, slope * rugged);

        // A band's cliff is a few metres across along the ground; coarser samples see the slope it averages to.
        double spacing = BandHeight / Math.Max(slope, CliffSlopeStart);

        if (footprint > 0.0) {

            strength *= Saturate(spacing / (3.0 * footprint) - 1.0);

        }

        if (strength <= 0.0) {

            return height;

        }

        Vector3d warp = position / BandWarpWavelength;
        Vector3d ripple = position / BandRippleWavelength;
        double t = (height + BandHeight * (0.9 * Noise(warp.X, warp.Y, warp.Z, 91u) + 0.25 * Noise(ripple.X, ripple.Y, ripple.Z, 92u))) / BandHeight;
        double floor = Math.Floor(t);

        return height + strength * BandHeight * (floor + Band(t - floor) - t);

    }

    // Rise through one band, 0 to 1 over u in [0, 1): a level bench, then scree steepening into a cliff, which ends at a
    // sharp rim.
    internal static double Band(double u) {

        double x = Saturate((u - 0.5 * (1.0 - CliffShare)) / CliffShare);

        return x * x * x;

    }

    /// <summary>Gullies in [-1, 1] at <paramref name="p"/> (in cells): each jittered cell point carries ridges and furrows
    /// across <paramref name="across"/>, one per cell at full length, and neighbouring cells blend, breaking the furrows
    /// into gullies of a cell or two. The blend keeps one phase, so spurs keep sharp crests between rounded gully floors.
    /// <paramref name="change"/> is the rate of change per cell of the rounded waves that steer the next octave.</summary>
    public static double Gullies(Vector3d p, Vector3d across, uint seed, out Vector3d change) {

        int ix = (int)Math.Floor(p.X);
        int iy = (int)Math.Floor(p.Y);
        int iz = (int)Math.Floor(p.Z);

        double reach2 = Reach * Reach;
        double total = 0.0;
        double sum = 0.0;
        double sine = 0.0;

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
                    sine += w * Math.Sin(phase);

                }

            }

        }

        change = across * (-2.0 * Math.PI * sine / total);

        // The blended wave, amplitude and phase, with its crests pinched to a point.
        double amplitude = Math.Sqrt(sum * sum + sine * sine) / total;
        double half = Math.Abs(Math.Sin(0.5 * Math.Atan2(sine, sum)));

        return amplitude * (1.0 - 2.0 * half);

    }

    internal static double Saturate(double x) => Math.Min(Math.Max(x, 0.0), 1.0);

    private static double SmoothStep(double from, double to, double x) {

        double t = Saturate((x - from) / (to - from));

        return t * t * (3.0 - 2.0 * t);

    }

    private static double Offset(uint bits) => ((bits & 1023u) / 1023.0 * 2.0 - 1.0) * Jitter;

    private static double Fade(double t) => t * t * t * (t * (t * 6.0 - 15.0) + 10.0);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    internal static uint Hash(int x, int y, int z, uint seed) {

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
