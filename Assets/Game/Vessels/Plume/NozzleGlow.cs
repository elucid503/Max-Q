using System;
using System.Collections.Generic;

using MaxQ.Game.Map;
using MaxQ.Game.Vessels.Craft;

using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Vessels.Plume;

/// <summary>A radiatively cooled nozzle extension glowing as a grey body. The engine model's triangles below the joint are
/// drawn again by NozzleGlow.shader with each vertex's share of the hottest temperature, and lights round the extension
/// throw its glow on the stage above.</summary>
internal sealed class NozzleGlow {

    // The blackbody table: temperatures at its texel centres span these (K), below which nothing shows.
    private const int Entries = 256;
    private const float Coolest = 700.0f;
    private const float Hottest = 2_500.0f;

    // Sunlight above the air (lux) shows on white ground as 2.4 (see MapView), so a candela per square metre is this much
    // of the renderer's radiance.
    private const double SunIlluminance = 128_000.0;
    private const double RadiancePerCandela = 2.4 * Math.PI / SunIlluminance;

    // Each light stands for a third of the extension's outside; its surface faces the stage at about this cosine.
    private const int Lights = 3;
    private const float Facing = 0.5f;

    private static readonly int GlowId = Shader.PropertyToID("_NozzleGlow");
    private static readonly int BlackbodyId = Shader.PropertyToID("_Blackbody");

    private static Color[] _table;
    private static Texture2D _texture;

    private readonly MeshRenderer _renderer;
    private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
    private readonly float _emissivity;
    private readonly List<(int Sector, float Area, float Share, Vector3 Centre)> _faces = new List<(int, float, float, Vector3)>();
    private readonly Light[] _lights = new Light[Lights];
    private readonly float _nearby;
    private float _temperature = -1.0f;

    /// <summary>The glowing triangles, which the caller destroys.</summary>
    public Mesh Mesh { get; }

    /// <summary>Picks the extension out of <paramref name="model"/>, hung from <paramref name="pivot"/> with its exit
    /// <paramref name="length"/> metres below.</summary>
    public NozzleGlow(Material material, Transform pivot, GameObject model, double length, NozzleExtension extension) {

        // The stage's aft end stands about the cooled nozzle's length above the joint.
        _nearby = VesselLight.Nearby(length - extension.joint);
        _emissivity = (float)extension.emissivity;
        _table ??= Table();
        _texture ??= Texture(_table);

        MeshFilter filter = model.GetComponentInChildren<MeshFilter>();
        Mesh source = filter.sharedMesh;

        if (!source.isReadable) {

            throw new InvalidOperationException($"Engine model '{model.name}' is not readable, so its nozzle extension cannot glow.");

        }

        // Vertices in the pivot's metres, where the exit lies at -length on the axis.
        Matrix4x4 toPivot = pivot.worldToLocalMatrix * filter.transform.localToWorldMatrix;
        Vector3[] vertices = source.vertices;
        Vector3[] placed = Array.ConvertAll(vertices, v => toPivot.MultiplyPoint3x4(v));
        int[] triangles = source.triangles;
        float joint = (float)(extension.joint - length);
        float top = float.MaxValue;
        List<int> kept = new List<int>();

        for (int t = 0; t < triangles.Length; t += 3) {

            if (placed[triangles[t]].y < joint && placed[triangles[t + 1]].y < joint && placed[triangles[t + 2]].y < joint) {

                for (int k = 0; k < 3; k++) {

                    kept.Add(triangles[t + k]);
                    top = Mathf.Min(top, Across(placed[triangles[t + k]]));

                }

            }

        }

        // Each vertex's share of the hottest temperature: the gas's heat on the wall falls off as the area it has spread
        // over, r^2, and the wall radiates as T^4, so T runs as 1 / sqrt(r) down from the joint.
        Dictionary<int, int> remap = new Dictionary<int, int>();
        List<Vector3> positions = new List<Vector3>();
        List<Vector2> shares = new List<Vector2>();
        int[] indices = new int[kept.Count];

        for (int i = 0; i < kept.Count; i++) {

            if (!remap.TryGetValue(kept[i], out int index)) {

                index = positions.Count;
                remap[kept[i]] = index;
                positions.Add(vertices[kept[i]]);
                shares.Add(new Vector2(Mathf.Sqrt(top / Mathf.Max(Across(placed[kept[i]]), top)), 0.0f));

            }

            indices[i] = index;

        }

        // The extension is one sheet; its outside lights the stage, a third of the way round for each light.
        for (int i = 0; i < kept.Count; i += 3) {

            Vector3 a = placed[kept[i]];
            Vector3 b = placed[kept[i + 1]];
            Vector3 c = placed[kept[i + 2]];
            Vector3 centre = (a + b + c) / 3.0f;
            float around = Mathf.Repeat(Mathf.Atan2(centre.z, centre.x) / (2.0f * Mathf.PI), 1.0f);
            float share = (shares[remap[kept[i]]].x + shares[remap[kept[i + 1]]].x + shares[remap[kept[i + 2]]].x) / 3.0f;

            _faces.Add(((int)(around * Lights) % Lights, 0.5f * Vector3.Cross(b - a, c - a).magnitude, share, centre));

        }

        Mesh = new Mesh { name = "Nozzle Glow", hideFlags = HideFlags.DontSave, indexFormat = positions.Count > 65_535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        Mesh.SetVertices(positions);
        Mesh.SetUVs(0, shares);
        Mesh.SetTriangles(indices, 0);
        Mesh.RecalculateBounds();

        GameObject glow = new GameObject("Nozzle Glow");

        glow.transform.SetParent(filter.transform, false);
        glow.AddComponent<MeshFilter>().sharedMesh = Mesh;
        _renderer = glow.AddComponent<MeshRenderer>();
        _renderer.sharedMaterial = material;
        _renderer.shadowCastingMode = ShadowCastingMode.Off;
        _renderer.receiveShadows = false;

        for (int i = 0; i < Lights; i++) {

            Light light = new GameObject("Nozzle Light").AddComponent<Light>();

            light.transform.SetParent(pivot, false);
            light.type = LightType.Point;
            light.range = (float)(VesselLight.NearbyRange / MapSpace.MetresPerUnit);
            light.shadows = LightShadows.None;
            _lights[i] = light;

        }

        _block.SetTexture(BlackbodyId, _texture);
        Temperature = 0.0f;

    }

    /// <summary>The extension's hottest temperature, K.</summary>
    public float Temperature {

        set {

            if (Mathf.Abs(value - _temperature) < 0.5f) {

                return;

            }

            _temperature = value;
            _renderer.enabled = value > Coolest;
            _block.SetVector(GlowId, new Vector4(value, _emissivity, Coolest, Hottest));
            _renderer.SetPropertyBlock(_block);

            Shine(value);

        }

    }

    // Each light at the middle of its third's glow, as strong as that third's outside seen at the stage: radiance times
    // area times the cosine it faces at, over pi, as URP's Lit has none.
    private void Shine(float temperature) {

        Vector3[] power = new Vector3[Lights];
        Vector3[] middle = new Vector3[Lights];
        float[] weight = new float[Lights];

        foreach ((int sector, float area, float share, Vector3 centre) in _faces) {

            Color glow = Radiance(temperature * share) * (_emissivity * area);
            float luminance = 0.2126f * glow.r + 0.7152f * glow.g + 0.0722f * glow.b;

            power[sector] += new Vector3(glow.r, glow.g, glow.b);
            middle[sector] += centre * luminance;
            weight[sector] += luminance;

        }

        for (int i = 0; i < Lights; i++) {

            Light light = _lights[i];
            float peak = Mathf.Max(power[i].x, Mathf.Max(power[i].y, power[i].z));

            light.enabled = peak > 0.0f;

            if (!light.enabled) {

                continue;

            }

            light.transform.localPosition = middle[i] / weight[i];
            light.color = new Color(power[i].x / peak, power[i].y / peak, power[i].z / peak).gamma;
            light.intensity = _nearby * (float)(peak * Facing / Math.PI / (MapSpace.MetresPerUnit * MapSpace.MetresPerUnit));

        }

    }

    private static float Across(Vector3 v) => Mathf.Sqrt(v.x * v.x + v.z * v.z);

    private static Color Radiance(float temperature) {

        float x = Mathf.Clamp((temperature - Coolest) / (Hottest - Coolest) * Entries - 0.5f, 0.0f, Entries - 1.0f);
        int i = Mathf.Min((int)x, Entries - 2);

        return Color.LerpUnclamped(_table[i], _table[i + 1], x - i);

    }

    // A blackbody's radiance by temperature, as linear sRGB in the renderer's units: Planck's law through the CIE 1931
    // observer as Wyman, Sloan and Shirley (2013) fit it, at 683 lm/W.
    private static Color[] Table() {

        const double h = 6.626_070e-34;
        const double c = 2.997_925e8;
        const double k = 1.380_649e-23;
        const double step = 5e-9;
        Color[] table = new Color[Entries];

        for (int i = 0; i < Entries; i++) {

            double temperature = Coolest + (i + 0.5) / Entries * (Hottest - Coolest);
            double x = 0.0;
            double y = 0.0;
            double z = 0.0;

            for (double nm = 380.0; nm <= 780.0; nm += 5.0) {

                double metres = nm * 1e-9;
                double planck = 2.0 * h * c * c / Math.Pow(metres, 5.0) / (Math.Exp(h * c / (metres * k * temperature)) - 1.0) * step;

                x += planck * (1.056 * Lobe(nm, 599.8, 37.9, 31.0) + 0.362 * Lobe(nm, 442.0, 16.0, 26.7) - 0.065 * Lobe(nm, 501.1, 20.4, 26.2));
                y += planck * (0.821 * Lobe(nm, 568.8, 46.9, 40.5) + 0.286 * Lobe(nm, 530.9, 16.3, 31.1));
                z += planck * (1.217 * Lobe(nm, 437.0, 11.8, 36.0) + 0.681 * Lobe(nm, 459.0, 26.0, 13.8));

            }

            double scale = 683.0 * RadiancePerCandela;

            table[i] = new Color(
                (float)Math.Max((3.2406 * x - 1.5372 * y - 0.4986 * z) * scale, 0.0),
                (float)Math.Max((-0.9689 * x + 1.8758 * y + 0.0415 * z) * scale, 0.0),
                (float)Math.Max((0.0557 * x - 0.2040 * y + 1.0570 * z) * scale, 0.0));

        }

        return table;

    }

    private static double Lobe(double nm, double centre, double below, double above) {

        double width = nm < centre ? below : above;

        return Math.Exp(-0.5 * (nm - centre) * (nm - centre) / (width * width));

    }

    private static Texture2D Texture(Color[] table) {

        Texture2D texture = new Texture2D(Entries, 1, TextureFormat.RGBAHalf, false, true) {

            name = "Blackbody",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontSave,

        };

        texture.SetPixels(table);
        texture.Apply(false, true);

        return texture;

    }

}
