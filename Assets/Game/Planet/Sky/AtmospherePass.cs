using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Planet.Sky;

/// <summary>URP has no planetary sky, so this pass composites the atmosphere after the opaque scene and before
/// transparents, which keeps orbit lines crisp on top: it marches the air at half resolution, then redraws the colour
/// target through it at full resolution.</summary>
internal sealed class AtmospherePass : ScriptableRenderPass {

    private static readonly int SceneDepthId = Shader.PropertyToID("_SceneDepth");
    private static readonly int InscatterId = Shader.PropertyToID("_AtmosphereInscatter");
    private static readonly int TransmittanceId = Shader.PropertyToID("_AtmosphereTransmittance");
    private static readonly int SizeId = Shader.PropertyToID("_AtmosphereSize");

    private const int MarchPass = 0;
    private const int CompositePass = 2;

    private readonly Material _material;

    private sealed class MarchData {

        public Material Material;
        public TextureHandle Source;
        public TextureHandle Depth;

    }

    private sealed class CompositeData {

        public Material Material;
        public TextureHandle Source;
        public TextureHandle Depth;
        public TextureHandle Inscatter;
        public TextureHandle Transmittance;
        public Vector4 Size;

    }

    public AtmospherePass(Material material) {

        _material = material;
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

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Atmosphere March", out MarchData data)) {

            data.Material = _material;
            data.Source = source;
            data.Depth = depth;

            builder.UseTexture(source);
            builder.UseTexture(depth);
            builder.SetRenderAttachment(inscatter, 0);
            builder.SetRenderAttachment(transmittance, 1);
            builder.SetRenderFunc((MarchData pass, RasterGraphContext context) => {

                pass.Material.SetTexture(SceneDepthId, pass.Depth);
                Blitter.BlitTexture(context.cmd, pass.Source, new Vector4(1.0f, 1.0f, 0.0f, 0.0f), pass.Material, MarchPass);

            });

        }

        using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Atmosphere Composite", out CompositeData data)) {

            data.Material = _material;
            data.Source = source;
            data.Depth = depth;
            data.Inscatter = inscatter;
            data.Transmittance = transmittance;
            data.Size = new Vector4(half.width, half.height, 1.0f / half.width, 1.0f / half.height);

            builder.UseTexture(source);
            builder.UseTexture(depth);
            builder.UseTexture(inscatter);
            builder.UseTexture(transmittance);
            builder.SetRenderAttachment(target, 0);
            builder.SetRenderFunc((CompositeData pass, RasterGraphContext context) => {

                pass.Material.SetTexture(SceneDepthId, pass.Depth);
                pass.Material.SetTexture(InscatterId, pass.Inscatter);
                pass.Material.SetTexture(TransmittanceId, pass.Transmittance);
                pass.Material.SetVector(SizeId, pass.Size);
                Blitter.BlitTexture(context.cmd, pass.Source, new Vector4(1.0f, 1.0f, 0.0f, 0.0f), pass.Material, CompositePass);

            });

        }

    }

}
