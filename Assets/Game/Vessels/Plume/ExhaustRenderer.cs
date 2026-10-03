using System;

using MaxQ.Game.Map;
using MaxQ.Game.Vessels.Craft;
using MaxQ.Game.Vessels.Hull;

using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Vessels.Plume;

/// <summary>An engine's exhaust, or a cluster's: one volume Exhaust.shader lights from the catalogue's layers, drawn on a
/// hull round it. The holder sits at the exit plane with its +Y downstream, each engine's exit offset from it in X and Z;
/// the volume is in metres. Each layer's numbers follow the pressure round the exit, as Waterfall's atmosphereDepth
/// modifiers do.</summary>
internal sealed class ExhaustRenderer {

    private const int Segments = 48;

    // Most layers and engines one volume holds, and the vectors each layer takes; match Exhaust.shader.
    private const int MostLayers = 4;
    private const int LayerVectors = 6;
    private const int MostNozzles = 9;

    // The hull stands this many of a layer's widths off its axis, where its light is all but gone.
    private const float HullWidths = 3.0f;

    // The air shares the hull is sized over, from vacuum to sea level, and the points down each layer it is checked at.
    private static readonly double[] HullAirs = { 0.0, 0.05, 0.2, 0.5, 1.0 };
    private const int HullSamples = 8;

    // Steps summing the gas's light down each layer, and the lights standing in for it.
    private const int GlowSteps = 200;
    private const int GlowLights = 3;

    private const double SeaLevelPressure = 101_325.0;

    private static readonly int ShapeId = Shader.PropertyToID("_ExhaustShape");
    private static readonly int HullId = Shader.PropertyToID("_ExhaustHull");
    private static readonly int LayersId = Shader.PropertyToID("_ExhaustLayers");
    private static readonly int LayerCountsId = Shader.PropertyToID("_ExhaustLayerCounts");
    private static readonly int FlowId = Shader.PropertyToID("_ExhaustFlow");
    private static readonly int TurbulenceId = Shader.PropertyToID("_ExhaustTurbulence");
    private static readonly int NozzlesId = Shader.PropertyToID("_ExhaustNozzles");
    private static readonly int NozzleCountId = Shader.PropertyToID("_ExhaustNozzleCount");
    private static readonly int StrengthId = Shader.PropertyToID("_ExhaustStrength");

    private static Mesh _hull;

    private readonly Transform _transform;
    private readonly MeshRenderer _renderer;
    private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
    private readonly ExhaustPlume _plume;
    private readonly double _engineLength;
    private readonly double _exitRadius;
    private readonly int _engines;
    private readonly Vector4[] _layers = new Vector4[MostLayers * LayerVectors];
    private readonly Light[] _lights = new Light[GlowLights];
    private readonly float _lightScale;
    private float _strength = -1.0f;
    private float _air = -1.0f;
    private float _intensity;
    private Color _colour;

    /// <summary>The exhaust of the engines whose exits stand at <paramref name="nozzles"/> (X and Z, metres) in the holder's
    /// frame, its lights standing for them all, reaching a stack <paramref name="reach"/> metres long.</summary>
    public ExhaustRenderer(Material material, Transform parent, EngineEntry engine, Vector2[] nozzles, double reach) {

        _plume = engine.exhaust ?? throw new InvalidOperationException($"Engine '{engine.name}' has no exhaust.");

        if (_plume.layers is not { Length: > 0 and <= MostLayers }) {

            throw new InvalidOperationException($"Engine '{engine.name}' needs 1 to {MostLayers} exhaust layers.");

        }

        if (nozzles.Length is 0 or > MostNozzles) {

            throw new ArgumentException($"An exhaust holds 1 to {MostNozzles} engines, not {nozzles.Length}.", nameof(nozzles));

        }

        _engineLength = engine.length;
        _exitRadius = engine.exitRadius;
        _engines = nozzles.Length;

        double across = 0.0;
        Vector4[] exits = new Vector4[MostNozzles];

        for (int i = 0; i < nozzles.Length; i++) {

            exits[i] = new Vector4(nozzles[i].x, nozzles[i].y, 0.0f, 0.0f);
            across = Math.Max(across, nozzles[i].magnitude);

        }

        _transform = new GameObject("Exhaust").transform;
        _transform.SetParent(parent, false);
        _transform.gameObject.AddComponent<MeshFilter>().sharedMesh = _hull ??= Hull();

        _renderer = _transform.gameObject.AddComponent<MeshRenderer>();
        _renderer.sharedMaterial = material;
        _renderer.shadowCastingMode = ShadowCastingMode.Off;
        _renderer.receiveShadows = false;
        _renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

        // The hull holds every layer at every air: its length, and a frustum round the widest each grows to.
        float length = 0.0f;
        float radius = 0.0f;

        foreach (ExhaustLayer layer in _plume.layers) {

            float round = layer.merged ? 0.0f : (float)across;

            foreach (double air in HullAirs) {

                length = Math.Max(length, (float)AirCurve.At(layer.length, air, 0.0));
                radius = Math.Max(radius, HullWidths * (float)AirCurve.At(layer.radius, air, 0.0) + round);

            }

        }

        float flare = 0.0f;

        foreach (ExhaustLayer layer in _plume.layers) {

            float round = layer.merged ? 0.0f : (float)across;

            foreach (double air in HullAirs) {

                double reachOf = AirCurve.At(layer.length, air, 0.0);

                for (int s = 1; s <= HullSamples && reachOf > 0.0; s++) {

                    double x = reachOf * s / HullSamples;

                    flare = Math.Max(flare, (HullWidths * (float)Width(layer, air, x) + round - radius) / (float)x);

                }

            }

        }

        float width = 2.0f * (radius + length * flare);

        _renderer.localBounds = new Bounds(new Vector3(0.0f, 0.5f * length, 0.0f), new Vector3(width, length, width));
        _block.SetVector(HullId, new Vector4(length, radius, flare, 0.0f));
        _block.SetVectorArray(NozzlesId, exits);
        _block.SetFloat(NozzleCountId, _engines);
        _block.SetVector(FlowId, new Vector4((float)_plume.tilesAcross, (float)_plume.tileLength, (float)_plume.speed, (float)_plume.eddy));
        _block.SetVector(TurbulenceId, new Vector4((float)Math.Max(_plume.transition, 1e-3), (float)_plume.diamondSpacing,
            (float)(1.0 / (_plume.diamondSpacing * _plume.diamondCount)), 0.0f));

        // The gas lights the stack as it leaves the bells: lights round the fan's edge an exit radius downstream, or round the
        // cluster, so they reach the tank's walls as well as its aft end, and together as bright at the engine's mount as all
        // the gas down the axis is. No shadows, so a bell does not hide the nearest gas from the aft end.
        double drop = engine.exitRadius;
        double spread = Math.Max(2.0 * drop, across);
        double toMount = engine.length + drop;

        // Each light's illuminance on the axis at the mount, per unit intensity: its cosine over its distance squared.
        double share = GlowLights * toMount / Math.Pow(spread * spread + toMount * toMount, 1.5);

        _lightScale = (float)(1.0 / share);

        for (int i = 0; i < GlowLights; i++) {

            float around = 2.0f * Mathf.PI * i / GlowLights;
            Light light = new GameObject("Exhaust Glow").AddComponent<Light>();

            light.transform.SetParent(_transform, false);
            light.transform.localPosition = new Vector3((float)spread * Mathf.Cos(around), (float)drop, (float)spread * Mathf.Sin(around));
            light.type = LightType.Point;
            light.range = (float)(3.0 * (toMount + 2.0 * reach) / MapSpace.MetresPerUnit);
            light.shadows = LightShadows.None;
            _lights[i] = light;

        }

        Pressure = 0.0f;
        Strength = 0.0f;

    }

    /// <summary>How hard the engine runs, 0 to 1: its chamber's pressure.</summary>
    public float Strength {

        set {

            if (Mathf.Approximately(value, _strength)) {

                return;

            }

            _strength = value;
            _renderer.enabled = value > 1e-3f;
            _block.SetFloat(StrengthId, value);
            _renderer.SetPropertyBlock(_block);
            Shine();

        }

    }

    /// <summary>Pressure of the air round the exit, Pa.</summary>
    public float Pressure {

        set {

            float air = (float)Math.Clamp(value / SeaLevelPressure, 0.0, 1.0);

            if (Math.Abs(air - _air) < 1e-3f) {

                return;

            }

            _air = air;
            Shape(air);
            _renderer.SetPropertyBlock(_block);
            Shine();

        }

    }

    public void Place(Vector3 position, Quaternion rotation) => _transform.SetLocalPositionAndRotation(position, rotation);

    // A layer's width a distance past the exit, at an air share; it necks no narrower than a quarter of its exit width.
    private static double Width(ExhaustLayer layer, double air, double x) {

        double radius = AirCurve.At(layer.radius, air, 0.0);
        double length = Math.Max(AirCurve.At(layer.length, air, 0.0), 1e-3);
        double width = radius + x * (AirCurve.At(layer.linear, air, 0.0) + x * AirCurve.At(layer.square, air, 0.0))
            + AirCurve.At(layer.bounded, air, 0.0) * (1.0 - Math.Exp(-3.0 * x / length));

        return Math.Max(width, 0.25 * radius);

    }

    // Packs the layers shining at this air for the shader, each engine's first and merged ones after, and works out the
    // light they throw.
    private void Shape(float air) {

        int own = 0;
        int count = 0;
        double sum = 0.0;
        Vector4 colour = Vector4.zero;

        foreach (bool merged in new[] { false, true }) {

            foreach (ExhaustLayer layer in _plume.layers) {

                double brightness = AirCurve.At(layer.brightness, air, 0.0);

                if (layer.merged != merged || brightness <= 0.0) {

                    continue;

                }

                int at = count * LayerVectors;
                Vector4 start = Colour(layer.startTint);
                Vector4 end = Colour(layer.endTint);

                _layers[at] = new Vector4(F(layer.radius, 0.0), F(layer.linear, 0.0), F(layer.square, 0.0), F(layer.bounded, 0.0));
                _layers[at + 1] = new Vector4(Mathf.Max(F(layer.length, 0.0), 1e-3f), F(layer.falloff, 1.0), F(layer.fadeIn, 0.0), F(layer.fadeOut, 0.0));
                _layers[at + 2] = new Vector4((float)brightness, F(layer.tintFalloff, 1.0), Mathf.Max(F(layer.sharpness, 1.0), 0.1f), F(layer.hollow, 0.0));
                _layers[at + 3] = new Vector4(F(layer.noise, 0.0), F(layer.ragged, 0.0), F(layer.diamond, 0.0), 0.0f);
                _layers[at + 4] = start;
                _layers[at + 5] = end;

                double line = Line(layer, air, brightness) * (merged ? 1.0 : _engines);

                sum += line;
                colour += (float)line * 0.5f * (start + end);
                count++;
                own += merged ? 0 : 1;

            }

        }

        // Steps gather over the distance in which the first layer doubles its width at the exit.
        float doubling = count > 0 ? _layers[0].x / Mathf.Max(_layers[0].y, 0.01f) : 1.0f;

        _block.SetVector(ShapeId, new Vector4((float)_plume.bellDepth, doubling, 0.0f, (float)_exitRadius));
        _block.SetVectorArray(LayersId, _layers);
        _block.SetVector(LayerCountsId, new Vector4(own, count, 0.0f, 0.0f));

        // Illuminance over pi at the mount, as URP's Lit has none, in scene units (km) for its inverse square.
        _intensity = (float)(sum / Math.PI / (MapSpace.MetresPerUnit * MapSpace.MetresPerUnit));
        _colour = sum > 0.0 ? new Color(colour.x, colour.y, colour.z) / (float)sum : Color.white;

        float F(double[] keys, double fallback) => (float)AirCurve.At(keys, air, fallback);

    }

    // Illuminance on the axis at the engine's mount from a layer as a line of light: across it the light sums to
    // sqrt(2 pi) times its brightness and its width at the exit per metre, fading down its length.
    private double Line(ExhaustLayer layer, double air, double brightness) {

        double length = Math.Max(AirCurve.At(layer.length, air, 0.0), 1e-3);
        double falloff = AirCurve.At(layer.falloff, air, 1.0);
        double fadeIn = AirCurve.At(layer.fadeIn, air, 0.0);
        double radius = AirCurve.At(layer.radius, air, 0.0);
        double step = length / GlowSteps;
        double sum = 0.0;

        for (int i = 0; i < GlowSteps; i++) {

            double x = (i + 0.5) * step;
            double share = x / length;
            double shown = Math.Pow(1.0 - share, falloff) * (fadeIn > 0.0 ? Math.Min(share / fadeIn, 1.0) : 1.0);

            sum += shown / ((_engineLength + x) * (_engineLength + x));

        }

        return Math.Sqrt(2.0 * Math.PI) * brightness * radius * sum * step;

    }

    private void Shine() {

        if (_strength < 0.0f || _air < 0.0f) {

            return;

        }

        foreach (Light light in _lights) {

            light.enabled = _renderer.enabled;
            light.intensity = _intensity * _lightScale * _strength;
            light.color = _colour.gamma;

        }

    }

    private static Vector4 Colour(double[] rgb) => rgb is { Length: 3 } ? new Vector4((float)rgb[0], (float)rgb[1], (float)rgb[2], 0.0f) : Vector4.one;

    // A closed unit cylinder from y = 0 to 1, faces outwards; the shader stretches it to the frustum.
    private static Mesh Hull() {

        MeshBuilder builder = new MeshBuilder(1);

        builder.Lathe(new[] { new Vector2(0.0f, 0.0f), new Vector2(1.0f, 0.0f), new Vector2(1.0f, 1.0f), new Vector2(0.0f, 1.0f) }, Segments, 0);

        Mesh mesh = builder.Build("Exhaust Hull");

        mesh.hideFlags = HideFlags.DontSave;

        return mesh;

    }

}
