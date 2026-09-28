using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MaxQ.Game.Planet.Ground;

/// <summary>The smooth noise the ground's materials lay their borders along: two octaves of gradient noise on lattices of
/// 256 and 512 points a side, repeating once across the texture, which the shaders stretch over TILE_PERIOD repeats of a
/// scale so it wraps with the tile origins. Baked once, so a spot costs one filtered sample rather than hashing a lattice.</summary>
internal static class GroundNoise {

    public const int Size = 2_048;

    private const int Lattice = 256;

    public static Texture2D Create() {

        NativeArray<ushort> texels = new NativeArray<ushort>(Size * Size, Allocator.TempJob);

        new FillJob { Texels = texels }.Schedule(Size, 16).Complete();

        Texture2D texture = new Texture2D(Size, Size, GraphicsFormat.R16_UNorm, TextureCreationFlags.MipChain) {

            name = "Ground Noise",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,

        };

        texture.SetPixelData(texels, 0);
        texture.Apply(true, true);
        texels.Dispose();

        return texture;

    }

    [BurstCompile]
    private struct FillJob : IJobParallelFor {

        [NativeDisableParallelForRestriction]
        public NativeArray<ushort> Texels;

        public void Execute(int row) {

            for (int column = 0; column < Size; column++) {

                float2 p = (new float2(column, row) + 0.5f) / Size;
                float n = Gradient(p * Lattice, Lattice, 11u) + 0.5f * Gradient(p * 2 * Lattice, 2 * Lattice, 23u);

                Texels[row * Size + column] = (ushort)math.round(math.saturate(0.5f + 0.5f * n / 1.5f) * 65_535.0f);

            }

        }

        // Gradient noise in about [-0.7, 0.7], its lattice wrapping every period points.
        private static float Gradient(float2 q, int period, uint seed) {

            float2 i = math.floor(q);
            float2 f = q - i;
            float2 u = f * f * f * (f * (f * 6.0f - 15.0f) + 10.0f);
            int2 cell = (int2)i;

            float a = Corner(cell, new int2(0, 0), f, period, seed);
            float b = Corner(cell, new int2(1, 0), f, period, seed);
            float c = Corner(cell, new int2(0, 1), f, period, seed);
            float d = Corner(cell, new int2(1, 1), f, period, seed);

            return math.lerp(math.lerp(a, b, u.x), math.lerp(c, d, u.x), u.y);

        }

        private static float Corner(int2 cell, int2 offset, float2 f, int period, uint seed) {

            uint2 wrapped = (uint2)((cell + offset) & (period - 1));
            float angle = Mix(seed * 0x9E3779B9u ^ wrapped.x * 0x8DA6B343u ^ wrapped.y * 0xD8163841u) / 4_294_967_296.0f * 2.0f * math.PI;

            math.sincos(angle, out float sine, out float cosine);

            return math.dot(new float2(cosine, sine), f - offset);

        }

        // Unity's math.hash is too nearly linear for a lattice: neighbouring gradients would line up.
        private static uint Mix(uint h) {

            h = (h ^ (h >> 16)) * 0x7FEB352Du;
            h = (h ^ (h >> 15)) * 0x846CA68Bu;

            return h ^ (h >> 16);

        }

    }

}
