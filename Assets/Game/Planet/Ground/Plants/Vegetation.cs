using System;

using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Planet.Ground.Plants;

/// <summary>Grass and trees. Each patch that strews plants (see PatchStrewJob) hands over the ones its ground's cover
/// grows, sorted by a random rank, and draws the first of them that its distance keeps: grass tufts of procedural blades
/// near the camera, thinning with distance, and trees, conifers and broadleaves, of foliage cards and then of single
/// painted cards. Past the trees, the coarser levels' patches draw groves, one card for the trees of each cell, their
/// cells doubling in size as the distance doubles, so each level draws about as many as the last and the forest reaches
/// ten kilometres for little more than the trees cost. Pictures and groves, a few pixels across, draw again as the soft
/// fringe of their cut once everything opaque stands behind them.</summary>
public sealed class Vegetation : IDisposable {

    // Metres: grass is drawn within GrassReach, every tuft within GrassDense and a share falling with the square of
    // distance past it; trees in their near detail within TreeNear, their far detail within TreeFar and as pictures past
    // it, each at its own distance jittered by up to Jitter either way, and the near ones cast shadows. Must match
    // Grass.shader and Tree.shader.
    private const float GrassReach = 50.0f;
    private const float GrassDense = 12.0f;
    private const float TreeNear = 150.0f;
    private const float TreeFar = 500.0f;
    private const float Jitter = 0.15f;
    private const float Everywhere = 1e9f;

    // Metres over which each level hands over to the next coarser, tree by tree: the trees to the first groves, each level
    // of groves to the next, and the last fading out at the reach. One per level from PatchStrewJob.TreeDepth out.
    private static readonly Vector2[] Handovers = { new Vector2(1_200.0f, 1_400.0f), new Vector2(2_500.0f, 2_800.0f), new Vector2(5_000.0f, 5_600.0f),
        new Vector2(8_500.0f, 10_000.0f) };

    // Blades per tuft; cards of each tree detail, a trunk and the crown's foliage, the far detail taking every third of the
    // near one's, and a picture's one. Blades must match Grass.shader, and the foliage cards must divide FOLIAGE in
    // Tree.shader.
    private const int Blades = 16;
    private const int NearCards = 1 + 72;
    private const int FarCards = 1 + 24;
    private const int StandCards = 1;

    private static readonly int PlantsId = Shader.PropertyToID("_Plants");
    private static readonly int PatchPositionId = Shader.PropertyToID("_PatchPosition");
    private static readonly int PatchRotationId = Shader.PropertyToID("_PatchRotation");
    private static readonly int CardsId = Shader.PropertyToID("_Cards");
    private static readonly int BandId = Shader.PropertyToID("_Band");
    private static readonly int FadeId = Shader.PropertyToID("_Fade");
    private static readonly int GroveId = Shader.PropertyToID("_Grove");
    private static readonly int FringeId = Shader.PropertyToID("_Fringe");
    private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
    private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
    private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");

    /// <summary>One patch's plants on the GPU, sorted by rank.</summary>
    public sealed class Plot : IDisposable {

        public readonly GraphicsBuffer Plants = new GraphicsBuffer(GraphicsBuffer.Target.Structured, PatchStrewJob.PlantSlots, 48);
        public readonly MaterialPropertyBlock NearBlock = new MaterialPropertyBlock();
        public readonly MaterialPropertyBlock FarBlock = new MaterialPropertyBlock();
        public readonly MaterialPropertyBlock ShadowBlock = new MaterialPropertyBlock();
        public readonly MaterialPropertyBlock StandBlock = new MaterialPropertyBlock();

        public int Count;

        // The quadtree level of the patch: grass on PatchStrewJob.TuftDepth, trees on TreeDepth, groves on the coarser ones.
        public int Depth;

        public bool Trees => Depth != PatchStrewJob.TuftDepth;

        public void Dispose() => Plants.Release();

    }

    private readonly Material _grass;
    private readonly Material _tree;
    private readonly Material _fringe;
    private readonly GraphicsBuffer _bladeIndices;
    private readonly GraphicsBuffer _nearIndices;
    private readonly GraphicsBuffer _farIndices;
    private readonly GraphicsBuffer _standIndices;
    private readonly float _radiusMetres;

    /// <summary><paramref name="radius"/> is the planet's, in scene units.</summary>
    public Vegetation(Material grass, Material tree, float radius) {

        _grass = grass;
        _tree = tree;
        _radiusMetres = radius * 1_000.0f;
        _bladeIndices = Indices(BladeTriangles());
        _nearIndices = Indices(CardTriangles(NearCards));
        _farIndices = Indices(CardTriangles(FarCards));
        _standIndices = Indices(CardTriangles(StandCards));

        // Last of the opaques, so the fringe blends over the ground and the trees behind it; before the sky, which only
        // fills where nothing wrote depth, so against the sky the cut stays as it is.
        _fringe = new Material(tree) { name = "Tree Fringe", renderQueue = (int)RenderQueue.GeometryLast };
        _fringe.SetFloat(FringeId, 1.0f);
        _fringe.SetFloat(SrcBlendId, (float)BlendMode.SrcAlpha);
        _fringe.SetFloat(DstBlendId, (float)BlendMode.OneMinusSrcAlpha);
        _fringe.SetFloat(ZWriteId, 0.0f);
        _fringe.SetShaderPassEnabled("ShadowCaster", false);
        _fringe.SetShaderPassEnabled("DepthOnly", false);

    }

    /// <summary>Takes the plants of a freshly built patch at <paramref name="depth"/>, three float4s each, as PatchJob
    /// gathers them.</summary>
    public void Load(Plot plot, NativeArray<float4> plants, int count, int depth) {

        plot.Plants.SetData(plants.Reinterpret<float4x3>(16), 0, 0, count);
        plot.Count = count;
        plot.Depth = depth;

    }

    /// <summary>Draws a patch's plants, the patch's origin standing at <paramref name="position"/> turned by
    /// <paramref name="rotation"/>, its ground within <paramref name="radius"/> of <paramref name="middle"/>, as seen from
    /// <paramref name="camera"/>; <paramref name="refined"/> when the finer level's patches, and their plants, are drawn
    /// in its place.</summary>
    public void Draw(Plot plot, Vector3 position, Quaternion rotation, Vector3 middle, float radius, Vector3 camera, bool refined) {

        float centre = Vector3.Distance(middle, camera) * 1_000.0f;
        float nearest = Mathf.Max(centre - radius * 1_000.0f, 1.0f);
        float farthest = centre + radius * 1_000.0f;
        Bounds bounds = new Bounds(middle, Vector3.one * (2.0f * radius + 0.1f));

        if (!plot.Trees) {

            if (nearest < GrassReach) {

                int tufts = Share(plot.Count, GrassDense * GrassDense / (nearest * nearest));

                Issue(plot.NearBlock, _grass, _bladeIndices, plot, position, rotation, bounds, tufts, Vector2.zero, Vector4.zero, 0.0f, ShadowCastingMode.Off, 0);

            }

            return;

        }

        int level = PatchStrewJob.TreeDepth - plot.Depth;
        // Groves hand over to the finer level only where it has been built; until then they stand in for it up close.
        Vector2 fadeIn = level == 0 || !refined ? new Vector2(-2.0f, -1.0f) : Handovers[level - 1];
        Vector4 fade = new Vector4(fadeIn.x, fadeIn.y, Handovers[level].x, Handovers[level].y);

        if (farthest < fade.x || nearest >= fade.w) {

            return;

        }

        if (level > 0) {

            Pictures(plot, position, rotation, bounds, new Vector2(0.0f, Everywhere), fade, GroveCell(plot.Depth));

            return;

        }

        // A tree's jittered distance can move a detail's edge by Jitter either way.
        if (nearest < TreeNear / (1.0f - Jitter)) {

            Issue(plot.NearBlock, _tree, _nearIndices, plot, position, rotation, bounds, plot.Count, new Vector2(0.0f, TreeNear), fade, 0.0f, ShadowCastingMode.Off, NearCards);

            // Near trees cast their shadows from their far detail, which is all a shadow texel can tell apart.
            Issue(plot.ShadowBlock, _tree, _farIndices, plot, position, rotation, bounds, plot.Count, new Vector2(0.0f, TreeNear), fade, 0.0f, ShadowCastingMode.ShadowsOnly,
                FarCards);

        }

        if (nearest < TreeFar / (1.0f - Jitter) && farthest > TreeNear / (1.0f + Jitter)) {

            Issue(plot.FarBlock, _tree, _farIndices, plot, position, rotation, bounds, plot.Count, new Vector2(TreeNear, TreeFar), fade, 0.0f, ShadowCastingMode.Off, FarCards);

        }

        if (farthest > TreeFar / (1.0f + Jitter)) {

            Pictures(plot, position, rotation, bounds, new Vector2(TreeFar, Everywhere), fade, 0.0f);

        }

    }

    // A plot's trees or groves as pictures, then their fringe.
    private void Pictures(Plot plot, Vector3 position, Quaternion rotation, Bounds bounds, Vector2 band, Vector4 fade, float grove) {

        Issue(plot.StandBlock, _tree, _standIndices, plot, position, rotation, bounds, plot.Count, band, fade, grove, ShadowCastingMode.Off, StandCards);
        Issue(plot.StandBlock, _fringe, _standIndices, plot, position, rotation, bounds, plot.Count, band, fade, grove, ShadowCastingMode.Off, StandCards);

    }

    // Metres across the cell each grove at depth stands for.
    private float GroveCell(int depth) => _radiusMetres * 0.5f * Mathf.PI / (PatchJob.Quads * (float)(1L << depth)) / Mathf.Sqrt(PatchStrewJob.PlantsPerQuad(depth));

    // The first plants of a sorted plot whose rank is under keep, with a margin for ranks not quite even.
    private static int Share(int count, float keep) => Mathf.Min(count, Mathf.CeilToInt(count * Mathf.Min(keep, 1.0f) * 1.1f) + 8);

    private static void Issue(MaterialPropertyBlock block, Material material, GraphicsBuffer indices, Plot plot, Vector3 position, Quaternion rotation, Bounds bounds,
        int instances, Vector2 band, Vector4 fade, float grove, ShadowCastingMode shadows, int cards) {

        block.SetBuffer(PlantsId, plot.Plants);
        block.SetVector(PatchPositionId, position);
        block.SetVector(PatchRotationId, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));
        block.SetInteger(CardsId, cards);
        block.SetVector(BandId, band);
        block.SetVector(FadeId, fade);
        block.SetFloat(GroveId, grove);

        RenderParams parameters = new RenderParams(material) {

            matProps = block,
            worldBounds = bounds,
            shadowCastingMode = shadows,
            receiveShadows = true,

        };

        Graphics.RenderPrimitivesIndexed(parameters, MeshTopology.Triangles, indices, indices.count, 0, instances);

    }

    public void Dispose() {

        _bladeIndices.Release();
        _nearIndices.Release();
        _farIndices.Release();
        _standIndices.Release();
        UnityEngine.Object.Destroy(_fringe);

    }

    private static GraphicsBuffer Indices(int[] triangles) {

        GraphicsBuffer buffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, triangles.Length, sizeof(int));

        buffer.SetData(triangles);

        return buffer;

    }

    // Each blade is five vertices, base and middle pairs and a tip, in three triangles.
    private static int[] BladeTriangles() {

        int[] triangles = new int[Blades * 9];

        for (int b = 0; b < Blades; b++) {

            int v = 5 * b;

            new[] { v, v + 1, v + 2, v + 1, v + 3, v + 2, v + 2, v + 3, v + 4 }.CopyTo(triangles, 9 * b);

        }

        return triangles;

    }

    // Each card is four vertices, its corners in reading order, in two triangles.
    private static int[] CardTriangles(int cards) {

        int[] triangles = new int[cards * 6];

        for (int c = 0; c < cards; c++) {

            int v = 4 * c;

            new[] { v, v + 1, v + 2, v + 2, v + 1, v + 3 }.CopyTo(triangles, 6 * c);

        }

        return triangles;

    }

}
