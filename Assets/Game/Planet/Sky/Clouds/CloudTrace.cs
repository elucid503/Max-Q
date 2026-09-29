using System;

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Planet.Sky.Clouds;

/// <summary>Records the clouds into the atmosphere's frame: one ray per 4x4 block through a different pixel each frame,
/// resolved into a half-resolution history per camera that is reprojected as the camera moves and the planet turns. The
/// resolve weighs the rays by the ground they met, so clouds never bleed across a silhouette.</summary>
internal sealed class CloudTrace {

    private static readonly int SceneDepthId = Shader.PropertyToID("_SceneDepth");
    private static readonly int SceneSizeId = Shader.PropertyToID("_SceneSize");
    private static readonly int JitterId = Shader.PropertyToID("_CloudJitter");
    private static readonly int PixelAngleId = Shader.PropertyToID("_CloudPixelAngle");
    private static readonly int TraceId = Shader.PropertyToID("_CloudTrace");
    private static readonly int TraceDepthId = Shader.PropertyToID("_CloudTraceDepth");
    private static readonly int HistoryId = Shader.PropertyToID("_CloudHistory");
    private static readonly int HistoryDepthId = Shader.PropertyToID("_CloudHistoryDepth");
    private static readonly int ReprojectionId = Shader.PropertyToID("_CloudReprojection");
    private static readonly int HistoryValidId = Shader.PropertyToID("_CloudHistoryValid");
    private static readonly int SizeId = Shader.PropertyToID("_CloudSize");
    private static readonly int TraceSizeId = Shader.PropertyToID("_CloudTraceSize");
    private static readonly int PreviousCameraId = Shader.PropertyToID("_CloudPreviousCamera");

    private const int TracePass = 0;
    private const int ResolvePass = 1;
    private const int Block = 4;

    // History is dropped when the camera jumps farther than this in a frame (km), plus a share of its altitude.
    private const float JumpDistance = 5.0f;
    private const float JumpPerAltitude = 0.1f;

    // Each block's half-resolution pixel traced on successive frames, then the full-resolution pixel within it on each
    // round of four, both in Bayer order.
    private static readonly Vector2Int[] Order = {

        new Vector2Int(0, 0), new Vector2Int(1, 1), new Vector2Int(1, 0), new Vector2Int(0, 1),

    };

    private readonly Material _material;
    private readonly float _radius;

    // A camera's resolved clouds in URP's camera history, which swaps each pair every frame and frees them once the camera
    // stops asking; and the frame they were seen from.
    private sealed class History : CameraHistoryItem {

        private int _light;
        private int _depth;

        public int Frame;
        public bool Valid;
        public Matrix4x4 ViewProjection;
        public Vector3 Centre;
        public double Rotation;
        public Vector3 Camera;

        public override void OnCreate(BufferedRTHandleSystem owner, uint typeId) {

            base.OnCreate(owner, typeId);
            _light = MakeId(0);
            _depth = MakeId(1);

        }

        public RTHandle Light(bool previous) => previous ? GetPreviousFrameRT(_light) : GetCurrentFrameRT(_light);

        public RTHandle Depth(bool previous) => previous ? GetPreviousFrameRT(_depth) : GetCurrentFrameRT(_depth);

        public void Fit(int width, int height) {

            RTHandle light = GetCurrentFrameRT(_light);

            if (light != null && light.rt.width == width && light.rt.height == height) {

                return;

            }

            Reset();

            RenderTextureDescriptor description = new RenderTextureDescriptor(width, height, GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.None, 1);

            AllocHistoryFrameRT(_light, 2, ref description, "Cloud History");
            description.graphicsFormat = GraphicsFormat.R32G32_SFloat;
            AllocHistoryFrameRT(_depth, 2, ref description, "Cloud History Depth");

        }

        public override void Reset() {

            ReleaseHistoryFrameRT(_light);
            ReleaseHistoryFrameRT(_depth);
            Valid = false;

        }

    }

    private sealed class PassData {

        public Material Material;
        public TextureHandle Depth;
        public TextureHandle Trace;
        public TextureHandle TraceDepth;
        public TextureHandle History;
        public TextureHandle HistoryDepth;

    }

    /// <summary>Traces with <paramref name="material"/> over a planet of <paramref name="radius"/> kilometres.</summary>
    public CloudTrace(Material material, float radius) {

        _material = material;
        _radius = radius;

    }

    /// <summary>Records the trace and resolve for this frame's camera against its <paramref name="depth"/> buffer of
    /// <paramref name="sceneSize"/>, with the planet's <paramref name="centre"/> and <paramref name="rotation"/> this frame.
    /// Gives the resolved light and depth, and their size; false for a camera that keeps no history.</summary>
    public bool Record(RenderGraph renderGraph, ContextContainer frameData, TextureHandle depth, Vector4 sceneSize, Vector3 centre, double rotation,
        out TextureHandle light, out TextureHandle lightDepth, out Vector4 size) {

        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        UniversalCameraHistory histories = cameraData.historyManager;

        light = TextureHandle.nullHandle;
        lightDepth = TextureHandle.nullHandle;
        size = Vector4.zero;
        histories?.RequestAccess<History>();

        if (histories?.GetHistoryForWrite<History>() is not { } history) {

            return false;

        }

        Camera camera = cameraData.camera;
        int width = Mathf.CeilToInt(sceneSize.x / Block);
        int height = Mathf.CeilToInt(sceneSize.y / Block);
        int halfWidth = Mathf.CeilToInt(sceneSize.x / 2.0f);
        int halfHeight = Mathf.CeilToInt(sceneSize.y / 2.0f);

        history.Fit(halfWidth, halfHeight);
        size = new Vector4(halfWidth, halfHeight, 1.0f / halfWidth, 1.0f / halfHeight);

        // The camera's place on the turning planet, and the transform that takes this frame's scene to the last one's.
        Vector3 bodyCamera = Quaternion.AngleAxis((float)(rotation * 180.0 / Math.PI), Vector3.up) * (cameraData.worldSpaceCameraPos - centre);
        // The atmosphere draws only into intermediate targets, so the projection is always the one for rendering into a texture.
        Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true) * cameraData.GetViewMatrix();
        Matrix4x4 reprojection = history.ViewProjection * Matrix4x4.Translate(history.Centre) *
            Matrix4x4.Rotate(Quaternion.AngleAxis((float)((rotation - history.Rotation) * 180.0 / Math.PI), Vector3.up)) * Matrix4x4.Translate(-centre);
        Vector3 previousCamera = centre + Quaternion.AngleAxis(-(float)(rotation * 180.0 / Math.PI), Vector3.up) * history.Camera;
        float altitude = Mathf.Max(bodyCamera.magnitude - _radius, 0.0f);
        bool valid = history.Valid && (bodyCamera - history.Camera).magnitude < JumpDistance + JumpPerAltitude * altitude;
        int frame = history.Frame;
        Vector2Int offset = 2 * Order[frame & 3] + Order[(frame >> 2) & 3];

        // Each camera's graph runs as soon as it is recorded, so the material's values can be set now.
        _material.SetVector(SceneSizeId, sceneSize);
        _material.SetVector(JitterId, new Vector4(offset.x, offset.y, frame, 0.0f));
        _material.SetFloat(PixelAngleId, 2.0f * 2.0f * Mathf.Tan(0.5f * camera.fieldOfView * Mathf.Deg2Rad) / sceneSize.y);
        _material.SetMatrix(ReprojectionId, reprojection);
        _material.SetVector(PreviousCameraId, previousCamera);
        _material.SetFloat(HistoryValidId, valid ? 1.0f : 0.0f);
        _material.SetVector(SizeId, size);
        _material.SetVector(TraceSizeId, new Vector4(width, height, 1.0f / width, 1.0f / height));

        TextureHandle trace = renderGraph.CreateTexture(new TextureDesc(width, height) {

            name = "Cloud Trace",
            format = GraphicsFormat.R16G16B16A16_SFloat,
            filterMode = FilterMode.Point,

        });

        TextureHandle traceDepth = renderGraph.CreateTexture(new TextureDesc(width, height) {

            name = "Cloud Trace Depth",
            format = GraphicsFormat.R32G32_SFloat,
            filterMode = FilterMode.Point,

        });

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Cloud Trace", out PassData data)) {

            data.Material = _material;
            data.Depth = depth;

            builder.UseTexture(depth);
            builder.SetRenderAttachment(trace, 0);
            builder.SetRenderAttachment(traceDepth, 1);
            builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) => {

                pass.Material.SetTexture(SceneDepthId, pass.Depth);
                Blitter.BlitTexture(context.cmd, new Vector4(1.0f, 1.0f, 0.0f, 0.0f), pass.Material, TracePass);

            });

        }

        light = renderGraph.ImportTexture(history.Light(false));
        lightDepth = renderGraph.ImportTexture(history.Depth(false));

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Cloud Resolve", out PassData data)) {

            data.Material = _material;
            data.Depth = depth;
            data.Trace = trace;
            data.TraceDepth = traceDepth;
            data.History = renderGraph.ImportTexture(history.Light(true));
            data.HistoryDepth = renderGraph.ImportTexture(history.Depth(true));

            // Each texel is resolved against the ground its own ray meets.
            builder.UseTexture(depth);
            builder.UseTexture(trace);
            builder.UseTexture(traceDepth);
            builder.UseTexture(data.History);
            builder.UseTexture(data.HistoryDepth);
            builder.SetRenderAttachment(light, 0);
            builder.SetRenderAttachment(lightDepth, 1);
            builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) => {

                pass.Material.SetTexture(SceneDepthId, pass.Depth);
                pass.Material.SetTexture(TraceId, pass.Trace);
                pass.Material.SetTexture(TraceDepthId, pass.TraceDepth);
                pass.Material.SetTexture(HistoryId, pass.History);
                pass.Material.SetTexture(HistoryDepthId, pass.HistoryDepth);
                Blitter.BlitTexture(context.cmd, new Vector4(1.0f, 1.0f, 0.0f, 0.0f), pass.Material, ResolvePass);

            });

        }

        history.Frame = frame + 1;
        history.Valid = true;
        history.ViewProjection = viewProjection;
        history.Centre = centre;
        history.Rotation = rotation;
        history.Camera = bodyCamera;

        return true;

    }

}
