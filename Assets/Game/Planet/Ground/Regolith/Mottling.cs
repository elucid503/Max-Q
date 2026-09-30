using System;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Surface;

namespace MaxQ.Game.Planet.Ground.Regolith;

/// <summary>Selene's albedo mottling over tens of kilometres, which its heights cannot tell: the maria's dark,
/// titanium-rich flows beside paler ones, the highlands' ejecta blankets and old basin rims. From afar it is most of what
/// sets one stretch of ground apart from the next. Patches bake it into their cover texture for Regolith.hlsl.</summary>
internal static class Mottling {

    // Metres: the longest wavelength, halving over the octaves; each octave's share of the last.
    private const double Longest = 96_000.0;
    private const int Octaves = 5;
    private const double Gain = 0.55;

    /// <summary>0 to 1 about a half at a unit body-fixed <paramref name="direction"/> on a body of
    /// <paramref name="radius"/> metres, keeping no wavelength finer than <paramref name="footprint"/> metres.</summary>
    public static float At(Vector3d direction, double radius, double footprint) {

        Vector3d position = direction * radius;
        double wavelength = Longest;
        double amplitude = 0.5;
        double sum = 0.0;

        for (int octave = 0; octave < Octaves; octave++) {

            double weight = footprint <= 0.0 ? 1.0 : Math.Clamp(wavelength / (2.0 * footprint) - 1.0, 0.0, 1.0);

            if (weight <= 0.0) {

                break;

            }

            Vector3d p = position / wavelength;

            sum += weight * amplitude * Relief.Noise(p.X, p.Y, p.Z, 301u + (uint)octave);
            amplitude *= Gain;
            wavelength *= 0.5;

        }

        return (float)Math.Clamp(0.5 + sum, 0.0, 1.0);

    }

}
