using MaxQ.Game.Planet.Sky.Clouds;

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Planet.Sky;

/// <summary>URP has no planetary sky, so this pass composites the atmosphere after the opaque scene and before
/// transparents, which keeps orbit lines crisp on top: it traces the clouds, marches the air through them at half
/// resolution, then redraws the colour target through both at full resolution. URP has no eye adaptation either; the composite applies the exposure, and a
/// histogram of its result adapts the exposure for the next frame.</summary>
internal sealed class AtmospherePass : ScriptableRenderPass {

    private static readonly int SceneDepthId = Shader.PropertyToID("_SceneDepth");
    private static readonly int InscatterId = Shader.PropertyToID("_AtmosphereInscatter");
    private static readonly int TransmittanceId = Shader.PropertyToID("_AtmosphereTransmittance");
    private static readonly int SizeId = Shader.PropertyToID("_AtmosphereSize");
    private static readonly int SceneSizeId = Shader.PropertyToID("_SceneSize");
    private static readonly int ExposureId = Shader.PropertyToID("_Exposure");
    private static readonly int HistogramId = Shader.PropertyToID("_Histogram");
    private static readonly int SourceId = Shader.PropertyToID("_Source");
    private static readonly int SourceSizeId = Shader.PropertyToID("_SourceSize");
    private static readonly int DeltaTimeId = Shader.PropertyToID("_DeltaTime");
    private static readonly int AdaptingId = Shader.PropertyToID("_Adapting");
    private static readonly int CloudLightId = Shader.PropertyToID("_CloudLight");
    private static readonly int CloudDepthId = Shader.PropertyToID("_CloudDepth");
    private static readonly int CloudSizeId = Shader.PropertyToID("_CloudSize");

    // Pixels per histogram sample along each axis, and threads per group along each; must match Exposure.compute.
    private const int ExposureStride = 4;
    private const int ExposureGroup = 16;

    private const int MarchPass = 0;
    private const int CompositePass = 2;

    private readonly Material _material;
    private readonly ComputeShader _exposure;
    private readonly GraphicsBuffer _exposureValue;
    private readonly GraphicsBuffer _histogram;
    private readonly CloudView _clouds;
    private readonly int _histogramKernel;
    private readonly int _adaptKernel;

    /// <summary>Whether the eye adapts to the view; above the air it keeps to daylight exposure.</summary>
    public bool Adapting { get; set; } = true;

    private sealed class MarchData {

        public Material Material;
        public TextureHandle Source;
        public TextureHandle Depth;
        public Vector4 SceneSize;
        public TextureHandle CloudLight;
        public TextureHandle CloudDepth;
        public Vector4 CloudSize;

    }

    private sealed class CompositeData {

        public Material Material;
        public TextureHandle Source;
        public TextureHandle Depth;
        public TextureHandle Inscatter;
        public TextureHandle Transmittance;
        public BufferHandle Exposure;
        public Vector4 Size;
        public Vector4 SceneSize;

    }

    private sealed class ExposureData {

        public ComputeShader Shader;
        public int HistogramKernel;
        public int AdaptKernel;
        public TextureHandle Source;
        public BufferHandle Histogram;
        public BufferHandle Exposure;
        public Vector4 Size;
        public bool Adapting;

    }

    public AtmospherePass(Material material, ComputeShader exposure, GraphicsBuffer exposureValue, GraphicsBuffer histogram, CloudView clouds) {

        _material = material;
        _clouds = clouds;
        _exposure = exposure;
        _exposureValue = exposureValue;
        _histogram = histogram;
        _histogramKernel = exposure.FindKernel("Histogram");
        _adaptKernel = exposure.FindKernel("Adapt");
        renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
        ConfigureInput(ScriptableRenderPassInput.Depth);

    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {

        UniversalResourceData resources = frameData.Get<UniversalResourceData>();

        if (resources.isActiveTargetBackBuffer) {

            return;

        }

        TextureHandle target = resources.activeColorTexture;
        TextureHandle depth = resources.cameraDepthTexture;
        TextureDesc full = renderGraph.GetTextureDesc(target);
        full.name = "Atmosphere Source";
        full.clearBuffer = false;
        full.msaaSamples = MSAASamples.None;

        TextureHandle source = renderGraph.CreateTexture(full);
        Vector4 sceneSize = new Vector4(full.width, full.height, 1.0f / full.width, 1.0f / full.height);
        renderGraph.AddBlitPass(target, source, Vector2.one, Vector2.zero, filterMode: RenderGraphUtils.BlitFilterMode.ClampNearest, passName: "Atmosphere Copy");

        TextureDesc half = full;
        half.width = Mathf.Max(1, full.width / 2);
        half.height = Mathf.Max(1, full.height / 2);
        half.format = GraphicsFormat.R16G16B16A16_SFloat;
        half.filterMode = FilterMode.Point;
        half.name = "Atmosphere Inscatter";

        TextureHandle inscatter = renderGraph.CreateTexture(half);
        half.name = "Atmosphere Transmittance";
        TextureHandle transmittance = renderGraph.CreateTexture(half);
        BufferHandle exposure = renderGraph.ImportBuffer(_exposureValue);
        TextureHandle cloudLight = TextureHandle.nullHandle;
        TextureHandle cloudDepth = TextureHandle.nullHandle;
        Vector4 cloudSize = Vector4.zero;

        if (_clouds.Enabled) {

            _clouds.Trace.Record(renderGraph, frameData, depth, sceneSize, _clouds.Centre, _clouds.Rotation, out cloudLight, out cloudDepth, out cloudSize);

        }

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Atmosphere March", out MarchData data)) {

            data.Material = _material;
            data.Source = source;
            data.Depth = depth;
            data.SceneSize = sceneSize;
            data.CloudLight = cloudLight;
            data.CloudDepth = cloudDepth;
            data.CloudSize = cloudSize;

            builder.UseTexture(source);
            builder.UseTexture(depth);

            // The march samples the sun's shadow cascades and the clouds' shadow, both published as globals.
            builder.UseAllGlobalTextures(true);

            if (resources.mainShadowsTexture.IsValid()) {

                builder.UseTexture(resources.mainShadowsTexture);

            }

            if (cloudLight.IsValid()) {

                builder.UseTexture(cloudLight);
                builder.UseTexture(cloudDepth);

            }

            builder.SetRenderAttachment(inscatter, 0);
            builder.SetRenderAttachment(transmittance, 1);
            builder.SetRenderFunc((MarchData pass, RasterGraphContext context) => {

                pass.Material.SetTexture(SceneDepthId, pass.Depth);
                pass.Material.SetVector(SceneSizeId, pass.SceneSize);
                pass.Material.SetVector(CloudSizeId, pass.CloudSize);
                pass.Material.SetTexture(CloudLightId, pass.CloudLight.IsValid() ? pass.CloudLight : Texture2D.blackTexture);
                pass.Material.SetTexture(CloudDepthId, pass.CloudDepth.IsValid() ? pass.CloudDepth : Texture2D.blackTexture);
                Blitter.BlitTexture(context.cmd, pass.Source, new Vector4(1.0f, 1.0f, 0.0f, 0.0f), pass.Material, MarchPass);

            });

        }

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Atmosphere Composite", out CompositeData data)) {

            data.Material = _material;
            data.Source = source;
            data.Depth = depth;
            data.Inscatter = inscatter;
            data.Transmittance = transmittance;
            data.Exposure = exposure;
            data.Size = new Vector4(half.width, half.height, 1.0f / half.width, 1.0f / half.height);
            data.SceneSize = sceneSize;

            builder.UseTexture(source);
            builder.UseTexture(depth);
            builder.UseTexture(inscatter);
            builder.UseTexture(transmittance);
            builder.UseBuffer(exposure);
            builder.SetRenderAttachment(target, 0);
            builder.SetRenderFunc((CompositeData pass, RasterGraphContext context) => {

                pass.Material.SetTexture(SceneDepthId, pass.Depth);
                pass.Material.SetTexture(InscatterId, pass.Inscatter);
                pass.Material.SetTexture(TransmittanceId, pass.Transmittance);
                pass.Material.SetBuffer(ExposureId, pass.Exposure);
                pass.Material.SetVector(SizeId, pass.Size);
                pass.Material.SetVector(SceneSizeId, pass.SceneSize);
                Blitter.BlitTexture(context.cmd, pass.Source, new Vector4(1.0f, 1.0f, 0.0f, 0.0f), pass.Material, CompositePass);

            });

        }

        // Only the game camera adapts; the scene view shows the game's exposure.
        if (frameData.Get<UniversalCameraData>().cameraType != CameraType.Game) {

            return;

        }

        using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass("Exposure", out ExposureData data)) {

            data.Shader = _exposure;
            data.HistogramKernel = _histogramKernel;
            data.AdaptKernel = _adaptKernel;
            data.Source = target;
            data.Adapting = Adapting;
            data.Histogram = renderGraph.ImportBuffer(_histogram);
            data.Exposure = exposure;
            data.Size = new Vector4(full.width, full.height, 0.0f, 0.0f);

            builder.UseTexture(target);
            builder.UseBuffer(data.Histogram, AccessFlags.ReadWrite);
            builder.UseBuffer(exposure, AccessFlags.ReadWrite);
            builder.SetRenderFunc((ExposureData pass, ComputeGraphContext context) => {

                int groupsX = Mathf.CeilToInt(pass.Size.x / (ExposureStride * ExposureGroup));
                int groupsY = Mathf.CeilToInt(pass.Size.y / (ExposureStride * ExposureGroup));

                context.cmd.SetComputeTextureParam(pass.Shader, pass.HistogramKernel, SourceId, pass.Source);
                context.cmd.SetComputeBufferParam(pass.Shader, pass.HistogramKernel, HistogramId, pass.Histogram);
                context.cmd.SetComputeBufferParam(pass.Shader, pass.HistogramKernel, ExposureId, pass.Exposure);
                context.cmd.SetComputeVectorParam(pass.Shader, SourceSizeId, pass.Size);
                context.cmd.DispatchCompute(pass.Shader, pass.HistogramKernel, groupsX, groupsY, 1);

                context.cmd.SetComputeBufferParam(pass.Shader, pass.AdaptKernel, HistogramId, pass.Histogram);
                context.cmd.SetComputeBufferParam(pass.Shader, pass.AdaptKernel, ExposureId, pass.Exposure);
                context.cmd.SetComputeFloatParam(pass.Shader, DeltaTimeId, Time.unscaledDeltaTime);
                context.cmd.SetComputeFloatParam(pass.Shader, AdaptingId, pass.Adapting ? 1.0f : 0.0f);
                context.cmd.DispatchCompute(pass.Shader, pass.AdaptKernel, 1, 1, 1);

            });

        }

    }

}
