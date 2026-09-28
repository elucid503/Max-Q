using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Planet.Sky;

/// <summary>Before the opaque scene, draws small maps that the scene reads as shader globals, each one pass of a material
/// over the whole map: the sky round the camera (Hillaire's sky-view table), and the clouds' shadow and sky. Readers blur
/// them by reading coarser mips, as water does by the spread of its waves.</summary>
internal sealed class TablePass : ScriptableRenderPass {

    private readonly Material _material;
    private readonly (string Name, int Width, int Height, GraphicsFormat Format, int Pass, int GlobalId)[] _tables;

    private sealed class PassData {

        public Material Material;
        public int Pass;

    }

    public TablePass(Material material, params (string Name, int Width, int Height, GraphicsFormat Format, int Pass, int GlobalId)[] tables) {

        _material = material;
        _tables = tables;
        renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;

    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {

        foreach ((string name, int width, int height, GraphicsFormat format, int pass, int globalId) in _tables) {

            TextureHandle target = renderGraph.CreateTexture(new TextureDesc(width, height) {

                name = name,
                format = format,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = true,
                autoGenerateMips = true,

            });

            using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(name, out PassData data);

            data.Material = _material;
            data.Pass = pass;

            builder.SetRenderAttachment(target, 0);
            builder.SetGlobalTextureAfterPass(target, globalId);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (PassData passData, RasterGraphContext context) =>
                Blitter.BlitTexture(context.cmd, new Vector4(1.0f, 1.0f, 0.0f, 0.0f), passData.Material, passData.Pass));

        }

    }

}
