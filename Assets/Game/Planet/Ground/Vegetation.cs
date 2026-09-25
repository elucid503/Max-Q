using System;

using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Planet.Ground;

/// <summary>Grass and trees. Patches offer places where plants may stand; once a patch has its satellite tile, a compute
/// pass keeps the places whose ground the materials call meadow or forest, with each plant's colour and size, so the
/// plants grow exactly where the ground shows them, and sorts them by a random rank. Each patch then draws the first of
/// its plants that its distance keeps: grass tufts of procedural blades near the camera, thinning with distance, and
/// trees of revolution, conifers and broadleaves, thinning past the near ones into the canopy the ground draws.</summary>
public sealed class Vegetation : IDisposable {

    // Metres: grass is drawn within GrassReach, every tuft within GrassDense and a share falling with the square of
    // distance past it; trees within TreeReach, all of them inside TreeNear in their near detail, then a share falling
    // with the square of distance, and only those inside TreeShadows cast shadows. Must match Grass.shader and
    // Tree.shader.
    private const float GrassReach = 50.0f;
    private const float GrassDense = 12.0f;
    private const float TreeReach = 800.0f;
    private const float TreeNear = 150.0f;
    private const float TreeThinning = 2.0f;
    private const float TreeShadows = 80.0f;

    // Blades per tuft; rings and segments of each tree detail, three of trunk and the rest crown, which for a conifer is
    // tiers of about the square root of its rings each. Blades must match Grass.shader.
    private const int Blades = 16;
    private const int NearRings = 19;
    private const int NearSegments = 10;
    private const int FarRings = 9;
    private const int FarSegments = 6;

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
    private static readonly int RingsId = Shader.PropertyToID("_Rings");
    private static readonly int SegmentsId = Shader.PropertyToID("_Segments");
    private static readonly int BandId = Shader.PropertyToID("_Band");
    private static readonly int ThinId = Shader.PropertyToID("_Thin");
    private static readonly int PlanetRadiusId = Shader.PropertyToID("_PlanetRadius");

    /// <summary>One patch's plants on the GPU: the places it offers and the plants kept, sorted by rank.</summary>
    public sealed class Plot : IDisposable {

        public readonly GraphicsBuffer Candidates = new GraphicsBuffer(GraphicsBuffer.Target.Structured, PatchJob.PlantLength, 16);
        public readonly GraphicsBuffer Plants = new GraphicsBuffer(GraphicsBuffer.Target.Structured, PatchJob.MaxTufts, 48);
        public readonly MaterialPropertyBlock NearBlock = new MaterialPropertyBlock();
        public readonly MaterialPropertyBlock FarBlock = new MaterialPropertyBlock();
        public readonly MaterialPropertyBlock ShadowBlock = new MaterialPropertyBlock();

        public int Count;
        public bool Trees;

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

        _bladeIndices = Indices(BladeTriangles());
        _nearIndices = Indices(LatheTriangles(NearRings, NearSegments));
        _farIndices = Indices(LatheTriangles(FarRings, FarSegments));

    }

    /// <summary>Takes a freshly built patch's places; its plants wait for <see cref="Select"/>.</summary>
    public void Load(Plot plot, NativeArray<float4> places, int count, bool trees) {

        plot.Candidates.SetData(places, 0, 0, 2 * count);
        plot.Count = count;
        plot.Trees = trees;
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

        float nearest = Mathf.Max((Vector3.Distance(middle, camera) - radius) * 1_000.0f, 1.0f);
        Bounds bounds = new Bounds(middle, Vector3.one * (2.0f * radius + 0.06f));

        if (!plot.Trees) {

            if (nearest < GrassReach) {

                int tufts = Share(plot.Kept, GrassDense * GrassDense / (nearest * nearest));

                Issue(plot.NearBlock, _grass, _bladeIndices, plot, position, rotation, bounds, tufts, Vector2.zero, false, ShadowCastingMode.Off, 0, 0);

            }

            return;

        }

        if (nearest >= TreeReach) {

            return;

        }

        if (nearest < TreeNear) {

            Issue(plot.NearBlock, _tree, _nearIndices, plot, position, rotation, bounds, plot.Kept, new Vector2(0.0f, TreeNear), false, ShadowCastingMode.Off, NearRings, NearSegments);

        }

        if (nearest < TreeShadows) {

            // Near trees cast their shadows from their far detail, which is all a shadow texel can tell apart.
            Issue(plot.ShadowBlock, _tree, _farIndices, plot, position, rotation, bounds, plot.Kept, new Vector2(0.0f, TreeShadows), false, ShadowCastingMode.ShadowsOnly, FarRings, FarSegments);

        }

        // Past the near trees the canopy's own shading carries the forest's shade.
        float far = Mathf.Max(nearest, TreeNear);
        int trees = Share(plot.Kept, TreeThinning * TreeNear * TreeNear / (far * far));

        Issue(plot.FarBlock, _tree, _farIndices, plot, position, rotation, bounds, trees, new Vector2(TreeNear, TreeReach), true, ShadowCastingMode.Off, FarRings, FarSegments);

    }

    // The first plants of a sorted plot whose rank is under keep, with a margin for ranks not quite even.
    private static int Share(int kept, float keep) => Mathf.Min(kept, Mathf.CeilToInt(kept * Mathf.Min(keep, 1.0f) * 1.1f) + 8);

    private static void Issue(MaterialPropertyBlock block, Material material, GraphicsBuffer indices, Plot plot, Vector3 position, Quaternion rotation, Bounds bounds,
        int instances, Vector2 band, bool thin, ShadowCastingMode shadows, int rings, int segments) {

        block.SetBuffer(PlantsId, plot.Plants);
        block.SetVector(PatchPositionId, position);
        block.SetVector(PatchRotationId, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));
        block.SetInteger(RingsId, rings);
        block.SetInteger(SegmentsId, segments);
        block.SetVector(BandId, band);
        block.SetInteger(ThinId, thin ? 1 : 0);

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

    // A surface of revolution: rings from the foot to the crown's top, each of segments + 1 vertices, the last repeating
    // the first so the seam can close.
    private static int[] LatheTriangles(int rings, int segments) {

        int[] triangles = new int[(rings - 1) * segments * 6];
        int n = 0;

        for (int r = 0; r < rings - 1; r++) {

            for (int s = 0; s < segments; s++) {

                int a = r * (segments + 1) + s;
                int b = a + 1;
                int c = a + segments + 1;
                int d = c + 1;

                triangles[n++] = a;
                triangles[n++] = c;
                triangles[n++] = b;
                triangles[n++] = b;
                triangles[n++] = c;
                triangles[n++] = d;

            }

        }

        return triangles;

    }

}
