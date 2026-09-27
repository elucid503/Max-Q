using System;

using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Planet.Ground.Plants;

/// <summary>Grass and trees. Patches offer places where plants may stand; once a patch has its satellite tile, a compute
/// pass keeps the places whose ground the materials call meadow or forest, with each plant's colour and size, so the
/// plants grow exactly where the ground shows them, and sorts them by a random rank. Each patch then draws the first of
/// its plants that its distance keeps: grass tufts of procedural blades near the camera, thinning with distance, and
/// trees, conifers and broadleaves, of foliage cards and then of single painted cards. Past the trees, the coarser levels'
/// patches draw groves, one card for the trees of each cell, their cells doubling in size as the distance doubles, so each
/// level draws about as many as the last and the forest reaches ten kilometres for little more than the trees cost.</summary>
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
    // of groves to the next, and the last fading out at the reach. One per level from PatchJob.TreeDepth out.
    private static readonly Vector2[] Handovers = { new Vector2(1_200.0f, 1_400.0f), new Vector2(2_500.0f, 2_800.0f), new Vector2(5_000.0f, 5_600.0f),
        new Vector2(8_500.0f, 10_000.0f) };

    // Blades per tuft; cards of each tree detail, a trunk and the crown's foliage, the far detail taking every third of the
    // near one's, and a picture's one. Blades must match Grass.shader, and the foliage cards must divide FOLIAGE in
    // Tree.shader.
    private const int Blades = 16;
    private const int NearCards = 1 + 72;
    private const int FarCards = 1 + 24;
    private const int StandCards = 1;

    // Places a patch can offer, as the compute pass sorts them; must match SLOTS in Vegetation.compute.
    private const int Slots = 4096;

    private static readonly int CandidatesId = Shader.PropertyToID("_Candidates");
    private static readonly int CandidateCountId = Shader.PropertyToID("_CandidateCount");
    private static readonly int ScratchId = Shader.PropertyToID("_Scratch");
    private static readonly int PlantsId = Shader.PropertyToID("_Plants");
    private static readonly int KeptId = Shader.PropertyToID("_Kept");
    private static readonly int ColourId = Shader.PropertyToID("_Colour");
    private static readonly int ColourRectId = Shader.PropertyToID("_ColourRect");
    private static readonly int DetailId = Shader.PropertyToID("_Detail");
    private static readonly int ParentDetailId = Shader.PropertyToID("_ParentDetail");
    private static readonly int ParentRectId = Shader.PropertyToID("_ParentRect");
    private static readonly int TileOriginMacroId = Shader.PropertyToID("_TileOriginMacro");
    private static readonly int TileOriginBroadId = Shader.PropertyToID("_TileOriginBroad");
    private static readonly int GroundAlbedoId = Shader.PropertyToID("_GroundAlbedo");
    private static readonly int PatchCentreId = Shader.PropertyToID("_PatchCentre");
    private static readonly int PatchPositionId = Shader.PropertyToID("_PatchPosition");
    private static readonly int PatchRotationId = Shader.PropertyToID("_PatchRotation");
    private static readonly int CardsId = Shader.PropertyToID("_Cards");
    private static readonly int BandId = Shader.PropertyToID("_Band");
    private static readonly int FadeId = Shader.PropertyToID("_Fade");
    private static readonly int GroveId = Shader.PropertyToID("_Grove");
    private static readonly int PlanetRadiusId = Shader.PropertyToID("_PlanetRadius");

    /// <summary>One patch's plants on the GPU: the places it offers and the plants kept, sorted by rank.</summary>
    public sealed class Plot : IDisposable {

        public readonly GraphicsBuffer Candidates = new GraphicsBuffer(GraphicsBuffer.Target.Structured, PatchJob.PlantLength, 16);
        public readonly GraphicsBuffer Plants = new GraphicsBuffer(GraphicsBuffer.Target.Structured, PatchJob.MaxTufts, 48);
        public readonly MaterialPropertyBlock NearBlock = new MaterialPropertyBlock();
        public readonly MaterialPropertyBlock FarBlock = new MaterialPropertyBlock();
        public readonly MaterialPropertyBlock ShadowBlock = new MaterialPropertyBlock();
        public readonly MaterialPropertyBlock StandBlock = new MaterialPropertyBlock();

        public int Count;
        public bool Trees;

        // The quadtree level of the patch: trees on PatchJob.TreeDepth, groves on the coarser ones.
        public int Depth;

        /// <summary>Plants kept, once the GPU has told; until then the plot draws nothing.</summary>
        public int Kept = -1;

        // Counts selections; a count read back for an older one is stale.
        public int Selection;

        public bool Ready => Kept > 0;

        public void Dispose() {

            Candidates.Release();
            Plants.Release();

        }

    }

    private readonly Material _grass;
    private readonly Material _tree;
    private readonly ComputeShader _select;
    private readonly int _selectTufts;
    private readonly int _selectTrees;
    private readonly GraphicsBuffer _scratch = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Slots, 48);
    private readonly GraphicsBuffer _kept = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
    private readonly GraphicsBuffer _bladeIndices;
    private readonly GraphicsBuffer _nearIndices;
    private readonly GraphicsBuffer _farIndices;
    private readonly GraphicsBuffer _standIndices;
    private readonly float _radiusMetres;

    /// <summary><paramref name="radius"/> is the planet's, in scene units.</summary>
    public Vegetation(Material grass, Material tree, ComputeShader select, Texture groundAlbedo, float radius) {

        _grass = grass;
        _tree = tree;
        _select = select;
        _selectTufts = select.FindKernel("SelectTufts");
        _selectTrees = select.FindKernel("SelectTrees");

        foreach (int kernel in new[] { _selectTufts, _selectTrees }) {

            _select.SetTexture(kernel, GroundAlbedoId, groundAlbedo);
            _select.SetBuffer(kernel, ScratchId, _scratch);
            _select.SetBuffer(kernel, KeptId, _kept);

        }

        _select.SetFloat(PlanetRadiusId, radius);
        _radiusMetres = radius * 1_000.0f;

        _bladeIndices = Indices(BladeTriangles());
        _nearIndices = Indices(CardTriangles(NearCards));
        _farIndices = Indices(CardTriangles(FarCards));
        _standIndices = Indices(CardTriangles(StandCards));

    }

    /// <summary>Takes the places of a freshly built patch at <paramref name="depth"/>; its plants wait for <see cref="Select"/>.</summary>
    public void Load(Plot plot, NativeArray<float4> places, int count, bool trees, int depth) {

        plot.Candidates.SetData(places, 0, 0, 2 * count);
        plot.Count = count;
        plot.Trees = trees;
        plot.Depth = depth;
        plot.Kept = -1;
        plot.Selection++;

    }

    /// <summary>Keeps the places the ground under them suits, against the patch's satellite tile and detail.</summary>
    public void Select(Plot plot, Texture colour, Vector4 rect, Texture detail, Vector4 macroOrigin, Vector4 broadOrigin, Vector3 centre) {

        int kernel = plot.Trees ? _selectTrees : _selectTufts;

        _select.SetBuffer(kernel, CandidatesId, plot.Candidates);
        _select.SetBuffer(kernel, PlantsId, plot.Plants);
        _select.SetInt(CandidateCountId, plot.Count);
        _select.SetTexture(kernel, ColourId, colour);
        _select.SetTexture(kernel, DetailId, detail);
        _select.SetTexture(kernel, ParentDetailId, detail);
        _select.SetVector(ColourRectId, rect);
        _select.SetVector(ParentRectId, new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
        _select.SetVector(TileOriginMacroId, macroOrigin);
        _select.SetVector(TileOriginBroadId, broadOrigin);
        _select.SetVector(PatchCentreId, centre);
        _select.Dispatch(kernel, 1, 1, 1);

        // Until the new count arrives the old one stands: a better tile only recolours the same places.
        int selection = ++plot.Selection;

        AsyncGPUReadback.Request(_kept, request => {

            if (!request.hasError && plot.Selection == selection) {

                plot.Kept = (int)request.GetData<uint>()[0];

            }

        });

    }

    /// <summary>Draws a patch's plants, the patch's origin standing at <paramref name="position"/> turned by
    /// <paramref name="rotation"/>, its ground within <paramref name="radius"/> of <paramref name="middle"/>, as seen from
    /// <paramref name="camera"/>.</summary>
    public void Draw(Plot plot, Vector3 position, Quaternion rotation, Vector3 middle, float radius, Vector3 camera) {

        float centre = Vector3.Distance(middle, camera) * 1_000.0f;
        float nearest = Mathf.Max(centre - radius * 1_000.0f, 1.0f);
        float farthest = centre + radius * 1_000.0f;
        Bounds bounds = new Bounds(middle, Vector3.one * (2.0f * radius + 0.1f));

        if (!plot.Trees) {

            if (nearest < GrassReach) {

                int tufts = Share(plot.Kept, GrassDense * GrassDense / (nearest * nearest));

                Issue(plot.NearBlock, _grass, _bladeIndices, plot, position, rotation, bounds, tufts, Vector2.zero, Vector4.zero, 0.0f, ShadowCastingMode.Off, 0);

            }

            return;

        }

        int level = PatchJob.TreeDepth - plot.Depth;
        Vector2 fadeIn = level == 0 ? new Vector2(-2.0f, -1.0f) : Handovers[level - 1];
        Vector4 fade = new Vector4(fadeIn.x, fadeIn.y, Handovers[level].x, Handovers[level].y);

        if (farthest < fade.x || nearest >= fade.w) {

            return;

        }

        if (level > 0) {

            Issue(plot.StandBlock, _tree, _standIndices, plot, position, rotation, bounds, plot.Kept, new Vector2(0.0f, Everywhere), fade, GroveCell(plot.Depth),
                ShadowCastingMode.Off, StandCards);

            return;

        }

        // A tree's jittered distance can move a detail's edge by Jitter either way.
        if (nearest < TreeNear / (1.0f - Jitter)) {

            Issue(plot.NearBlock, _tree, _nearIndices, plot, position, rotation, bounds, plot.Kept, new Vector2(0.0f, TreeNear), fade, 0.0f, ShadowCastingMode.Off, NearCards);

            // Near trees cast their shadows from their far detail, which is all a shadow texel can tell apart.
            Issue(plot.ShadowBlock, _tree, _farIndices, plot, position, rotation, bounds, plot.Kept, new Vector2(0.0f, TreeNear), fade, 0.0f, ShadowCastingMode.ShadowsOnly,
                FarCards);

        }

        if (nearest < TreeFar / (1.0f - Jitter) && farthest > TreeNear / (1.0f + Jitter)) {

            Issue(plot.FarBlock, _tree, _farIndices, plot, position, rotation, bounds, plot.Kept, new Vector2(TreeNear, TreeFar), fade, 0.0f, ShadowCastingMode.Off, FarCards);

        }

        if (farthest > TreeFar / (1.0f + Jitter)) {

            Issue(plot.StandBlock, _tree, _standIndices, plot, position, rotation, bounds, plot.Kept, new Vector2(TreeFar, Everywhere), fade, 0.0f, ShadowCastingMode.Off,
                StandCards);

        }

    }

    // Metres across the cell each grove at depth stands for.
    private float GroveCell(int depth) => _radiusMetres * 0.5f * Mathf.PI / (PatchJob.Quads * (float)(1L << depth)) / Mathf.Sqrt(PatchJob.GrovesPerQuad);

    // The first plants of a sorted plot whose rank is under keep, with a margin for ranks not quite even.
    private static int Share(int kept, float keep) => Mathf.Min(kept, Mathf.CeilToInt(kept * Mathf.Min(keep, 1.0f) * 1.1f) + 8);

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

        _scratch.Release();
        _kept.Release();
        _bladeIndices.Release();
        _nearIndices.Release();
        _farIndices.Release();
        _standIndices.Release();

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
