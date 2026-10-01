using System;

using MaxQ.Game.Map;
using MaxQ.Game.Vessels.Craft;
using MaxQ.Game.Vessels.Hull;

using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Vessels.Plume;

/// <summary>An engine's exhaust: one volume Exhaust.shader lights, drawn on a hull round it. The holder sits at the nozzle
/// exit with its +Y downstream; the volume is in metres.</summary>
internal sealed class ExhaustRenderer {

    private const int Segments = 48;

    // The hull stands this many of the wider lobe's widths off the axis, where its light is all but gone.
    private const float HullWidths = 3.5f;

    // Steps summing the gas's light down the axis, and the lights standing in for it.
    private const int GlowSteps = 2_000;
    private const int GlowLights = 3;

    private static readonly int ShapeId = Shader.PropertyToID("_ExhaustShape");
    private static readonly int HullId = Shader.PropertyToID("_ExhaustHull");
    private static readonly int CoreId = Shader.PropertyToID("_CoreLobe");
    private static readonly int CoreStartId = Shader.PropertyToID("_CoreStartTint");
    private static readonly int CoreEndId = Shader.PropertyToID("_CoreEndTint");
    private static readonly int ExpansionId = Shader.PropertyToID("_ExpansionLobe");
    private static readonly int ExpansionStartId = Shader.PropertyToID("_ExpansionStartTint");
    private static readonly int ExpansionEndId = Shader.PropertyToID("_ExpansionEndTint");
    private static readonly int FlowId = Shader.PropertyToID("_ExhaustFlow");
    private static readonly int StrengthId = Shader.PropertyToID("_ExhaustStrength");

    private static Mesh _hull;

    private readonly Transform _transform;
    private readonly MeshRenderer _renderer;
    private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
    private readonly Light[] _lights;
    private readonly float _intensity;
    private float _strength = -1.0f;

    public ExhaustRenderer(Material material, Transform parent, EngineEntry engine) {

        ExhaustPlume exhaust = engine.exhaust ?? throw new InvalidOperationException($"Engine '{engine.name}' has no exhaust.");

        _transform = new GameObject("Exhaust").transform;
        _transform.SetParent(parent, false);
        _transform.gameObject.AddComponent<MeshFilter>().sharedMesh = _hull ??= Hull();

        _renderer = _transform.gameObject.AddComponent<MeshRenderer>();
        _renderer.sharedMaterial = material;
        _renderer.shadowCastingMode = ShadowCastingMode.Off;
        _renderer.receiveShadows = false;
        _renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

        float length = (float)exhaust.length;
        float radius = HullWidths * (float)Math.Max(exhaust.core.radius, exhaust.expansion.radius);
        float flare = HullWidths * Mathf.Tan((float)Math.Max(exhaust.core.width, exhaust.expansion.width));
        float width = 2.0f * (radius + length * flare);

        _renderer.localBounds = new Bounds(new Vector3(0.0f, 0.5f * length, 0.0f), new Vector3(width, length, width));

        // Steps gather over the distance in which the faster-spreading lobe doubles its width at the exit.
        float softening = Mathf.Min(Doubling(exhaust.core), Doubling(exhaust.expansion));

        _block.SetVector(ShapeId, new Vector4((float)exhaust.bellDepth, softening, length, (float)engine.exitRadius));
        _block.SetVector(HullId, new Vector4(length, radius, flare, Mathf.Tan((float)exhaust.fan)));
        SetLobe(CoreId, CoreStartId, CoreEndId, exhaust.core);
        SetLobe(ExpansionId, ExpansionStartId, ExpansionEndId, exhaust.expansion);
        _block.SetVector(FlowId, new Vector4((float)exhaust.noise, (float)exhaust.tilesAcross, (float)exhaust.tileLength, (float)exhaust.speed));

        // The gas lights the stack as it leaves the bell: lights round the fan's edge an exit radius downstream, so they
        // reach the tank's walls as well as its aft end, and together as bright at the engine's mount as all the gas down
        // the axis is. No shadows, so the bell does not hide the nearest gas from the aft end as it would.
        double near = engine.length;
        double coreNear = Seen(exhaust.core, length, near);
        double expansionNear = Seen(exhaust.expansion, length, near);
        double atNear = coreNear + expansionNear;
        Vector4 colour = (float)(coreNear / atNear) * Mean(exhaust.core) + (float)(expansionNear / atNear) * Mean(exhaust.expansion);
        double drop = engine.exitRadius;
        double spread = drop * (1.0 + Math.Tan(exhaust.fan));
        double toMount = near + drop;

        // Each light's illuminance on the axis at the mount, per unit intensity: its cosine over its distance squared.
        double share = GlowLights * toMount / Math.Pow(spread * spread + toMount * toMount, 1.5);

        _lights = new Light[GlowLights];

        for (int i = 0; i < GlowLights; i++) {

            float around = 2.0f * Mathf.PI * i / GlowLights;
            Light light = new GameObject("Exhaust Glow").AddComponent<Light>();

            light.transform.SetParent(_transform, false);
            light.transform.localPosition = new Vector3((float)spread * Mathf.Cos(around), (float)drop, (float)spread * Mathf.Sin(around));
            light.type = LightType.Point;
            light.range = (float)(3.0 * (toMount + 2.0 * VesselView.Reach) / MapSpace.MetresPerUnit);
            light.color = new Color(colour.x, colour.y, colour.z).gamma;
            light.shadows = LightShadows.None;
            _lights[i] = light;

        }

        // Illuminance over pi, as URP's Lit has none, and its inverse square in scene units (km).
        _intensity = (float)(atNear / Math.PI / share / (MapSpace.MetresPerUnit * MapSpace.MetresPerUnit));

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

            foreach (Light light in _lights) {

                light.enabled = _renderer.enabled;
                light.intensity = _intensity * value;

            }

        }

    }

    public void Place(Vector3 position, Quaternion rotation) => _transform.SetLocalPositionAndRotation(position, rotation);

    private static float Doubling(ExhaustLobe lobe) => (float)(lobe.radius / Math.Tan(lobe.width));

    // Across the axis a Gaussian of width w over w^2 sums to sqrt(2 pi) / w; scaled so the exit shows the lobe's brightness.
    private void SetLobe(int lobeId, int startId, int endId, ExhaustLobe lobe) {

        double scale = lobe.brightness * lobe.radius / Math.Sqrt(2.0 * Math.PI);

        _block.SetVector(lobeId, new Vector4((float)lobe.radius, (float)Math.Tan(lobe.width), (float)scale, (float)(1.0 / lobe.reach)));
        _block.SetVector(startId, Colour(lobe.startTint));
        _block.SetVector(endId, Colour(lobe.endTint));

    }

    // Illuminance on the axis at a height above the exit from a lobe as a line of light: its Gaussian summed across gives
    // sqrt(2 pi) times its brightness and width per metre, falling by e every reach.
    private static double Seen(ExhaustLobe lobe, double length, double height) {

        double step = length / GlowSteps;
        double sum = 0.0;

        for (int i = 0; i < GlowSteps; i++) {

            double x = (i + 0.5) * step;

            sum += Math.Exp(-x / lobe.reach) / ((height + x) * (height + x));

        }

        return Math.Sqrt(2.0 * Math.PI) * lobe.brightness * lobe.radius * sum * step;

    }

    // A lobe's tint averaged over its light, which cools from start to end as it dims.
    private static Vector4 Mean(ExhaustLobe lobe) => 0.5f * (Colour(lobe.startTint) + Colour(lobe.endTint));

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
