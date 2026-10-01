using System;
using System.Collections.Generic;

using MaxQ.Game.Vessels.Craft;
using MaxQ.Game.Vessels.Hull;

using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Vessels.Plume;

/// <summary>One nozzle's exhaust: its catalogue layers, each an open tube Plume.shader flares and lights. The holder sits at
/// the nozzle exit with its +Y downstream; the layers are in metres.</summary>
internal sealed class PlumeRenderer {

    // A hydrazine pulse lights in a few milliseconds and its gas thins out within a couple of tenths of a second.
    private const float AttackSeconds = 0.01f;
    private const float DecaySeconds = 0.12f;

    // The tube: rings along the length, and edges round it.
    private const int Rings = 40;
    private const int Segments = 64;

    private static readonly int ShapeId = Shader.PropertyToID("_PlumeShape");
    private static readonly int OffsetId = Shader.PropertyToID("_PlumeOffset");
    private static readonly int LightId = Shader.PropertyToID("_PlumeLight");
    private static readonly int FresnelId = Shader.PropertyToID("_PlumeFresnel");
    private static readonly int FlowId = Shader.PropertyToID("_PlumeFlow");
    private static readonly int StartTintId = Shader.PropertyToID("_PlumeStartTint");
    private static readonly int EndTintId = Shader.PropertyToID("_PlumeEndTint");
    private static readonly int StrengthId = Shader.PropertyToID("_PlumeStrength");

    private static Mesh _tube;
    private static int _made;

    private readonly Transform _transform;
    private readonly List<(MeshRenderer Renderer, MaterialPropertyBlock Block)> _layers = new List<(MeshRenderer, MaterialPropertyBlock)>();
    private float _strength = -1.0f;

    public PlumeRenderer(Material material, Transform parent, PlumeLayer[] layers) {

        _transform = new GameObject("Plume").transform;
        _transform.SetParent(parent, false);

        // Each nozzle's noise starts somewhere else, so neighbouring jets never stream in step.
        float seed = (float)new System.Random(++_made).NextDouble() * 100.0f;

        foreach (PlumeLayer layer in layers ?? Array.Empty<PlumeLayer>()) {

            GameObject tube = new GameObject(layer.name);

            tube.transform.SetParent(_transform, false);
            tube.AddComponent<MeshFilter>().sharedMesh = _tube ??= Tube();

            MeshRenderer renderer = tube.AddComponent<MeshRenderer>();

            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.enabled = false;

            // The tube's bounds are a unit cylinder; the shader flares it out to the layer's full width and length.
            float reach = (float)(layer.radius + layer.spread * layer.length + layer.bounded);

            renderer.localBounds = new Bounds(new Vector3(0.0f, (float)(layer.offset + 0.5 * layer.length), 0.0f), new Vector3(2.0f * reach, (float)layer.length, 2.0f * reach));

            MaterialPropertyBlock block = new MaterialPropertyBlock();

            block.SetVector(ShapeId, new Vector4((float)layer.radius, (float)layer.length, (float)layer.spread, (float)layer.bounded));
            block.SetFloat(OffsetId, (float)layer.offset);
            block.SetVector(LightId, new Vector4((float)layer.falloff, (float)layer.fadeIn, (float)layer.tintFalloff, (float)layer.brightness));
            block.SetVector(FresnelId, new Vector4((float)layer.fresnel, (float)layer.fresnelInvert, (float)layer.noise, seed));
            block.SetVector(FlowId, new Vector4(Mathf.Max(1.0f, Mathf.Round((float)layer.tilesAround)), (float)layer.tilesAlong, (float)layer.speed, 0.0f));
            block.SetVector(StartTintId, Colour(layer.startTint));
            block.SetVector(EndTintId, Colour(layer.endTint));

            _layers.Add((renderer, block));

        }

        Strength = 0.0f;

    }

    /// <summary>How hard the nozzle runs, 0 to 1: the chamber's pressure, or an RCS pulse's envelope.</summary>
    public float Strength {

        set {

            if (Mathf.Approximately(value, _strength)) {

                return;

            }

            _strength = value;

            foreach ((MeshRenderer renderer, MaterialPropertyBlock block) in _layers) {

                renderer.enabled = value > 1e-3f;
                block.SetFloat(StrengthId, value);
                renderer.SetPropertyBlock(block);

            }

        }

    }

    public void Place(Vector3 position, Quaternion rotation) => _transform.SetLocalPositionAndRotation(position, rotation);

    /// <summary>Follows an RCS nozzle's pulses: fast up while it fires, a short fade after.</summary>
    public void Pulse(bool firing, float deltaSeconds) {

        float strength = firing ? Math.Min(1.0f, Math.Max(_strength, 0.0f) + deltaSeconds / AttackSeconds) : _strength * Mathf.Exp(-deltaSeconds / DecaySeconds);

        Strength = strength < 1e-3f ? 0.0f : strength;

    }

    private static Vector4 Colour(double[] rgb) => rgb is { Length: 3 } ? new Vector4((float)rgb[0], (float)rgb[1], (float)rgb[2], 0.0f) : Vector4.one;

    // A unit open tube from y = 0 to 1; UV runs round it in radians, then along it from 0 to 1.
    private static Mesh Tube() {

        MeshBuilder builder = new MeshBuilder(1);
        Vector2[] profile = new Vector2[Rings + 1];

        for (int i = 0; i <= Rings; i++) {

            profile[i] = new Vector2(1.0f, (float)i / Rings);

        }

        builder.Lathe(profile, Segments, 0);

        Mesh mesh = builder.Build("Plume Tube");

        mesh.hideFlags = HideFlags.DontSave;

        return mesh;

    }

}
