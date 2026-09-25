using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Planet.Sky;

/// <summary>Renders the sky around the camera into a small table before the opaque scene, so water can mirror the sky
/// it actually faces (Hillaire's sky-view table).</summary>
internal sealed class SkyViewPass : ScriptableRenderPass {

    private static readonly int SkyViewId = Shader.PropertyToID("_SkyViewLut");

    private const int Pass = 1;

    private readonly Material _material;

    private sealed class PassData {

        public Material Material;

    }

    public SkyViewPass(Material material) {

        _material = material;
        renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;

    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {

        TextureDesc description = new TextureDesc(192, 108) {

            name = "Sky View",
            format = GraphicsFormat.R16G16B16A16_SFloat,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,

        };

        TextureHandle table = renderGraph.CreateTexture(description);

        using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Atmosphere Sky View", out PassData data);

        data.Material = _material;

        builder.SetRenderAttachment(table, 0);
        builder.SetGlobalTextureAfterPass(table, SkyViewId);
        builder.SetRenderFunc((PassData pass, RasterGraphContext context) => Blitter.BlitTexture(context.cmd, new Vector4(1.0f, 1.0f, 0.0f, 0.0f), pass.Material, Pass));

    }

}
