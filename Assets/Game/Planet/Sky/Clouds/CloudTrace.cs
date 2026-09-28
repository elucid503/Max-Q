using System;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Planet.Sky.Clouds;

/// <summary>Records the clouds into the atmosphere's frame: one ray per 4x4 block through a different pixel each frame,
/// resolved into a half-resolution history per camera that is reprojected as the camera moves and the planet turns.</summary>
internal sealed class CloudTrace : IDisposable {

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
    private readonly Dictionary<Camera, History> _histories = new Dictionary<Camera, History>();

    // A camera's resolved clouds: two pairs of light and depth targets written in turn, and the frame they were seen from.
    private sealed class History : IDisposable {

        public readonly RenderTexture[] Textures = new RenderTexture[4];
        public readonly RTHandle[] Handles = new RTHandle[4];
        public readonly int Width;
        public readonly int Height;
        public int Current;
        public int Frame;
        public bool Valid;
        public Matrix4x4 ViewProjection;
        public Vector3 Centre;
        public double Rotation;
        public Vector3 Camera;

        public History(int width, int height) {

            Width = width;
            Height = height;

            for (int i = 0; i < 4; i++) {

                Textures[i] = new RenderTexture(width, height, 0, i < 2 ? GraphicsFormat.R16G16B16A16_SFloat : GraphicsFormat.R32G32_SFloat) {

                    name = i < 2 ? "Cloud History" : "Cloud History Depth",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,

                };

                Textures[i].Create();
                Handles[i] = RTHandles.Alloc(Textures[i]);

            }

        }

        public RTHandle Light(int index) => Handles[index];

        public RTHandle Depth(int index) => Handles[2 + index];

        public void Dispose() {

            for (int i = 0; i < 4; i++) {

                RTHandles.Release(Handles[i]);
                Textures[i].Release();
                UnityEngine.Object.Destroy(Textures[i]);

            }

        }

    }

    private sealed class TraceData {

        public Material Material;
        public TextureHandle Depth;
        public Vector4 SceneSize;
        public Vector4 Jitter;
        public float PixelAngle;

    }

    private sealed class ResolveData {

        public Material Material;
        public TextureHandle Trace;
        public TextureHandle TraceDepth;
        public TextureHandle History;
        public TextureHandle HistoryDepth;
        public Matrix4x4 Reprojection;
        public Vector3 PreviousCamera;
        public bool Valid;
        public Vector4 SceneSize;
        public Vector4 Size;
        public Vector4 TraceSize;
        public Vector4 Jitter;

    }

    /// <summary>Traces with <paramref name="material"/> over a planet of <paramref name="radius"/> kilometres.</summary>
    public CloudTrace(Material material, float radius) {

        _material = material;
        _radius = radius;

    }

    /// <summary>Records the trace and resolve for this frame's camera against its <paramref name="depth"/> buffer of
    /// <paramref name="sceneSize"/>, with the planet's <paramref name="centre"/> and <paramref name="rotation"/> this frame.
    /// Returns the resolved light and depth, and their size.</summary>
    public void Record(RenderGraph renderGraph, ContextContainer frameData, TextureHandle depth, Vector4 sceneSize, Vector3 centre, double rotation,
        out TextureHandle light, out TextureHandle lightDepth, out Vector4 size) {

        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        Camera camera = cameraData.camera;
        int width = Mathf.CeilToInt(sceneSize.x / Block);
        int height = Mathf.CeilToInt(sceneSize.y / Block);
        int halfWidth = Mathf.CeilToInt(sceneSize.x / 2.0f);
        int halfHeight = Mathf.CeilToInt(sceneSize.y / 2.0f);
        History history = HistoryFor(camera, halfWidth, halfHeight);

        size = new Vector4(halfWidth, halfHeight, 1.0f / halfWidth, 1.0f / halfHeight);

        // The camera's place on the turning planet, and the transform that takes this frame's scene to the last one's.
        Vector3 cameraPosition = cameraData.worldSpaceCameraPos;
        Vector3 bodyCamera = Quaternion.AngleAxis((float)(rotation * 180.0 / Math.PI), Vector3.up) * (cameraPosition - centre);
        // The atmosphere draws only into intermediate targets, so the projection is always the one for rendering into a texture.
        Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true) * cameraData.GetViewMatrix();
        Matrix4x4 reprojection = history.ViewProjection * Matrix4x4.Translate(history.Centre) *
            Matrix4x4.Rotate(Quaternion.AngleAxis((float)((rotation - history.Rotation) * 180.0 / Math.PI), Vector3.up)) * Matrix4x4.Translate(-centre);
        Vector3 previousCamera = centre + Quaternion.AngleAxis(-(float)(rotation * 180.0 / Math.PI), Vector3.up) * history.Camera;
        float altitude = Mathf.Max(bodyCamera.magnitude - _radius, 0.0f);
        bool valid = history.Valid && (bodyCamera - history.Camera).magnitude < JumpDistance + JumpPerAltitude * altitude;
        int frame = history.Frame;
        Vector2Int offset = 2 * Order[frame & 3] + Order[(frame >> 2) & 3];
        Vector4 jitter = new Vector4(offset.x, offset.y, frame, 0.0f);

        TextureDesc lightDescription = new TextureDesc(width, height) {

            name = "Cloud Trace",
            format = GraphicsFormat.R16G16B16A16_SFloat,
            filterMode = FilterMode.Point,

        };

        TextureDesc depthDescription = new TextureDesc(width, height) {

            name = "Cloud Trace Depth",
            format = GraphicsFormat.R32G32_SFloat,
            filterMode = FilterMode.Point,

        };

        TextureHandle trace = renderGraph.CreateTexture(lightDescription);
        TextureHandle traceDepth = renderGraph.CreateTexture(depthDescription);

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Cloud Trace", out TraceData data)) {

            data.Material = _material;
            data.Depth = depth;
            data.SceneSize = sceneSize;
            data.Jitter = jitter;

            // Noise is blurred to the footprint of the history's pixels.
            data.PixelAngle = 2.0f * 2.0f * Mathf.Tan(0.5f * camera.fieldOfView * Mathf.Deg2Rad) / sceneSize.y;

            builder.UseTexture(depth);
            builder.SetRenderAttachment(trace, 0);
            builder.SetRenderAttachment(traceDepth, 1);
            builder.SetRenderFunc((TraceData pass, RasterGraphContext context) => {

                pass.Material.SetTexture(SceneDepthId, pass.Depth);
                pass.Material.SetVector(SceneSizeId, pass.SceneSize);
                pass.Material.SetVector(JitterId, pass.Jitter);
                pass.Material.SetFloat(PixelAngleId, pass.PixelAngle);
                Blitter.BlitTexture(context.cmd, new Vector4(1.0f, 1.0f, 0.0f, 0.0f), pass.Material, TracePass);

            });

        }

        int next = 1 - history.Current;

        light = renderGraph.ImportTexture(history.Light(next));
        lightDepth = renderGraph.ImportTexture(history.Depth(next));

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Cloud Resolve", out ResolveData data)) {

            data.Material = _material;
            data.Trace = trace;
            data.TraceDepth = traceDepth;
            data.History = renderGraph.ImportTexture(history.Light(history.Current));
            data.HistoryDepth = renderGraph.ImportTexture(history.Depth(history.Current));
            data.Reprojection = reprojection;
            data.PreviousCamera = previousCamera;
            data.Valid = valid;
            data.SceneSize = sceneSize;
            data.Size = size;
            data.TraceSize = new Vector4(width, height, 1.0f / width, 1.0f / height);
            data.Jitter = jitter;

            builder.UseTexture(trace);
            builder.UseTexture(traceDepth);
            builder.UseTexture(data.History);
            builder.UseTexture(data.HistoryDepth);
            builder.SetRenderAttachment(light, 0);
            builder.SetRenderAttachment(lightDepth, 1);
            builder.SetRenderFunc((ResolveData pass, RasterGraphContext context) => {

                pass.Material.SetTexture(TraceId, pass.Trace);
                pass.Material.SetTexture(TraceDepthId, pass.TraceDepth);
                pass.Material.SetTexture(HistoryId, pass.History);
                pass.Material.SetTexture(HistoryDepthId, pass.HistoryDepth);
                pass.Material.SetMatrix(ReprojectionId, pass.Reprojection);
                pass.Material.SetVector(PreviousCameraId, pass.PreviousCamera);
                pass.Material.SetFloat(HistoryValidId, pass.Valid ? 1.0f : 0.0f);
                pass.Material.SetVector(SceneSizeId, pass.SceneSize);
                pass.Material.SetVector(SizeId, pass.Size);
                pass.Material.SetVector(TraceSizeId, pass.TraceSize);
                pass.Material.SetVector(JitterId, pass.Jitter);
                Blitter.BlitTexture(context.cmd, new Vector4(1.0f, 1.0f, 0.0f, 0.0f), pass.Material, ResolvePass);

            });

        }

        history.Current = next;
        history.Frame = frame + 1;
        history.Valid = true;
        history.ViewProjection = viewProjection;
        history.Centre = centre;
        history.Rotation = rotation;
        history.Camera = bodyCamera;

    }

    private History HistoryFor(Camera camera, int width, int height) {

        if (_histories.TryGetValue(camera, out History history) && history.Width == width && history.Height == height) {

            return history;

        }

        history?.Dispose();
        history = new History(width, height);
        _histories[camera] = history;

        return history;

    }

    public void Dispose() {

        foreach (History history in _histories.Values) {

            history.Dispose();

        }

        _histories.Clear();

    }

}
