using UnityEditor;
using UnityEngine;

namespace MaxQ.Game.Editor;

/// <summary>Bakes the hull's tiling detail: a height field of paint orange peel and sanding marks over gentle panel
/// waviness, as a normal map, and the faint mottling of paint and handling as a detail albedo around mid-grey (URP's Lit
/// doubles it). Both tile on a 2 m square.</summary>
public static class HullTextures {

    private const int Size = 512;

    public static (Texture2D Albedo, Texture2D Normal) Bake(string folder) {

        float[] height = new float[Size * Size];
        Color[] albedo = new Color[Size * Size];
        Color[] normal = new Color[Size * Size];

        for (int y = 0; y < Size; y++) {

            for (int x = 0; x < Size; x++) {

                float peel = Noise(x, y, 96, 1) * 0.6f + Noise(x, y, 192, 2) * 0.4f;
                float wave = Noise(x, y, 4, 3);

                height[y * Size + x] = 0.35f * peel + 2.5f * wave;

                float mottle = Noise(x, y, 6, 4) * 0.6f + Noise(x, y, 24, 5) * 0.4f;

                // Vertical streaks where handling and venting run down the stage.
                float streak = Noise(x, y, 48, 6, 3);
                float shade = 0.5f + 0.025f * (mottle - 0.5f) - 0.012f * Mathf.Max(0.0f, streak - 0.6f);

                albedo[y * Size + x] = new Color(shade, shade, shade, 1.0f);

            }

        }

        for (int y = 0; y < Size; y++) {

            for (int x = 0; x < Size; x++) {

                float dx = height[y * Size + (x + 1) % Size] - height[y * Size + (x + Size - 1) % Size];
                float dy = height[(y + 1) % Size * Size + x] - height[(y + Size - 1) % Size * Size + x];
                Vector3 n = new Vector3(-dx * 0.02f, -dy * 0.02f, 1.0f).normalized;

                normal[y * Size + x] = new Color(0.5f * n.x + 0.5f, 0.5f * n.y + 0.5f, 0.5f * n.z + 0.5f, 1.0f);

            }

        }

        return (Save(albedo, $"{folder}/Hull Detail Albedo.asset", false), Save(normal, $"{folder}/Hull Detail Normal.asset", true));

    }

    private static Texture2D Save(Color[] pixels, string path, bool linear) {

        Texture2D texture = new Texture2D(Size, Size, TextureFormat.RGBA32, true, linear) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };

        texture.SetPixels(pixels);
        texture.Apply(true);
        EditorUtility.CompressTexture(texture, TextureFormat.BC7, TextureCompressionQuality.Best);

        Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

        if (existing == null) {

            AssetDatabase.CreateAsset(texture, path);

            return texture;

        }

        EditorUtility.CopySerialized(texture, existing);
        EditorUtility.SetDirty(existing);

        return existing;

    }

    // Smooth value noise in [0, 1] that tiles the texture with the given number of cells across, and down when that differs.
    private static float Noise(int x, int y, int cells, int seed, int cellsDown = 0) {

        int down = cellsDown > 0 ? cellsDown : cells;

        float fx = (float)x * cells / Size;
        float fy = (float)y * down / Size;
        int ix = Mathf.FloorToInt(fx);
        int iy = Mathf.FloorToInt(fy);
        float tx = Smooth(fx - ix);
        float ty = Smooth(fy - iy);

        float a = Lattice(ix, iy, cells, down, seed);
        float b = Lattice(ix + 1, iy, cells, down, seed);
        float c = Lattice(ix, iy + 1, cells, down, seed);
        float d = Lattice(ix + 1, iy + 1, cells, down, seed);

        return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);

    }

    private static float Smooth(float t) => t * t * (3.0f - 2.0f * t);

    private static float Lattice(int x, int y, int across, int down, int seed) {

        uint h = (uint)((x % across + across) % across) * 374_761_393u + (uint)((y % down + down) % down) * 668_265_263u + (uint)seed * 2_246_822_519u;

        h = (h ^ (h >> 13)) * 1_274_126_177u;

        return (h ^ (h >> 16)) / (float)uint.MaxValue;

    }

}
