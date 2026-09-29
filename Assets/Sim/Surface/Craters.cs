using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Surface;

/// <summary>Airless ground below the survey's resolution: craters from 640 m down to about a metre, worn by age, and
/// regolith hummocks. Highlands are saturated; maria hold fewer large craters.</summary>
public static class Craters {

    /// <summary>Metres below the survey the deepest crater reaches.</summary>
    public const double Deepest = 2.0 * Largest * DepthRatio;

    // Octaves of diameter halving from Largest; each octave spans its diameter to twice that.
    private const double Largest = 640.0;
    private const int Octaves = 10;

    // Ejecta reach in radii; one candidate per cell of that width, so 27 cells cover every crater reaching a point.
    private const double Reach = 2.2;

    // Share of candidates kept on highlands; the maria keep MareLargest of that at the largest octave.
    private const double Saturated = 0.3;
    private const double MareLargest = 0.25;

    // Fresh depth and rim over diameter, and the shares left when fully worn.
    private const double DepthRatio = 0.2;
    private const double RimRatio = 0.04;
    private const double WornDepth = 0.15;
    private const double WornRim = 0.1;

    // Craters younger than this still show bright, rayed ejecta.
    private const double Young = 0.12;
    private const double RayFrequency = 7.0;

    private const double HummockLongest = 8.0;
    private const int HummockOctaves = 3;
    private const double HummockRatio = 0.015;

    /// <summary>Metres the craters and hummocks raise the ground at <paramref name="position"/> (body-fixed, on the reference
    /// sphere), nothing finer than <paramref name="footprint"/>; <paramref name="freshness"/> is young ejecta, 0 to 1.</summary>
    public static double Detail(Vector3d position, double footprint, double mare, out double freshness) {

        double radius = position.Length;
        double height = 0.0;
        double diameter = Largest;

        freshness = 0.0;

        for (int octave = 0; octave < Octaves; octave++) {

            double weight = footprint <= 0.0 ? 1.0 : Relief.Saturate(diameter / (2.0 * footprint) - 1.0);

            if (weight <= 0.0) {

                break;

            }

            double keep = Saturated * (1.0 + (MareLargest - 1.0) * mare * (1.0 - octave / (Octaves - 1.0)));

            height += weight * Octave(position, radius, diameter, keep, (uint)octave, weight, ref freshness);
            diameter *= 0.5;

        }

        double wavelength = HummockLongest;

        for (int octave = 0; octave < HummockOctaves; octave++) {

            double weight = footprint <= 0.0 ? 1.0 : Relief.Saturate(wavelength / footprint - 1.0);

            if (weight <= 0.0) {

                break;

            }

            Vector3d p = position / wavelength;

            height += weight * HummockRatio * wavelength * Relief.Noise(p.X, p.Y, p.Z, 211u + (uint)octave);
            wavelength *= 0.5;

        }

        return height;

    }

    // Candidates jitter anywhere in their cell and count only within half a cell of the surface, so density is even.
    private static double Octave(Vector3d position, double radius, double diameter, double keep, uint seed, double weight, ref double freshness) {

        double cell = Reach * diameter;
        Vector3d p = position / cell;
        int ix = (int)Math.Floor(p.X);
        int iy = (int)Math.Floor(p.Y);
        int iz = (int)Math.Floor(p.Z);
        double sum = 0.0;

        for (int k = -1; k <= 1; k++) {

            for (int j = -1; j <= 1; j++) {

                for (int i = -1; i <= 1; i++) {

                    uint where = Relief.Hash(ix + i, iy + j, iz + k, 0xC4A7E125u + seed);
                    Vector3d candidate = new Vector3d(ix + i + Unit(where), iy + j + Unit(where >> 10), iz + k + Unit(where >> 20)) * cell;

                    // Cheap reject: a counted crater's centre is within half a cell of its candidate and reaches one cell.
                    if ((candidate - position).LengthSquared >= 2.25 * cell * cell) {

                        continue;

                    }

                    double length = candidate.Length;

                    if (Math.Abs(length - radius) >= 0.5 * cell) {

                        continue;

                    }

                    Vector3d offset = position - candidate * (radius / length);
                    double distance = offset.Length;

                    if (distance >= cell) {

                        continue;

                    }

                    uint what = Relief.Hash(ix + i, iy + j, iz + k, 0x2F6B3D19u + seed);

                    if (Unit(what) >= keep) {

                        continue;

                    }

                    double r = 0.5 * diameter * Math.Pow(2.0, 1.0 - Math.Sqrt(Unit(what >> 10)));
                    double x = distance / r;

                    if (x >= Reach) {

                        continue;

                    }

                    double age = Math.Sqrt(Unit(what >> 20));

                    sum += Profile(x, r, age);

                    if (age < Young) {

                        freshness = Math.Max(freshness, weight * Ejecta(x, offset / distance, age, what));

                    }

                }

            }

        }

        return sum;

    }

    /// <summary>Height (m) <paramref name="x"/> radii from a crater of radius <paramref name="r"/>: bowl, sharp rim and
    /// apron, worn by <paramref name="age"/> (0 to 1) toward a shallow dimple.</summary>
    public static double Profile(double x, double r, double age) {

        double depth = 2.0 * r * DepthRatio * (1.0 + (WornDepth - 1.0) * age);
        double rim = 2.0 * r * RimRatio * (1.0 + (WornRim - 1.0) * age);
        double apron = 1.0 / (Reach * Reach * Reach);
        double sharp = x < 1.0 ? -depth + (depth + rim) * x * x : rim * (1.0 / (x * x * x) - apron) / (1.0 - apron);
        double t = Relief.Saturate(x / 1.3);
        double soft = -depth * (1.0 - t * t * (3.0 - 2.0 * t));

        return sharp + (soft - sharp) * age;

    }

    private static double Ejecta(double x, Vector3d outward, double age, uint seed) {

        double blanket = x < 1.0 ? 0.85 : Math.Pow(1.0 - (x - 1.0) / (Reach - 1.0), 2.0);
        Vector3d q = outward * RayFrequency;
        double rays = x < 1.0 ? 1.0 : Math.Pow(Relief.Saturate(0.6 + 0.8 * Relief.Noise(q.X, q.Y, q.Z, seed)), 2.0);

        return blanket * rays * (1.0 - age / Young);

    }

    private static double Unit(uint bits) => (bits & 1023u) / 1024.0;

}
