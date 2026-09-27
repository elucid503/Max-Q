using System;

using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MaxQ.Game.Planet.Water.Surface;

/// <summary>A tileable texture the water draws foam and slow variation from. Red is foam's structure: packed bubbles
/// (cellular noise, bright at the cell walls where bubbles crowd) over a fine fractal grain, which foam of a given
/// coverage eats into from its brightest texels down. Green, blue and alpha are smooth fractal noise, each on its own
/// lattice: read at kilometre scales they vary the waves' strength so no cascade's repeat lines up across a view, and
/// green gathers a river's white water into clumps.</summary>
internal static class FoamTexture {

    private const int Size = 256;

    public static Texture2D Create() {

        byte[] texels = new byte[Size * Size * 4];

        for (int y = 0; y < Size; y++) {

            for (int x = 0; x < Size; x++) {

                double u = (x + 0.5) / Size;
                double v = (y + 0.5) / Size;
                double bubbles = 0.6 * Walls(u, v, 9, 17) + 0.4 * Walls(u, v, 23, 29);
                double grain = Fractal(u, v, 16, 4, 3);
                int t = (y * Size + x) * 4;

                texels[t] = Byte(0.75 * bubbles + 0.25 * grain);
                texels[t + 1] = Byte(Fractal(u, v, 12, 3, 11));
                texels[t + 2] = Byte(Fractal(u, v, 29, 3, 31));
                texels[t + 3] = Byte(Fractal(u, v, 17, 3, 53));

            }

        }

        Texture2D texture = new Texture2D(Size, Size, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.MipChain) {

            name = "Water Foam",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,

        };

        texture.SetPixelData(texels, 0);
        texture.Apply(true, true);

        return texture;

    }

    private static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255.0);

    // Cellular noise on a torus of cells by cells: one minus the gap between the nearest two points, so cell walls are
    // bright and cell middles dark.
    private static double Walls(double u, double v, int cells, int seed) {

        double x = u * cells;
        double y = v * cells;
        int cx = (int)Math.Floor(x);
        int cy = (int)Math.Floor(y);
        double first = double.MaxValue;
        double second = double.MaxValue;

        for (int j = -1; j <= 1; j++) {

            for (int i = -1; i <= 1; i++) {

                int px = cx + i;
                int py = cy + j;
                int wx = (px % cells + cells) % cells;
                int wy = (py % cells + cells) % cells;
                double fx = px + Hash(wx, wy, seed);
                double fy = py + Hash(wx, wy, seed + 1);
                double d = Math.Sqrt((fx - x) * (fx - x) + (fy - y) * (fy - y));

                if (d < first) {

                    second = first;
                    first = d;

                } else if (d < second) {

                    second = d;

                }

            }

        }

        return 1.0 - Math.Clamp((second - first) * 2.0, 0.0, 1.0);

    }

    // Octaves of tileable value noise from a lattice of cells by cells, halving in size and strength.
    private static double Fractal(double u, double v, int cells, int octaves, int seed) {

        double sum = 0.0;
        double weight = 0.5;
        double total = 0.0;

        for (int octave = 0; octave < octaves; octave++) {

            sum += weight * Value(u, v, cells << octave, seed + octave);
            total += weight;
            weight *= 0.5;

        }

        return sum / total;

    }

    private static double Value(double u, double v, int cells, int seed) {

        double x = u * cells;
        double y = v * cells;
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        double tx = Smooth(x - x0);
        double ty = Smooth(y - y0);

        double Corner(int i, int j) => Hash(((x0 + i) % cells + cells) % cells, ((y0 + j) % cells + cells) % cells, seed);

        double top = Corner(0, 0) + (Corner(1, 0) - Corner(0, 0)) * tx;
        double bottom = Corner(0, 1) + (Corner(1, 1) - Corner(0, 1)) * tx;

        return top + (bottom - top) * ty;

    }

    private static double Smooth(double t) => t * t * (3.0 - 2.0 * t);

    // A number in [0, 1) fixed by a lattice point and seed.
    private static double Hash(int x, int y, int seed) {

        uint h = (uint)(x * 374_761_393 + y * 668_265_263 + seed * 144_665_591);

        h = (h ^ (h >> 13)) * 1_274_126_177u;
        h ^= h >> 16;

        return h / 4_294_967_296.0;

    }

}
