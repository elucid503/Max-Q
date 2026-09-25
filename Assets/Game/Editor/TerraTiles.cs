using System.IO;

using MaxQ.Game.Planet;
using MaxQ.Game.Planet.Ground;

using UnityEditor;
using UnityEngine;

namespace MaxQ.Game.Editor;

/// <summary>Compresses the colour tiles tools/terra_bake.py sampled (colour.raw) into BC7 with mips (colour.tiles).</summary>
public static class TerraTiles {

    [MenuItem("Max-Q/Bake Terra Tiles")]
    public static void Bake() {

        string directory = TerraData.Directory;
        string raw = directory == null ? null : Path.Combine(directory, "colour.raw");

        if (raw == null || !File.Exists(raw)) {

            Debug.LogError("no Data/Terra/colour.raw; run tools/terra_bake.py first");

            if (Application.isBatchMode) {

                EditorApplication.Exit(1);

            }

            return;

        }

        int rawBytes = ColourTiles.Texels * ColourTiles.Texels * 3;
        long count = new FileInfo(raw).Length / rawBytes;
        string target = Path.Combine(directory, "colour.tiles");
        byte[] pixels = new byte[rawBytes];
        Texture2D texture = new Texture2D(ColourTiles.Texels, ColourTiles.Texels, TextureFormat.RGB24, true, false);

        using (FileStream input = File.OpenRead(raw))
        using (BinaryWriter output = new BinaryWriter(File.Create(target + ".part"))) {

            for (long i = 0; i < count; i++) {

                for (int read = 0; read < rawBytes;) {

                    read += input.Read(pixels, read, rawBytes - read);

                }

                texture.Reinitialize(ColourTiles.Texels, ColourTiles.Texels, TextureFormat.RGB24, true);
                texture.SetPixelData(pixels, 0);
                texture.Apply(true);
                EditorUtility.CompressTexture(texture, TextureFormat.BC7, TextureCompressionQuality.Normal);

                if (texture.format != TextureFormat.BC7) {

                    throw new System.InvalidOperationException($"tile {i} did not compress to BC7");

                }

                byte[] compressed = texture.GetRawTextureData();

                if (i == 0) {

                    output.Write(ColourTiles.Magic);
                    output.Write(ColourTiles.Texels);
                    output.Write(ColourTiles.Levels);
                    output.Write(compressed.Length);

                }

                output.Write(compressed);

                if (i % 256 == 0 && !Application.isBatchMode && EditorUtility.DisplayCancelableProgressBar("Baking Terra tiles", $"{i} / {count}", i / (float)count)) {

                    EditorUtility.ClearProgressBar();

                    return;

                }

            }

        }

        EditorUtility.ClearProgressBar();
        Object.DestroyImmediate(texture);
        File.Delete(target);
        File.Move(target + ".part", target);
        File.Delete(raw);
        Debug.Log($"baked {count} Terra colour tiles into {target}");

    }

}
