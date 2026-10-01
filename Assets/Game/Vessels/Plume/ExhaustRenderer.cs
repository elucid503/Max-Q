using System;

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
