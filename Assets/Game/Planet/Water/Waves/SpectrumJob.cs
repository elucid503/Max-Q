using MaxQ.Sim.Ocean;

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MaxQ.Game.Planet.Water.Waves;

/// <summary>Fills each cascade's initial spectrum, h0(k) and h0(-k) per texel, from the sim's directional spectrum, a row
/// of one cascade per work item. Each texel's Gaussian draw is fixed by its place, so a changed sea re-weights the
/// same waves rather than raising new ones. Also sums each row's height and slope variance and mean wavelength.</summary>
[BurstCompile]
internal struct SpectrumJob : IJobFor {

    public Spectrum Spectrum;

    // Per cascade: tile size (m), turn from the frame (radians), and the band of wavenumbers it holds (rad/m).
    public double4 Sizes;
    public double4 Angles;
    public double4 Low;
    public double4 High;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<float4> Initial;

    // Per row: the band's height variance (m^2), slope variance, and variance-weighted sum of wavelength (m^3).
    [WriteOnly]
    public NativeArray<double3> Rows;

    public void Execute(int row) {

        int cascade = row / WaveCascades.Size;
        int y = row % WaveCascades.Size;
        double size = Sizes[cascade];
        double step = 2.0 * math.PI_DBL / size;
        double cosine = math.cos(Angles[cascade]);
        double sine = math.sin(Angles[cascade]);
        double3 sums = 0.0;

        for (int x = 0; x < WaveCascades.Size; x++) {

            double2 k = new double2(x - WaveCascades.Size / 2, y - WaveCascades.Size / 2) * step;
            double energy = Energy(k, cascade, cosine, sine, step);
            double oppositeEnergy = Energy(-k, cascade, cosine, sine, step);

            // Each wave shows at k and, conjugated, at -k, so each carries half its variance: E|h0|^2 = S dk^2 / 2. Most
            // texels lie outside the cascade's band and draw nothing, so their Gaussians are never made.
            double2 h = energy > 0.0 ? Draw(cascade, x, y) * math.sqrt(0.25 * energy) : 0.0;
            double2 opposite = oppositeEnergy > 0.0 ?
                Draw(cascade, (WaveCascades.Size - x) % WaveCascades.Size, (WaveCascades.Size - y) % WaveCascades.Size) * math.sqrt(0.25 * oppositeEnergy) : 0.0;

            Initial[row * WaveCascades.Size + x] = new float4((float)h.x, (float)h.y, (float)opposite.x, (float)opposite.y);
            sums += new double3(energy, math.lengthsq(k) * energy, energy > 0.0 ? 2.0 * math.PI_DBL / math.length(k) * energy : 0.0);

        }

        Rows[row] = sums;

    }

    // The surface variance a texel carries: the spectrum's density at its wavenumber, turned into the frame, over the
    // texel's share of the wavenumber plane, if the wavenumber falls in this cascade's band.
    private double Energy(double2 k, int cascade, double cosine, double sine, double step) {

        double length = math.length(k);

        if (length < Low[cascade] || length >= High[cascade] || length < 1e-9) {

            return 0.0;

        }

        return Spectrum.Density(cosine * k.x - sine * k.y, sine * k.x + cosine * k.y) * step * step;

    }

    // A complex Gaussian of unit variance per part, fixed by the cascade and texel (Box-Muller).
    private static double2 Draw(int cascade, int x, int y) {

        uint seed = Mix((uint)x + Mix((uint)y + Mix((uint)cascade + 0x5EAu)));
        double u = (Mix(seed) + 0.5) / 4_294_967_296.0;
        double v = (Mix(seed ^ 0xC0A57u) + 0.5) / 4_294_967_296.0;
        double radius = math.sqrt(-2.0 * math.log(u));

        return new double2(radius * math.cos(2.0 * math.PI_DBL * v), radius * math.sin(2.0 * math.PI_DBL * v));

    }

    // PCG's output permutation: math.hash only sums its inputs times constants, which would line the waves' phases up.
    private static uint Mix(uint value) {

        uint state = value * 747_796_405u + 2_891_336_453u;
        uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277_803_737u;

        return (word >> 22) ^ word;

    }

}
