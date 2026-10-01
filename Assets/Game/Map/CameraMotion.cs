using System;

using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Map;

/// <summary>Motion vectors for temporal antialiasing. The scene's origin rides with the camera and the ground turns with its
/// body, so URP's own, which take the scene to stand still, would smear everything the camera passes; these carry each
/// pixel with the body the camera is over, and overwrite URP's before the antialiasing reads them.</summary>
public sealed class CameraMotion : IDisposable {

    private static readonly int SceneDepthId = Shader.PropertyToID("_SceneDepth");
    private static readonly int SceneSizeId = Shader.PropertyToID("_SceneSize");
    private static readonly int ViewProjectionId = Shader.PropertyToID("_MotionViewProjection");
    private static readonly int PreviousViewProjectionId = Shader.PropertyToID("_MotionPreviousViewProjection");
    private static readonly int CentreId = Shader.PropertyToID("_MotionCentre");
    private static readonly int ShiftId = Shader.PropertyToID("_MotionShift");
    private static readonly int TurnId = Shader.PropertyToID("_MotionTurn");
    private static readonly int KeepId = Shader.PropertyToID("_MotionKeep");

    private readonly Material _material;
    private readonly Pass _pass;

    // The body's centre from the scene's origin (sim axes, metres) and its rotation, this frame and when last drawn.
    private Vector3d _offset;
    private double _rotation;
    private double _keep;
    private Vector3d _previousOffset;
    private double _previousRotation;
    private Matrix4x4 _previousViewProjection;
    private bool _drawn;

    private sealed class PassData {

        public Material Material;
        public TextureHandle Depth;

    }

    private sealed class Pass : ScriptableRenderPass {

        private readonly CameraMotion _owner;

        public Pass(CameraMotion owner) {

            _owner = owner;
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Motion);

        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();

            if (resources.isActiveTargetBackBuffer || !resources.motionVectorColor.IsValid() || !resources.cameraDepthTexture.IsValid()) {

                return;

            }

            TextureDesc target = renderGraph.GetTextureDesc(resources.motionVectorColor);

            // Each camera's graph runs as soon as it is recorded, so the material's values can be set now.
            _owner.Prepare(frameData.Get<UniversalCameraData>(), new Vector4(target.width, target.height, 1.0f / target.width, 1.0f / target.height));

            using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Camera Motion", out PassData data);

            data.Material = _owner._material;
            data.Depth = resources.cameraDepthTexture;

            builder.UseTexture(data.Depth);
            builder.SetRenderAttachment(resources.motionVectorColor, 0);
            builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) => {

                pass.Material.SetTexture(SceneDepthId, pass.Depth);
                Blitter.BlitTexture(context.cmd, new Vector4(1.0f, 1.0f, 0.0f, 0.0f), pass.Material, 0);

            });

        }

    }

    public CameraMotion(Shader shader) {

        _material = new Material(shader);
        _pass = new Pass(this);
        RenderPipelineManager.beginCameraRendering += Enqueue;

    }

    /// <summary>The camera is over <paramref name="body"/> at sim <paramref name="time"/>; call after the origin moves.
    /// Pixels nearer than <paramref name="keep"/> metres are a vessel riding with the camera, whose own motion URP has
    /// already drawn.</summary>
    public void Update(CelestialBody body, double time, double keep = 0.0) {

        _offset = body.PositionAt(time) - MapSpace.Origin;
        _rotation = body.RotationAt(time);
        _keep = keep;

    }

    private void Prepare(UniversalCameraData camera, Vector4 size) {

        // Rendering goes to intermediate targets, so the projection is always the one for rendering into a texture.
        Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(camera.camera.projectionMatrix, true) * camera.GetViewMatrix();

        if (!_drawn) {

            _previousOffset = _offset;
            _previousRotation = _rotation;
            _previousViewProjection = viewProjection;
            _drawn = true;

        }

        // Last frame's body in this frame's axes: the scene's axes are the sim's, so a turn about the sim's pole is one
        // about the scene's up, the other way round for the change of hand.
        double turn = Math.IEEERemainder(_rotation - _previousRotation, 2.0 * Math.PI);
        float sine = (float)Math.Sin(turn);
        float versine = (float)(-2.0 * Math.Sin(0.5 * turn) * Math.Sin(0.5 * turn));
        Matrix4x4 turned = Matrix4x4.zero;

        turned.m00 = versine;
        turned.m02 = sine;
        turned.m20 = -sine;
        turned.m22 = versine;

        _material.SetVector(SceneSizeId, size);
        _material.SetMatrix(ViewProjectionId, viewProjection);
        _material.SetMatrix(PreviousViewProjectionId, _previousViewProjection);
        _material.SetVector(CentreId, MapSpace.Direction(_offset / MapSpace.MetresPerUnit));
        _material.SetVector(ShiftId, MapSpace.Direction((_previousOffset - _offset) / MapSpace.MetresPerUnit));
        _material.SetMatrix(TurnId, turned);
        _material.SetFloat(KeepId, (float)(_keep / MapSpace.MetresPerUnit));

        _previousOffset = _offset;
        _previousRotation = _rotation;
        _previousViewProjection = viewProjection;

    }

    private void Enqueue(ScriptableRenderContext context, Camera camera) {

        if (camera.cameraType != CameraType.Game) {

            return;

        }

        camera.GetUniversalAdditionalCameraData().scriptableRenderer.EnqueuePass(_pass);

    }

    public void Dispose() {

        RenderPipelineManager.beginCameraRendering -= Enqueue;
        UnityEngine.Object.Destroy(_material);

    }

}
