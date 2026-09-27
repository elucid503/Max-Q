using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Planet.Water.Surface;

/// <summary>Draws the water sheet between the opaque scene and the air. It copies the scene's colour and depth, which the
/// water samples for the bed beneath it and the scenery it mirrors, then draws every renderer's MaxQWater pass into the
/// camera's colour and depth. It never asks URP for the depth texture: the air's composite is then the first pass that
/// does, so URP copies depth after the water, and the haze lies on the water's surface rather than on the bed under it.
/// A pass reading depth earlier than the skybox would move that copy ahead of the water.</summary>
internal sealed class WaterPass : ScriptableRenderPass {

    private static readonly ShaderTagId Tag = new ShaderTagId("MaxQWater");
    private static readonly int SceneColourId = Shader.PropertyToID("_WaterSceneColour");
    private static readonly int SceneDepthId = Shader.PropertyToID("_WaterSceneDepth");

    private readonly Material _copy;

    private sealed class DrawData {

        public RendererListHandle Renderers;
        public TextureHandle Colour;
        public TextureHandle Depth;

    }

    public WaterPass(Material copy) {

        _copy = copy;
        renderPassEvent = RenderPassEvent.BeforeRenderingSkybox;

    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {

        UniversalResourceData resources = frameData.Get<UniversalResourceData>();

        if (resources.isActiveTargetBackBuffer) {

            return;

        }

        UniversalRenderingData rendering = frameData.Get<UniversalRenderingData>();
        UniversalCameraData camera = frameData.Get<UniversalCameraData>();
        UniversalLightData lights = frameData.Get<UniversalLightData>();

        TextureHandle colour = resources.activeColorTexture;
        TextureHandle depth = resources.activeDepthTexture;
        TextureDesc description = renderGraph.GetTextureDesc(colour);
        description.name = "Water Scene Colour";
        description.clearBuffer = false;
        description.msaaSamples = MSAASamples.None;

        TextureHandle sceneColour = renderGraph.CreateTexture(description);
        renderGraph.AddBlitPass(colour, sceneColour, Vector2.one, Vector2.zero, passName: "Water Scene Colour");

        // Its mips tell the reflections where anything but sky lies: sky leaves the depth at zero, so a coarse texel
        // averages above zero wherever something solid shows within it.
        description.name = "Water Scene Depth";
        description.format = GraphicsFormat.R32_SFloat;
        description.filterMode = FilterMode.Point;
        description.useMipMap = true;
        description.autoGenerateMips = true;

        TextureHandle sceneDepth = renderGraph.CreateTexture(description);
        renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(depth, sceneDepth, _copy, 0), "Water Scene Depth");

        DrawingSettings drawing = RenderingUtils.CreateDrawingSettings(Tag, rendering, camera, lights, SortingCriteria.CommonOpaque);
        RendererListHandle renderers = renderGraph.CreateRendererList(new RendererListParams(rendering.cullResults, drawing, new FilteringSettings(RenderQueueRange.all)));

        using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Water", out DrawData data);

        data.Renderers = renderers;
        data.Colour = sceneColour;
        data.Depth = sceneDepth;

        builder.UseRendererList(renderers);
        builder.UseTexture(sceneColour);
        builder.UseTexture(sceneDepth);
        builder.UseAllGlobalTextures(true);

        // The water samples the sun's shadow cascades, which URP publishes as a global.
        if (resources.mainShadowsTexture.IsValid()) {

            builder.UseTexture(resources.mainShadowsTexture);

        }

        builder.SetRenderAttachment(colour, 0);
        builder.SetRenderAttachmentDepth(depth, AccessFlags.ReadWrite);
        builder.AllowGlobalStateModification(true);
        builder.SetRenderFunc((DrawData pass, RasterGraphContext context) => {

            context.cmd.SetGlobalTexture(SceneColourId, pass.Colour);
            context.cmd.SetGlobalTexture(SceneDepthId, pass.Depth);
            context.cmd.DrawRendererList(pass.Renderers);

        });

    }

}
