using System;
using System.Collections.Generic;

using UnityEditor;
using UnityEngine;

namespace MaxQ.Game.Editor;

/// <summary>Bakes the trees' foliage atlas: a clump of broad leaves and a conifer's branch spray, painted leaf by leaf and
/// needle by needle, then pictures of a whole broadleaf and conifer and of a grove of each, painted clump by clump and
/// branch by branch. Each texel holds half the shade (1 is the tree's own colour), the normal in the card's axes (for a
/// picture, of the crown there) and coverage; each mip keeps the share of texels an alpha test passes, so a far crown
/// stays as full as a near one.</summary>
public static class FoliageAtlas {

    private const int Size = 512;

    // Slice order; Tree.shader names the same slices.
    private const int Broadleaf = 0;
    private const int Conifer = 1;
    private const int BroadleafTree = 2;
    private const int ConiferTree = 3;
    private const int BroadleafGrove = 4;
    private const int ConiferGrove = 5;

    // A conifer's picture is about a third as wide as it is tall; its branches droop by their true angle across it.
    private const float ConiferAspect = 0.32f;

    // Tree.shader clips at this coverage.
    private const float Cutoff = 0.5f;

    // What shows where nothing is painted: the mean shade, facing straight out of the card.
    private const float EmptyShade = 0.85f;

    private sealed class Canvas {

        public readonly float[] Shade = new float[Size * Size];
        public readonly float[] NormalX = new float[Size * Size];
        public readonly float[] NormalY = new float[Size * Size];
        public readonly float[] Alpha = new float[Size * Size];

        public Canvas() {

            Array.Fill(Shade, EmptyShade);

        }

        // A blade from base toward axis (unit, texture space) of the given length, as wide either side of its middle as
        // width times profile(t) at t along it. Its normal tilts by tilt (card axes) and folds toward its edges by fold,
        // and its shade runs from base to tip. Edges are antialiased over a texel.
        public void Blade(Vector2 basePoint, Vector2 axis, float length, float width, Func<float, float> profile, float baseShade, float tipShade,
            Vector2 tilt, float fold) {

            Vector2 across = new Vector2(-axis.y, axis.x);
            Vector2 tip = basePoint + axis * length;
            float texel = 1.0f / Size;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(basePoint.x, tip.x) - width) * Size) - 1);
            int x1 = Mathf.Min(Size - 1, Mathf.CeilToInt((Mathf.Max(basePoint.x, tip.x) + width) * Size) + 1);
            int y0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(basePoint.y, tip.y) - width) * Size) - 1);
            int y1 = Mathf.Min(Size - 1, Mathf.CeilToInt((Mathf.Max(basePoint.y, tip.y) + width) * Size) + 1);

            for (int y = y0; y <= y1; y++) {

                for (int x = x0; x <= x1; x++) {

                    Vector2 p = new Vector2((x + 0.5f) * texel, (y + 0.5f) * texel) - basePoint;
                    float along = Vector2.Dot(p, axis);
                    float t = along / length;
                    float side = Vector2.Dot(p, across);
                    float half = width * profile(Mathf.Clamp01(t));
                    float inside = Mathf.Min(half - Mathf.Abs(side), Mathf.Min(along, length - along));
                    float cover = Mathf.Clamp01(0.5f + inside / texel);

                    if (cover <= 0.0f || half <= 0.0f) {

                        continue;

                    }

                    float edge = Mathf.Clamp(side / Mathf.Max(half, texel), -1.0f, 1.0f);
                    Vector3 normal = new Vector3(tilt.x + across.x * fold * edge, tilt.y + across.y * fold * edge, 1.0f).normalized;
                    int i = y * Size + x;

                    Shade[i] = Mathf.Lerp(Shade[i], Mathf.Lerp(baseShade, tipShade, Mathf.Clamp01(t)), cover);
                    NormalX[i] = Mathf.Lerp(NormalX[i], normal.x, cover);
                    NormalY[i] = Mathf.Lerp(NormalY[i], normal.y, cover);
                    Alpha[i] += cover * (1.0f - Alpha[i]);

                }

            }

        }

        // A stroke of even width, for stems and twigs.
        public void Stroke(Vector2 from, Vector2 to, float baseWidth, float tipWidth, float shade) {

            Vector2 axis = (to - from).normalized;
            float length = (to - from).magnitude;

            Blade(from, axis, length, 0.5f * baseWidth, t => Mathf.Lerp(1.0f, tipWidth / baseWidth, t), shade, shade, Vector2.zero, 0.6f);

        }

    }

    /// <summary>Paints the atlas and saves it at <paramref name="path"/>, over any earlier bake.</summary>
    public static Texture2DArray Bake(string path) {

        Canvas[] slices = new Canvas[6];

        slices[Broadleaf] = PaintBroadleaf(new System.Random(1_931));
        slices[Conifer] = PaintConifer(new System.Random(7_411));
        slices[BroadleafTree] = new Canvas();
        slices[ConiferTree] = new Canvas();
        slices[BroadleafGrove] = new Canvas();
        slices[ConiferGrove] = new Canvas();

        PaintBroadleafTree(slices[BroadleafTree], new System.Random(2_207), 0.5f, 0.5f, 1.0f, 1.0f);
        PaintConiferTree(slices[ConiferTree], new System.Random(3_361), 0.5f, 0.5f, 1.0f, 1.0f);
        PaintGrove(slices[BroadleafGrove], new System.Random(4_783), false);
        PaintGrove(slices[ConiferGrove], new System.Random(5_039), true);

        int mips = (int)Math.Round(Math.Log(Size, 2)) + 1;
        Texture2DArray array = new Texture2DArray(Size, Size, slices.Length, TextureFormat.RGBA32, true, true) {

            name = "Foliage",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4,

        };

        for (int slice = 0; slice < slices.Length; slice++) {

            Canvas canvas = slices[slice];
            float coverage = Coverage(canvas.Alpha, 1.0f);
            float[] shade = canvas.Shade;
            float[] normalX = canvas.NormalX;
            float[] normalY = canvas.NormalY;
            float[] alpha = canvas.Alpha;

            for (int mip = 0, size = Size; mip < mips; mip++, size /= 2) {

                if (mip > 0) {

                    shade = Downsample(shade, alpha, size, EmptyShade);
                    normalX = Downsample(normalX, alpha, size, 0.0f);
                    normalY = Downsample(normalY, alpha, size, 0.0f);
                    alpha = Downsample(alpha, null, size, 0.0f);

                }

                float scale = mip == 0 ? 1.0f : Preserve(alpha, coverage);
                byte[] pixels = new byte[size * size * 4];

                for (int i = 0; i < size * size; i++) {

                    pixels[4 * i] = Byte(0.5f * shade[i]);
                    pixels[4 * i + 1] = Byte(0.5f + 0.5f * normalX[i]);
                    pixels[4 * i + 2] = Byte(0.5f + 0.5f * normalY[i]);
                    pixels[4 * i + 3] = Byte(alpha[i] * scale);

                }

                array.SetPixelData(pixels, mip, slice);

            }

        }

        array.Apply(false, false);

        Texture2DArray existing = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);

        if (existing == null) {

            AssetDatabase.CreateAsset(array, path);

            return array;

        }

        EditorUtility.CopySerialized(array, existing);
        EditorUtility.SetDirty(existing);

        return existing;

    }

    // A clump of leaves as a crown shows it from outside: dense and sunlit in the middle, thinning to a ragged edge of
    // leaves turned away, over a few twigs that show through the gaps. Leaves point outward from the twigs they hang on.
    private static Canvas PaintBroadleaf(System.Random random) {

        Canvas canvas = new Canvas();
        Vector2 centre = new Vector2(0.5f, 0.5f);
        const float reach = 0.46f;

        for (int twig = 0; twig < 7; twig++) {

            float angle = (float)(twig * 2.0 * Math.PI / 7.0 + random.NextDouble() * 0.6);
            Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

            canvas.Stroke(centre - direction * 0.05f, centre + direction * reach * 0.8f, 0.012f, 0.004f, 0.25f);

        }

        List<(float Radius, Vector2 Place)> leaves = new List<(float, Vector2)>();

        while (leaves.Count < 360) {

            Vector2 place = new Vector2((float)random.NextDouble() * 2.0f - 1.0f, (float)random.NextDouble() * 2.0f - 1.0f) * reach;
            float radius = place.magnitude / reach;

            if (radius > 1.0f || random.NextDouble() > 1.0 - Math.Pow(radius, 3.0)) {

                continue;

            }

            leaves.Add((radius, centre + place));

        }

        // The rim first, so the middle of the clump lies over it.
        leaves.Sort((a, b) => b.Radius.CompareTo(a.Radius));

        foreach ((float radius, Vector2 place) in leaves) {

            Vector2 outward = (place - centre).normalized;
            float angle = Mathf.Atan2(outward.y, outward.x) + (float)(random.NextDouble() - 0.5) * 1.6f;
            Vector2 axis = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            float length = Mathf.Lerp(0.07f, 0.1f, (float)random.NextDouble());
            float shade = Mathf.Lerp(0.7f, 1.2f, (float)random.NextDouble()) * Mathf.Lerp(1.08f, 0.8f, radius);
            Vector2 tilt = new Vector2((float)(random.NextDouble() - 0.5), (float)(random.NextDouble() - 0.5)) * 1.1f + outward * 0.6f * radius;

            canvas.Blade(place - axis * length * 0.3f, axis, length, 0.21f * length, Leaf, 0.85f * shade, shade, tilt, 0.35f);

        }

        return canvas;

    }

    // Ovate, widest a third of the way along, drawn to a point.
    private static float Leaf(float t) => t < 0.35f ? Mathf.Sin(t / 0.35f * 0.5f * Mathf.PI) : Mathf.Pow(Mathf.Cos((t - 0.35f) / 0.65f * 0.5f * Mathf.PI), 0.8f);

    // A spray running up the card from the trunk: a stem with twigs either side, widest a third of the way out, and
    // twiglets off the twigs. Needles stand all round every stem, so seen flat they bristle at every angle, most swept
    // toward the tip; the spray rounds off to either side, and new growth at the ends is paler.
    private static Canvas PaintConifer(System.Random random) {

        Canvas canvas = new Canvas();
        const int twigs = 34;

        Vector2 Stem(float t) => new Vector2(0.5f + 0.03f * Mathf.Sin(3.0f * t), 0.02f + 0.96f * t);

        canvas.Stroke(Stem(0.0f), Stem(1.0f), 0.018f, 0.005f, 0.3f);

        for (int i = 0; i < twigs; i++) {

            float t = 0.04f + 0.92f * i / twigs + (float)(random.NextDouble() - 0.5) * 0.02f;
            float side = i % 2 == 0 ? 1.0f : -1.0f;
            float envelope = Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Pow(t, 0.7f)), 0.8f) * (1.0f - 0.3f * t);
            Vector2 start = Stem(t);
            Vector2 end = start + Turn(new Vector2(0.0f, 1.0f), -side * Mathf.Lerp(0.6f, 1.05f, (float)random.NextDouble())) * 0.5f * envelope;

            canvas.Stroke(start, end, 0.007f, 0.003f, 0.35f);

            for (int k = 0; k < 3; k++) {

                float along = 0.3f + 0.25f * k + (float)(random.NextDouble() - 0.5) * 0.1f;
                Vector2 fork = Vector2.Lerp(start, end, along);
                Vector2 twiglet = fork + Turn((end - start).normalized, (k % 2 == 0 ? 1.0f : -1.0f) * 0.7f) * (end - start).magnitude * (1.0f - along) * 0.5f;

                canvas.Stroke(fork, twiglet, 0.004f, 0.002f, 0.4f);
                Needles(canvas, random, fork, twiglet);

            }

            Needles(canvas, random, start, end);

        }

        Needles(canvas, random, Stem(0.03f), Stem(1.0f));

        return canvas;

    }

    private static Vector2 Turn(Vector2 v, float angle) => new Vector2(v.x * Mathf.Cos(angle) - v.y * Mathf.Sin(angle), v.x * Mathf.Sin(angle) + v.y * Mathf.Cos(angle));

    // Needles along a stem from start to end, both sides and now and then along it.
    private static void Needles(Canvas canvas, System.Random random, Vector2 start, Vector2 end) {

        Vector2 axis = (end - start).normalized;
        float length = (end - start).magnitude;

        for (float s = 0.03f * length; s < length; s += Mathf.Lerp(0.005f, 0.008f, (float)random.NextDouble())) {

            float along = s / length;
            int count = random.NextDouble() < 0.35 ? 3 : 2;

            for (int n = 0; n < count; n++) {

                float sweep = n == 2 ? (float)(random.NextDouble() - 0.5) * 0.5f : (n == 0 ? -1.0f : 1.0f) * Mathf.Lerp(0.3f, 1.45f, (float)random.NextDouble());
                Vector2 root = start + axis * s;
                float needle = Mathf.Lerp(0.022f, 0.034f, (float)random.NextDouble()) * Mathf.Lerp(1.0f, 0.8f, along);
                float shade = Mathf.Lerp(0.7f, 1.15f, (float)random.NextDouble()) * Mathf.Lerp(0.85f, 1.3f, along * along);
                Vector2 tilt = new Vector2((root.x - 0.5f) * 2.4f, 0.0f) + new Vector2((float)(random.NextDouble() - 0.5), (float)(random.NextDouble() - 0.5)) * 1.2f;

                canvas.Blade(root, Turn(axis, sweep), needle, 0.0026f, Needle, 0.8f * shade, shade, tilt, 0.5f);

            }

        }

    }

    private static float Needle(float t) => t < 0.85f ? 1.0f : (1.0f - t) / 0.15f;

    // A broadleaf seen from the side, centred at x, half wide and top tall on the canvas: a trunk forking into limbs under a
    // dome of leaf clumps, the far ones first. Each leaf's normal is part the dome's and part its own clump's, so the
    // crown shades as lobes; the top is sunlit and the underside and the rims of clumps dark.
    private static void PaintBroadleafTree(Canvas canvas, System.Random random, float x, float half, float top, float shade) {

        canvas.Stroke(new Vector2(x, 0.0f), new Vector2(x, 0.5f * top), 0.1f * half, 0.05f * half, 0.3f * shade);

        for (int limb = -1; limb <= 1; limb++) {

            canvas.Stroke(new Vector2(x, 0.32f * top), new Vector2(x + 0.45f * half * limb, (limb == 0 ? 0.75f : 0.6f) * top), 0.06f * half, 0.02f * half, 0.3f * shade);

        }

        Vector2 centre = new Vector2(x, 0.64f * top);
        Vector2 radii = new Vector2(0.92f * half, 0.34f * top);
        List<(float Depth, Vector2 Place)> clumps = new List<(float, Vector2)>();

        for (int k = 0; k < 55; k++) {

            float angle = (float)(random.NextDouble() * 2.0 * Math.PI);
            float radius = Mathf.Pow((float)random.NextDouble(), 0.35f);
            Vector2 place = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;

            clumps.Add((Mathf.Sqrt(Mathf.Max(1.0f - radius * radius, 0.0f)), place));

        }

        clumps.Sort((a, b) => a.Depth.CompareTo(b.Depth));

        foreach ((float depth, Vector2 place) in clumps) {

            Vector2 at = centre + Vector2.Scale(place * 0.8f, radii);
            Vector2 size = 0.3f * radii;
            float clumpShade = shade * Mathf.Lerp(0.5f, 1.15f, 0.5f + 0.5f * place.y) * Mathf.Lerp(0.8f, 1.05f, depth) * Mathf.Lerp(0.9f, 1.1f, (float)random.NextDouble());

            for (int leaf = 0; leaf < 22; leaf++) {

                float angle = (float)(random.NextDouble() * 2.0 * Math.PI);
                float reach = Mathf.Sqrt((float)random.NextDouble());
                Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * reach;
                Vector2 leafAt = at + Vector2.Scale(offset, size);
                Vector2 dome = Vector2.Scale(leafAt - centre, new Vector2(1.0f / radii.x, 1.0f / radii.y));

                dome = dome.magnitude > 0.99f ? dome.normalized * 0.99f : dome;

                Vector3 normal = (0.55f * new Vector3(dome.x, dome.y, Mathf.Sqrt(1.0f - dome.sqrMagnitude)) +
                    0.45f * new Vector3(offset.x, offset.y, Mathf.Sqrt(Mathf.Max(1.0f - reach * reach, 0.0f)))).normalized;
                float axisAngle = (float)(random.NextDouble() * 2.0 * Math.PI);
                Vector2 axis = new Vector2(Mathf.Cos(axisAngle), Mathf.Sin(axisAngle));
                float length = 0.5f * Mathf.Max(size.x, size.y);
                float leafShade = clumpShade * Mathf.Lerp(0.85f, 1.15f, (float)random.NextDouble()) * Mathf.Lerp(0.8f, 1.0f, 1.0f - reach * reach);
                Vector2 tilt = new Vector2(normal.x, normal.y) / Mathf.Max(normal.z, 0.25f);

                canvas.Blade(leafAt - axis * length * 0.3f, axis, length, 0.18f * length, Leaf, 0.9f * leafShade, leafShade, tilt, 0.2f);

            }

        }

    }

    // A conifer seen from the side, centred at x, half wide and top tall: a trunk under whorls of branches from the base
    // of the crown up to a leader, each drooping by its true angle and fringed with twigs, the upper ones over the lower.
    // Normals are the cone's; the crown darkens toward its foot and each branch lightens toward its tip.
    private static void PaintConiferTree(Canvas canvas, System.Random random, float x, float half, float top, float shade) {

        const int whorls = 40;
        const float crownBase = 0.2f;
        float aspect = ConiferAspect * top / (2.0f * half);

        canvas.Stroke(new Vector2(x, 0.0f), new Vector2(x, 0.55f * top), 0.08f * half, 0.03f * half, 0.3f * shade);

        for (int i = 0; i < whorls; i++) {

            float s = i / (whorls - 1.0f);
            float y = top * (crownBase + (1.0f - crownBase) * s);
            float envelope = Mathf.Pow(1.0f - s, 0.85f);
            float droop = Mathf.Lerp(0.55f, 0.15f, s);

            for (int branch = 0; branch < 3; branch++) {

                float side = branch == 2 ? (random.NextDouble() < 0.5 ? -1.0f : 1.0f) : branch * 2.0f - 1.0f;
                float reach = envelope * half * Mathf.Lerp(0.85f, 1.1f, (float)random.NextDouble()) * (branch == 2 ? 0.6f : 1.0f);
                float angle = droop + (float)(random.NextDouble() - 0.5) * 0.2f;
                Vector2 direction = new Vector2(side * Mathf.Cos(angle), -Mathf.Sin(angle) * aspect);
                Vector2 span = direction / Mathf.Abs(direction.x) * reach;
                Vector2 start = new Vector2(x, y);
                float thickness = 0.016f * top * (1.0f + 0.5f * (1.0f - s));
                float branchShade = shade * Mathf.Lerp(0.5f, 1.05f, s) * Mathf.Lerp(0.85f, 1.15f, (float)random.NextDouble());

                for (int k = 0; k < 3; k++) {

                    Vector2 from = start + span * (k / 3.0f);
                    Vector2 to = start + span * ((k + 1) / 3.0f);
                    float across = ((from + to) * 0.5f).x - x;
                    float t = Mathf.Clamp(across / Mathf.Max(envelope * half, 1e-3f), -1.0f, 1.0f);
                    Vector3 normal = new Vector3(0.8f * t + (float)(random.NextDouble() - 0.5) * 0.2f, 0.3f, Mathf.Sqrt(Mathf.Max(0.05f, 1.0f - 0.64f * t * t))).normalized;
                    Vector2 tilt = new Vector2(normal.x, normal.y) / normal.z;
                    float segmentShade = branchShade * Mathf.Lerp(0.85f, 1.1f, k / 2.0f);
                    Vector2 axis = (to - from).normalized;
                    float length = (to - from).magnitude;

                    canvas.Blade(from, axis, length * 1.15f, thickness * (1.0f - 0.25f * k), t2 => 1.0f - 0.4f * t2, 0.9f * segmentShade, segmentShade, tilt, 0.3f);

                    for (int twig = -1; twig <= 1; twig += 2) {

                        Vector2 twigAxis = Turn(axis, twig * side * 0.6f);

                        canvas.Blade(from + (to - from) * 0.5f, twigAxis, 0.5f * length, 0.5f * thickness, Needle, 0.9f * segmentShade, segmentShade, tilt, 0.3f);

                    }

                }

            }

        }

        canvas.Blade(new Vector2(x, 0.94f * top), new Vector2(0.0f, 1.0f), 0.06f * top, 0.25f * half * ConiferAspect, t => 1.0f - t, shade, shade, new Vector2(0.0f, 0.3f), 0.3f);

    }

    // A stand of trees across the card, a back row, darker, and a front row between them, of mixed heights.
    private static void PaintGrove(Canvas canvas, System.Random random, bool needles) {

        const int perRow = 4;

        for (int row = 0; row < 2; row++) {

            for (int k = 0; k < perRow; k++) {

                float x = (k + 0.5f * row + 0.25f + 0.5f * (float)random.NextDouble()) / (perRow + 0.5f);
                float half = needles ? Mathf.Lerp(0.07f, 0.1f, (float)random.NextDouble()) : Mathf.Lerp(0.12f, 0.17f, (float)random.NextDouble());
                float top = row == 0 ? Mathf.Lerp(0.75f, 0.95f, (float)random.NextDouble()) : Mathf.Lerp(0.6f, 0.85f, (float)random.NextDouble());
                float shade = row == 0 ? 0.75f : 1.0f;

                if (needles) {

                    PaintConiferTree(canvas, random, x, half, top, shade);

                } else {

                    PaintBroadleafTree(canvas, random, x, half, top, shade);

                }

            }

        }

    }

    // Half the size each way; colour channels weighted by coverage, so empty texels never darken an edge.
    private static float[] Downsample(float[] source, float[] weights, int size, float empty) {

        float[] result = new float[size * size];
        int from = size * 2;

        for (int y = 0; y < size; y++) {

            for (int x = 0; x < size; x++) {

                float sum = 0.0f;
                float weight = 0.0f;

                for (int k = 0; k < 4; k++) {

                    int i = (2 * y + (k >> 1)) * from + 2 * x + (k & 1);
                    float w = weights == null ? 1.0f : weights[i];

                    sum += source[i] * w;
                    weight += w;

                }

                result[y * size + x] = weight > 1e-4f ? sum / weight : empty;

            }

        }

        return result;

    }

    // Share of texels whose coverage, scaled, passes the alpha test.
    private static float Coverage(float[] alpha, float scale) {

        int passed = 0;

        foreach (float a in alpha) {

            passed += a * scale >= Cutoff ? 1 : 0;

        }

        return passed / (float)alpha.Length;

    }

    // The coverage scale at which a mip passes the alpha test as often as the full image does.
    private static float Preserve(float[] alpha, float coverage) {

        float low = 0.0f;
        float high = 8.0f;

        for (int i = 0; i < 20; i++) {

            float middle = 0.5f * (low + high);

            if (Coverage(alpha, middle) < coverage) {

                low = middle;

            } else {

                high = middle;

            }

        }

        return high;

    }

    private static byte Byte(float x) => (byte)Mathf.RoundToInt(Mathf.Clamp01(x) * 255.0f);

}
