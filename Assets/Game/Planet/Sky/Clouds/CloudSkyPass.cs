using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Planet.Sky.Clouds;

/// <summary>Before the opaque scene, maps the clouds' shadow along the sun, which the ground, water, plants and haze read,
/// and the clouds round the camera, which water mirrors over the clear sky's table.</summary>
internal sealed class CloudSkyPass : ScriptableRenderPass {

    private static readonly int ShadowId = Shader.PropertyToID("_CloudShadow");
    private static readonly int SkyId = Shader.PropertyToID("_CloudSky");

    private const int ShadowPass = 2;
    private const int SkyPass = 3;

    // Size of the sky map; the width matches CLOUD_SKY_WIDTH in Atmosphere.hlsl.
    private const int SkyWidth = 256;
    private const int SkyHeight = 96;

    private readonly Material _material;

    private sealed class PassData {

        public Material Material;
        public int Pass;

    }

    public CloudSkyPass(Material material) {

        _material = material;
        renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;

    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {

        Draw(renderGraph, new TextureDesc(CloudView.ShadowTexels, CloudView.ShadowTexels) {

            name = "Cloud Shadow",
            format = GraphicsFormat.R16G16_SFloat,
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp,

            // The air reads coarser mips over its longer steps.
            useMipMap = true,
            autoGenerateMips = true,

        }, ShadowPass, ShadowId);

        Draw(renderGraph, new TextureDesc(SkyWidth, SkyHeight) {

            name = "Cloud Sky",
            format = GraphicsFormat.R16G16B16A16_SFloat,
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp,

            // Water blurs what it mirrors by the spread of its waves, reading coarser mips as the sea roughens.
            useMipMap = true,
            autoGenerateMips = true,

        }, SkyPass, SkyId);

    }

    private void Draw(RenderGraph renderGraph, TextureDesc description, int pass, int globalId) {

        TextureHandle target = renderGraph.CreateTexture(description);

        using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(description.name, out PassData data);

        data.Material = _material;
        data.Pass = pass;

        builder.SetRenderAttachment(target, 0);
        builder.SetGlobalTextureAfterPass(target, globalId);
        builder.AllowPassCulling(false);
        builder.SetRenderFunc((PassData passData, RasterGraphContext context) => Blitter.BlitTexture(context.cmd, new Vector4(1.0f, 1.0f, 0.0f, 0.0f), passData.Material, passData.Pass));

    }

}
