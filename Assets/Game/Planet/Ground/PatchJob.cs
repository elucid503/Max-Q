using System;
using System.Runtime.InteropServices;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Ocean;
using MaxQ.Sim.Surface;

using Terrain = MaxQ.Sim.Surface.Terrain;

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Planet.Ground;

/// <summary>Builds one quadtree node's ground and water mesh plus its detail texture from the sim's terrain function, and
/// the horizon around each vertex, which gives the ground its ambient occlusion. <see cref="ScheduleStages"/> runs it as
/// the last of three stages: the terrain samples and the rocks and trees are spread across the workers first, so no one
/// job holds a worker, or a main thread waiting on one, for long.</summary>
[BurstCompile]
internal struct PatchJob : IJob {

    public const int Quads = 32;
    public const int Vertices = Quads + 1;
    public const int TexelsPerQuad = 4;
    public const int Texels = TexelsPerQuad * Quads + 1;

    public const int GroundVertices = Vertices * Vertices + 4 * Vertices;
    public const int WaterVertices = Vertices * Vertices + 4 * Vertices;
    public const int MaxIndices = 3 * (Quads * Quads * 6 + 4 * Quads * 6);

    // Water depth range the detail texture encodes, metres either side of the surface. Depth is stored as a signed
    // square root: fine near the shore, and it never saturates, so coarse patches keep smooth coastlines.
    public const double WaterDepthRange = 16_384.0;

    // The water detail: two texels to a quad, holding the river's current (m/s, either way up to FlowRange) and the
    // land's shelter from the wind's sea and from swell; must match WaterSurface.hlsl.
    public const int WaterTexels = 2 * Quads + 1;
    public const double FlowRange = 8.0;

    // Ground lies under water deeper than this (m) only where no light it returns could reach the eye: the clearest
    // ocean swallows the round trip through a hundred real metres.
    private const double SeabedDepth = 20.0;

    // Waves reach this far (m) above and below the sheet, which culling and bounds must allow for.
    public const double WaveReach = 30.0;

    // A vertex with no water level carries this one.
    private const float NoLevel = -1e6f;

    public const int InfoMinHeight = 0;
    public const int InfoMaxHeight = 1;
    public const int InfoRadius = 2;
    public const int InfoGroundIndices = 3;
    public const int InfoWaterIndices = 4;
    public const int InfoRocks = 5;
    public const int InfoTufts = 6;
    public const int InfoTrees = 7;
    public const int InfoCoarseWaterIndices = 8;

    // Added to a skirt vertex's first texture coordinate; must match SKIRT_FLAG in Ground.hlsl.
    private const float SkirtFlag = 2.0f;
    public const int InfoLength = 9;

    // Places where grass or trees may grow; the ground's materials decide which do, on the GPU (see Vegetation). Each is
    // two float4s in the patch's scene axes: position in kilometres from the patch centre and a random number; the place
    // on the patch (0 to 1 each way). Tufts cover the finest patches, three to a quad, on the mesh's own triangles so
    // they stand on exactly the ground drawn. Trees stand on patches of the boulder level, two chances to a quad.
    public const int TuftDepth = GroundView.MaxDepth;
    public const int TreeDepth = RockDepth;
    private const int TuftsPerQuad = 3;
    public const int TreesPerQuad = 2;
    public const int MaxTufts = Quads * Quads * TuftsPerQuad;
    public const int MaxTrees = Quads * Quads * TreesPerQuad;
    public const int PlantLength = 2 * Quads * Quads * TuftsPerQuad;

    // Past the trees, forest is groves: two places to a quad on the coarser levels' patches, each standing for the trees
    // of its cell, on the mesh's own triangles so a grove stands on exactly the ground drawn. A grove's second float4
    // carries the ground's normal there, octahedral in the patch's scene axes, after its place on the patch.
    public const int FarthestGroveDepth = 10;
    public const int GrovesPerQuad = 2;

    // Plants and rocks stand only on ground at least this far (m) over any water sheet: the shores hold dry land just
    // clear of the water, and grass, trees and stones reach down to it.
    public const double DryClearance = 0.05;

    // Boulders are strewn by the patches of one level, a few hundred metres across, and outcrops of bedrock by a coarser
    // one, a few kilometres across: chances per quad, each taken more often the steeper the ground (see PatchStrewJob).
    // Each rock is three float4s in the patch's scene axes: position in kilometres from the patch centre and size in
    // metres; orientation as a quaternion; and a random number that picks its shape, its place on the patch (0 to 1 each
    // way, for its colour), and one for an outcrop.
    public const int RockDepth = 13;
    public const int OutcropDepth = 11;
    public const int MaxRocks = 512;
    public const int RockLength = 3 * MaxRocks;

    // Each vertex's horizon in eight directions, as the sine of its elevation in bytes. A patch searches out to
    // HorizonReach of its own vertices and takes its parent's horizon where that is higher, so every level adds the
    // occlusion at its own scale and the finest patch sees the valley walls as well as the stones.
    public const int HorizonDirections = 8;
    public const int HorizonLength = Vertices * Vertices * HorizonDirections;
    public const int HorizonReach = 15;

    // A water vertex's Parent is the offset (km, scene axes) to one end of the parent's edge or diagonal it morphs onto,
    // the other end mirrored through its morph target, so its waves can morph as its position does; a skirt's w is how
    // far it hangs below its edge vertex (km). Water is how far the sheet's level stands over the vertex (m, or NoLevel
    // where there is none) and the distance to shore (m).
    [StructLayout(LayoutKind.Sequential)]
    public struct Vertex {

        public float3 Position;
        public float2 Uv;
        public float3 Morph;
        public float4 Parent;
        public float2 Water;

    }

    public static readonly VertexAttributeDescriptor[] Layout = {

        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 2),

    };

    // Read-only views of the memory-mapped survey, shared by every job.
    [NativeDisableUnsafePtrRestriction]
    public Terrain Terrain;

    [NativeDisableUnsafePtrRestriction]
    public SeaState SeaState;

    // Month of the sea-state climatology the shelter is measured against.
    public int Month;

    public int Face;
    public int Depth;
    public int X;
    public int Y;

    public Mesh.MeshData Mesh;
    public NativeArray<ushort> Detail;
    public NativeArray<ushort> WaterDetail;
    public NativeArray<double> Info;
    public NativeArray<byte> Horizons;
    public NativeArray<float4> Rocks;
    public NativeArray<float4> Plants;

    // Empty for a root.
    [ReadOnly]
    public NativeArray<byte> ParentHorizons;

    // The earlier stages' results, from PatchSamples.
    [ReadOnly]
    public NativeArray<Vector3d> Grid;

    [ReadOnly]
    public NativeArray<Vector3d> Directions;

    [ReadOnly]
    public NativeArray<double> Heights;

    [ReadOnly]
    public NativeArray<double> Levels;

    [ReadOnly]
    public NativeArray<double> Shores;

    public NativeArray<Vector3d> Coarse;

    [ReadOnly]
    public NativeArray<Vector3d> Fine;

    [ReadOnly]
    public NativeArray<double> Depths;

    [ReadOnly]
    public NativeArray<float4> PlacedRocks;

    [ReadOnly]
    public NativeArray<float4> PlacedTrees;

    /// <summary>Sizes the mesh buffers on the main thread; the job fills them.</summary>
    public static void Prepare(Mesh.MeshData mesh) {

        mesh.SetVertexBufferParams(GroundVertices + WaterVertices, Layout);
        mesh.SetIndexBufferParams(MaxIndices, IndexFormat.UInt16);

    }

    /// <summary>Metres between a node's vertices at <paramref name="depth"/>.</summary>
    public static double Footprint(double radius, int depth) => radius * 0.5 * Math.PI / (Quads * (double)(1L << depth));

    public static Vector3d CentreDirection(int face, int depth, int x, int y) {

        double span = 2.0 / (1L << depth);

        return CubeFace.Direction(face, (x + 0.5) * span - 1.0, (y + 0.5) * span - 1.0);

    }

    /// <summary>Schedules the build with <paramref name="samples"/> as its scratch: the terrain sampled across the workers,
    /// then the rocks and trees placed, then this job assembling the patch.</summary>
    public JobHandle ScheduleStages(PatchSamples samples) {

        Grid = samples.Grid;
        Directions = samples.Directions;
        Heights = samples.Heights;
        Levels = samples.Levels;
        Shores = samples.Shores;
        Coarse = samples.Coarse;
        Fine = samples.Fine;
        Depths = samples.Depths;
        PlacedRocks = samples.Rocks;
        PlacedTrees = samples.Trees;

        JobHandle sampled = new PatchSampleJob {

            Terrain = Terrain,
            SeaState = SeaState,
            Month = Month,
            Face = Face,
            Depth = Depth,
            X = X,
            Y = Y,
            Grid = samples.Grid,
            Directions = samples.Directions,
            Heights = samples.Heights,
            Levels = samples.Levels,
            Shores = samples.Shores,
            Coarse = samples.Coarse,
            Fine = samples.Fine,
            Depths = samples.Depths,
            WaterDetail = WaterDetail,

        }.Schedule(PatchSampleJob.Rows, 1);

        JobHandle strewn = !PatchStrewJob.Strews(Depth) ? sampled : new PatchStrewJob {

            Terrain = Terrain,
            Face = Face,
            Depth = Depth,
            X = X,
            Y = Y,
            Grid = samples.Grid,
            Rocks = samples.Rocks,
            Trees = samples.Trees,

        }.Schedule(Quads, 1, sampled);

        return this.Schedule(strewn);

    }

    public void Execute() {

        double radius = Terrain.Radius;
        double footprint = Footprint(radius, Depth);
        Vector3d centre = CentreDirection(Face, Depth, X, Y) * radius;

        NativeArray<Vector3d> ground = new NativeArray<Vector3d>(Vertices * Vertices, Allocator.Temp);

        double minHeight = double.MaxValue;
        double maxHeight = double.MinValue;

        for (int j = 0; j < Vertices; j++) {

            for (int i = 0; i < Vertices; i++) {

                int v = j * Vertices + i;
                double height = Heights[v];
                double level = Levels[v];

                ground[v] = Grid[(j + HorizonReach) * PatchSamples.GridSize + i + HorizonReach];

                minHeight = Math.Min(minHeight, double.IsNaN(level) ? height : Math.Min(height, level - WaveReach));
                maxHeight = Math.Max(maxHeight, double.IsNaN(level) ? height : Math.Max(height, level + WaveReach));

            }

        }

        // Where the parent level would put each vertex: its own posts at the parent's footprint (a root takes its
        // own), and the points between them on the parent's edges and diagonals.
        if (Depth == 0) {

            for (int j = 0; j < Vertices; j += 2) {

                for (int i = 0; i < Vertices; i += 2) {

                    Coarse[j * Vertices + i] = ground[j * Vertices + i];

                }

            }

        }

        Between(Coarse);

        // Triangles are judged on levels spread one vertex past each body; the sheet's positions on two, so every
        // vertex of a kept triangle has levelled neighbours to morph between.
        NativeArray<double> reach1 = Spread(Levels);
        NativeArray<double> reach2 = Spread(reach1);

        WriteMesh(ground, Coarse, Directions, Heights, reach1, reach2, centre, footprint);
        WriteDetail(Occlusion());

        double reach = 0.0;
        Vector3d middle = centre / radius * (radius + 0.5 * (minHeight + maxHeight));

        for (int v = 0; v < ground.Length; v++) {

            reach = Math.Max(reach, (ground[v] - middle).Length);

        }

        Info[InfoMinHeight] = minHeight;
        Info[InfoMaxHeight] = maxHeight;
        Info[InfoRadius] = reach + 0.5 * (maxHeight - minHeight);
        Info[InfoTufts] = Depth == TuftDepth ? ScatterTufts(ground, Heights, Levels, centre) : 0;
        Info[InfoTrees] = Depth == TreeDepth ? GatherTrees() : Depth >= FarthestGroveDepth && Depth < TreeDepth ? ScatterGroves(ground, Heights, Levels, centre) : 0;
        Info[InfoRocks] = GatherRocks();

    }

    // The rocks the strewing placed, in the order of their chances, up to as many as a patch holds.
    private int GatherRocks() {

        int slots = PatchStrewJob.RockSlots(Depth);
        int count = 0;

        for (int slot = 0; slot < slots && count < MaxRocks; slot++) {

            if (PlacedRocks[3 * slot].w <= 0.0f) {

                continue;

            }

            Rocks[3 * count] = PlacedRocks[3 * slot];
            Rocks[3 * count + 1] = PlacedRocks[3 * slot + 1];
            Rocks[3 * count + 2] = PlacedRocks[3 * slot + 2];
            count++;

        }

        return count;

    }

    private int GatherTrees() {

        int count = 0;

        for (int slot = 0; slot < MaxTrees; slot++) {

            if (PlacedTrees[2 * slot].w < 0.0f) {

                continue;

            }

            Plants[2 * count] = PlacedTrees[2 * slot];
            Plants[2 * count + 1] = PlacedTrees[2 * slot + 1];
            count++;

        }

        return count;

    }

    private int ScatterTufts(NativeArray<Vector3d> ground, NativeArray<double> heights, NativeArray<double> levels, Vector3d centre) {

        int count = 0;

        for (int j = 0; j < Quads; j++) {

            for (int i = 0; i < Quads; i++) {

                int a = j * Vertices + i;
                int b = a + 1;
                int c = a + Vertices;
                int d = c + 1;

                if (Below(heights, levels, a, -DryClearance) || Below(heights, levels, b, -DryClearance) || Below(heights, levels, c, -DryClearance) || Below(heights, levels, d, -DryClearance)) {

                    continue;

                }

                for (int t = 0; t < TuftsPerQuad; t++) {

                    uint hash = math.hash(new int4(Face, X * Quads + i, Y * Quads + j, t + 32));
                    float s = (hash & 0xFFFFu) / 65_536.0f;
                    float u = (hash >> 16) / 65_536.0f;

                    // The mesh splits each quad into (a, b, c) and (b, d, c).
                    Vector3d p = s + u < 1.0f
                        ? ground[a] + (ground[b] - ground[a]) * s + (ground[c] - ground[a]) * u
                        : ground[d] + (ground[c] - ground[d]) * (1.0f - s) + (ground[b] - ground[d]) * (1.0f - u);

                    Plants[2 * count] = new float4(Scene(p - centre), math.hash(new uint2(hash, 7u)) / 4_294_967_296.0f);
                    Plants[2 * count + 1] = new float4((i + s) / Quads, (j + u) / Quads, 0.0f, 0.0f);
                    count++;

                }

            }

        }

        return count;

    }

    private int ScatterGroves(NativeArray<Vector3d> ground, NativeArray<double> heights, NativeArray<double> levels, Vector3d centre) {

        int count = 0;

        for (int j = 0; j < Quads; j++) {

            for (int i = 0; i < Quads; i++) {

                int a = j * Vertices + i;
                int b = a + 1;
                int c = a + Vertices;
                int d = c + 1;

                if (Below(heights, levels, a, -DryClearance) || Below(heights, levels, b, -DryClearance) || Below(heights, levels, c, -DryClearance) || Below(heights, levels, d, -DryClearance)) {

                    continue;

                }

                for (int t = 0; t < GrovesPerQuad; t++) {

                    uint hash = math.hash(new int4(Face, X * Quads + i, Y * Quads + j, t + 64 + 4 * Depth));
                    float s = (hash & 0xFFFFu) / 65_536.0f;
                    float u = (hash >> 16) / 65_536.0f;
                    bool first = s + u < 1.0f;

                    // The mesh splits each quad into (a, b, c) and (b, d, c).
                    Vector3d p = first
                        ? ground[a] + (ground[b] - ground[a]) * s + (ground[c] - ground[a]) * u
                        : ground[d] + (ground[c] - ground[d]) * (1.0f - s) + (ground[b] - ground[d]) * (1.0f - u);
                    Vector3d normal = first
                        ? Vector3d.Cross(ground[b] - ground[a], ground[c] - ground[a]).Normalized
                        : Vector3d.Cross(ground[c] - ground[d], ground[b] - ground[d]).Normalized;

                    normal = Vector3d.Dot(normal, p) < 0.0 ? -normal : normal;

                    float2 octahedral = Octahedral(new float3((float)normal.X, (float)normal.Z, (float)normal.Y));

                    Plants[2 * count] = new float4(Scene(p - centre), math.hash(new uint2(hash, 13u)) / 4_294_967_296.0f);
                    Plants[2 * count + 1] = new float4((i + s) / Quads, (j + u) / Quads, octahedral.x, octahedral.y);
                    count++;

                }

            }

        }

        return count;

    }

    // Odd vertices take the midpoint of the parent's edge or of the diagonal its triangulation used.
    private static void Between(NativeArray<Vector3d> grid) {

        for (int j = 0; j < Vertices; j++) {

            for (int i = 0; i < Vertices; i++) {

                bool oddI = (i & 1) == 1;
                bool oddJ = (j & 1) == 1;

                if (!oddI && !oddJ) {

                    continue;

                }

                Vector3d a = oddI && oddJ ? grid[(j - 1) * Vertices + i + 1] : oddI ? grid[j * Vertices + i - 1] : grid[(j - 1) * Vertices + i];
                Vector3d b = oddI && oddJ ? grid[(j + 1) * Vertices + i - 1] : oddI ? grid[j * Vertices + i + 1] : grid[(j + 1) * Vertices + i];

                grid[j * Vertices + i] = (a + b) * 0.5;

            }

        }

    }

    // Vertices just outside a body's reach take a neighbour's level, so the sheet's last row of triangles reaches into the bank.
    private static NativeArray<double> Spread(NativeArray<double> source) {

        NativeArray<double> levels = new NativeArray<double>(source, Allocator.Temp);

        for (int j = 0; j < Vertices; j++) {

            for (int i = 0; i < Vertices; i++) {

                if (!double.IsNaN(source[j * Vertices + i])) {

                    continue;

                }

                double best = double.NaN;

                for (int n = 0; n < 4; n++) {

                    int ni = i + (n == 0 ? -1 : n == 1 ? 1 : 0);
                    int nj = j + (n == 2 ? -1 : n == 3 ? 1 : 0);

                    if (ni < 0 || nj < 0 || ni >= Vertices || nj >= Vertices) {

                        continue;

                    }

                    double level = source[nj * Vertices + ni];

                    if (!double.IsNaN(level) && (double.IsNaN(best) || level > best)) {

                        best = level;

                    }

                }

                levels[j * Vertices + i] = best;

            }

        }

        return levels;

    }

    private void WriteMesh(NativeArray<Vector3d> ground, NativeArray<Vector3d> coarse, NativeArray<Vector3d> directions,
        NativeArray<double> heights, NativeArray<double> levels, NativeArray<double> sheetLevels, Vector3d centre, double footprint) {

        NativeArray<Vertex> vertices = Mesh.GetVertexData<Vertex>();
        NativeArray<ushort> indices = Mesh.GetIndexData<ushort>();

        float3 low = new float3(float.MaxValue);
        float3 high = new float3(float.MinValue);
        double skirt = 8.0 * footprint;

        for (int j = 0; j < Vertices; j++) {

            for (int i = 0; i < Vertices; i++) {

                int v = j * Vertices + i;
                Vertex vertex = MakeVertex(ground[v], coarse[v], centre, i, j, ref low, ref high);

                // Judged on the same levels as the sheet's triangles, so the ground is a bed only where a sheet covers it.
                vertex.Water = WaterOf(levels, v);
                vertices[v] = vertex;

            }

        }

        // Skirts hang below each edge to hide the cracks of a neighbour that is still streaming in.
        WriteSkirts(vertices, 0, ground, coarse, directions, centre, skirt, ref low, ref high);

        // The water sheet: flat at each body's level, morphing on its own edges and diagonals like the ground.
        NativeArray<Vector3d> sheet = new NativeArray<Vector3d>(Vertices * Vertices, Allocator.Temp);
        NativeArray<Vector3d> sheetCoarse = new NativeArray<Vector3d>(Vertices * Vertices, Allocator.Temp);

        for (int v = 0; v < sheet.Length; v++) {

            double level = double.IsNaN(sheetLevels[v]) ? heights[v] : sheetLevels[v];

            sheet[v] = directions[v] * (Terrain.Radius + level);
            sheetCoarse[v] = sheet[v];

        }

        Between(sheetCoarse);

        for (int j = 0; j < Vertices; j++) {

            for (int i = 0; i < Vertices; i++) {

                int v = j * Vertices + i;
                Vertex vertex = MakeVertex(sheet[v], sheetCoarse[v], centre, i, j, ref low, ref high);

                vertex.Parent = new float4(Scene(sheet[ParentEnd(i, j)] - sheet[v]), 0.0f);
                vertex.Water = WaterOf(sheetLevels, v);
                vertices[GroundVertices + v] = vertex;

            }

        }

        // The sheet hangs skirts too: neighbouring levels' sheets meet only to within rounding, which shows sky between them.
        WriteSkirts(vertices, GroundVertices, sheet, sheetCoarse, directions, centre, skirt, ref low, ref high);

        int count = 0;
        double deep = Math.Max(SeabedDepth, 0.05 * footprint);

        for (int j = 0; j < Quads; j++) {

            for (int i = 0; i < Quads; i++) {

                int a = j * Vertices + i;
                int b = a + 1;
                int c = a + Vertices;
                int d = c + 1;

                if (!Submerged(heights, levels, a, b, c, deep)) {

                    AddTriangle(indices, ref count, vertices, a, b, c, centre);

                }

                if (!Submerged(heights, levels, b, d, c, deep)) {

                    AddTriangle(indices, ref count, vertices, b, d, c, centre);

                }

            }

        }

        for (int edge = 0; edge < 4; edge++) {

            for (int k = 0; k < Quads; k++) {

                int v0 = EdgeVertex(edge, k);
                int v1 = EdgeVertex(edge, k + 1);

                if (Submerged(heights, levels, v0, v1, v1, deep)) {

                    continue;

                }

                int s0 = Vertices * Vertices + edge * Vertices + k;
                int s1 = s0 + 1;
                float3 outward = vertices[v0].Position + vertices[v1].Position;

                AddFacing(indices, ref count, vertices, v0, v1, s0, outward);
                AddFacing(indices, ref count, vertices, v1, s1, s0, outward);

            }

        }

        int groundCount = count;

        for (int j = 0; j < Quads; j++) {

            for (int i = 0; i < Quads; i++) {

                AddWaterQuad(indices, ref count, vertices, heights, levels, i, j, centre);

            }

        }

        AddWaterSkirts(indices, ref count, vertices, heights, levels);

        int fineCount = count;

        // The sheet again at half resolution away from its shores, for a view that sees its quads edge-on: the parent's
        // triangles, onto whose edges and diagonals the odd vertices morph.
        for (int j = 0; j < Quads; j += 2) {

            for (int i = 0; i < Quads; i += 2) {

                int a = j * Vertices + i;

                if (Open(heights, levels, a)) {

                    AddTriangle(indices, ref count, vertices, GroundVertices + a, GroundVertices + a + 2, GroundVertices + a + 2 * Vertices, centre);
                    AddTriangle(indices, ref count, vertices, GroundVertices + a + 2, GroundVertices + a + 2 * Vertices + 2, GroundVertices + a + 2 * Vertices, centre);

                    continue;

                }

                for (int q = 0; q < 4; q++) {

                    AddWaterQuad(indices, ref count, vertices, heights, levels, i + (q & 1), j + (q >> 1), centre);

                }

            }

        }

        AddWaterSkirts(indices, ref count, vertices, heights, levels);

        if (count > groundCount) {

            low -= (float)(WaveReach / 1_000.0);
            high += (float)(WaveReach / 1_000.0);

        }

        Bounds bounds = new Bounds((Vector3)((low + high) * 0.5f), (Vector3)(high - low));

        Mesh.subMeshCount = 2;
        Mesh.SetSubMesh(0, new SubMeshDescriptor(0, groundCount) { bounds = bounds, vertexCount = GroundVertices }, Flags);
        Mesh.SetSubMesh(1, new SubMeshDescriptor(groundCount, fineCount - groundCount) { bounds = bounds, firstVertex = GroundVertices, vertexCount = WaterVertices }, Flags);

        Info[InfoGroundIndices] = groundCount;
        Info[InfoWaterIndices] = fineCount - groundCount;
        Info[InfoCoarseWaterIndices] = count - fineCount;

    }

    // One surface's skirts, after its grid of vertices from first: each edge vertex dropped straight down, keeping its
    // edge vertex's water and, for the sheet, its parent's edge and how far below it hangs.
    private static void WriteSkirts(NativeArray<Vertex> vertices, int first, NativeArray<Vector3d> surface, NativeArray<Vector3d> coarse,
        NativeArray<Vector3d> directions, Vector3d centre, double depth, ref float3 low, ref float3 high) {

        for (int edge = 0; edge < 4; edge++) {

            for (int k = 0; k < Vertices; k++) {

                int v = EdgeVertex(edge, k);
                Vector3d drop = directions[v] * depth;

                Vertex hanging = MakeVertex(surface[v] - drop, coarse[v] - drop, centre, v % Vertices, v / Vertices, ref low, ref high);

                hanging.Parent = new float4(vertices[first + v].Parent.xyz, (float)(depth / 1_000.0));
                hanging.Water = vertices[first + v].Water;

                // Skirts carry their texture coordinates shifted by SkirtFlag, so the sun's shadow pass can leave them out.
                hanging.Uv.x += SkirtFlag;
                vertices[first + Vertices * Vertices + edge * Vertices + k] = hanging;

            }

        }

    }

    private const MeshUpdateFlags Flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;

    private static Vertex MakeVertex(Vector3d position, Vector3d coarse, Vector3d centre, int i, int j, ref float3 low, ref float3 high) {

        float3 local = Scene(position - centre);
        float3 target = Scene(coarse - centre);

        low = math.min(low, math.min(local, target));
        high = math.max(high, math.max(local, target));

        return new Vertex { Position = local, Uv = new float2(i / (float)Quads, j / (float)Quads), Morph = target - local };

    }

    private float2 WaterOf(NativeArray<double> levels, int v) =>
        new float2(double.IsNaN(levels[v]) ? NoLevel : (float)(levels[v] - Heights[v]), (float)Shores[v]);

    // The end of the parent's edge or diagonal an odd vertex morphs onto, as Between takes it; an even vertex's own.
    private static int ParentEnd(int i, int j) {

        bool oddI = (i & 1) == 1;
        bool oddJ = (j & 1) == 1;

        if (oddI && oddJ) {

            return (j - 1) * Vertices + i + 1;

        }

        if (oddI) {

            return j * Vertices + i - 1;

        }

        return oddJ ? (j - 1) * Vertices + i : j * Vertices + i;

    }

    /// <summary>A current (m/s) as a water-detail channel.</summary>
    public static ushort EncodeFlow(double speed) => Unorm((float)(0.5 + 0.5 * speed / FlowRange));

    /// <summary>A share, 0 to 1, as a water-detail channel.</summary>
    public static ushort EncodeUnit(double share) => Unorm((float)share);

    // Sim frame (Z up, metres) to the patch's object space: kilometres, Y up, as MapSpace.
    public static float3 Scene(Vector3d v) => new float3((float)(v.X / 1_000.0), (float)(v.Z / 1_000.0), (float)(v.Y / 1_000.0));

    private static int EdgeVertex(int edge, int k) => edge switch {

        0 => k,
        1 => Quads * Vertices + k,
        2 => k * Vertices,
        _ => k * Vertices + Quads,

    };

    // A quad of the sheet: the triangles of it that are wet.
    private static void AddWaterQuad(NativeArray<ushort> indices, ref int count, NativeArray<Vertex> vertices, NativeArray<double> heights, NativeArray<double> levels,
        int i, int j, Vector3d centre) {

        int a = j * Vertices + i;
        int b = a + 1;
        int c = a + Vertices;
        int d = c + 1;

        if (Wet(heights, levels, a, b, c)) {

            AddTriangle(indices, ref count, vertices, GroundVertices + a, GroundVertices + b, GroundVertices + c, centre);

        }

        if (Wet(heights, levels, b, d, c)) {

            AddTriangle(indices, ref count, vertices, GroundVertices + b, GroundVertices + d, GroundVertices + c, centre);

        }

    }

    // The sheet's skirts, under each wet stretch of its edges.
    private static void AddWaterSkirts(NativeArray<ushort> indices, ref int count, NativeArray<Vertex> vertices, NativeArray<double> heights, NativeArray<double> levels) {

        for (int edge = 0; edge < 4; edge++) {

            for (int k = 0; k < Quads; k++) {

                int v0 = EdgeVertex(edge, k);
                int v1 = EdgeVertex(edge, k + 1);

                if (!Wet(heights, levels, v0, v1, v1)) {

                    continue;

                }

                int s0 = GroundVertices + Vertices * Vertices + edge * Vertices + k;
                int s1 = s0 + 1;
                float3 outward = vertices[v0].Position + vertices[v1].Position;

                AddFacing(indices, ref count, vertices, GroundVertices + v0, GroundVertices + v1, s0, outward);
                AddFacing(indices, ref count, vertices, GroundVertices + v1, s1, s0, outward);

            }

        }

    }

    // Whether the two by two quads from vertex a all lie under water, far enough from any shore to need no finer edge.
    private static bool Open(NativeArray<double> heights, NativeArray<double> levels, int a) {

        for (int j = 0; j <= 2; j++) {

            for (int i = 0; i <= 2; i++) {

                if (!Below(heights, levels, a + j * Vertices + i, 0.0)) {

                    return false;

                }

            }

        }

        return true;

    }

    private static bool Submerged(NativeArray<double> heights, NativeArray<double> levels, int a, int b, int c, double deep) =>
        Below(heights, levels, a, deep) && Below(heights, levels, b, deep) && Below(heights, levels, c, deep);

    private static bool Below(NativeArray<double> heights, NativeArray<double> levels, int v, double deep) =>
        !double.IsNaN(levels[v]) && heights[v] < levels[v] - deep;

    // A water triangle is kept when every corner has a level and some corner's ground lies under it.
    private static bool Wet(NativeArray<double> heights, NativeArray<double> levels, int a, int b, int c) {

        if (double.IsNaN(levels[a]) || double.IsNaN(levels[b]) || double.IsNaN(levels[c])) {

            return false;

        }

        return heights[a] < levels[a] + 1.0 || heights[b] < levels[b] + 1.0 || heights[c] < levels[c] + 1.0;

    }

    private static void AddTriangle(NativeArray<ushort> indices, ref int count, NativeArray<Vertex> vertices, int a, int b, int c, Vector3d centre) {

        float3 radial = vertices[a].Position + Scene(centre);

        AddFacing(indices, ref count, vertices, a, b, c, radial);

    }

    // Unity treats clockwise as front, and the sim-to-scene axis swap mirrors faces: wind each triangle to face along outward.
    private static void AddFacing(NativeArray<ushort> indices, ref int count, NativeArray<Vertex> vertices, int a, int b, int c, float3 outward) {

        float3 facing = math.cross(vertices[b].Position - vertices[a].Position, vertices[c].Position - vertices[a].Position);
        bool keep = math.dot(facing, outward) > 0.0f;

        indices[count++] = (ushort)a;
        indices[count++] = (ushort)(keep ? b : c);
        indices[count++] = (ushort)(keep ? c : b);

    }

    // The share of skylight each vertex receives, cosine-weighted, under its horizon.
    private NativeArray<float> Occlusion() {

        const int size = PatchSamples.GridSize;

        NativeArray<float> occlusion = new NativeArray<float>(Vertices * Vertices, Allocator.Temp);

        for (int j = 0; j < Vertices; j++) {

            for (int i = 0; i < Vertices; i++) {

                int v = j * Vertices + i;
                int centre = (j + HorizonReach) * size + i + HorizonReach;
                Vector3d p = Grid[centre];
                Vector3d up = p.Normalized;
                float open = 0.0f;

                for (int d = 0; d < HorizonDirections; d++) {

                    int dx = d == 0 || d == 1 || d == 7 ? 1 : d == 3 || d == 4 || d == 5 ? -1 : 0;
                    int dy = d == 1 || d == 2 || d == 3 ? 1 : d == 5 || d == 6 || d == 7 ? -1 : 0;
                    double highest = 0.0;

                    for (int step = 1; step <= HorizonReach; step += (step + 2) / 3) {

                        Vector3d toward = Grid[centre + (dy * size + dx) * step] - p;

                        highest = Math.Max(highest, Vector3d.Dot(toward, up) / toward.Length);

                    }

                    byte horizon = (byte)Math.Round(highest * 255.0);

                    if (Depth > 0) {

                        horizon = Math.Max(horizon, ParentHorizon(i, j, d));

                    }

                    float sine = horizon / 255.0f;

                    Horizons[v * HorizonDirections + d] = horizon;
                    open += 1.0f - sine * sine;

                }

                occlusion[v] = open / HorizonDirections;

            }

        }

        return occlusion;

    }

    // The parent's horizon at one of this patch's vertices: the parent's vertices fall on this patch's even ones.
    private byte ParentHorizon(int i, int j, int direction) {

        int x = (X & 1) * Quads / 2 + i / 2;
        int y = (Y & 1) * Quads / 2 + j / 2;
        int x1 = Math.Min(x + (i & 1), Quads);
        int y1 = Math.Min(y + (j & 1), Quads);

        int sum = ParentHorizons[(y * Vertices + x) * HorizonDirections + direction] + ParentHorizons[(y * Vertices + x1) * HorizonDirections + direction] +
            ParentHorizons[(y1 * Vertices + x) * HorizonDirections + direction] + ParentHorizons[(y1 * Vertices + x1) * HorizonDirections + direction];

        return (byte)((sum + 2) / 4);

    }

    // Normals from the terrain sampled at four times the vertex density, so ridges and cliff bands too fine for the mesh
    // still shade; the signed water depth under each texel; and the vertices' occlusion spread across the texels between them.
    private void WriteDetail(NativeArray<float> occlusion) {

        const int samples = PatchSamples.FineSize;

        for (int l = 0; l < Texels; l++) {

            for (int k = 0; k < Texels; k++) {

                int p = (l + 1) * samples + k + 1;
                Vector3d east = Fine[p + 1] - Fine[p - 1];
                Vector3d north = Fine[p + samples] - Fine[p - samples];
                Vector3d normal = Vector3d.Cross(east, north).Normalized;

                if (Vector3d.Dot(normal, Fine[p]) < 0.0) {

                    normal = -normal;

                }

                float2 octahedral = Octahedral(new float3((float)normal.X, (float)normal.Z, (float)normal.Y));
                int t = (l * Texels + k) * 4;

                Detail[t] = Unorm(octahedral.x * 0.5f + 0.5f);
                Detail[t + 1] = Unorm(octahedral.y * 0.5f + 0.5f);
                double depth = Depths[l * Texels + k];

                Detail[t + 2] = Unorm((float)(0.5 + 0.5 * Math.Sign(depth) * Math.Sqrt(Math.Min(Math.Abs(depth), WaterDepthRange) / WaterDepthRange)));
                int i0 = k / TexelsPerQuad;
                int j0 = l / TexelsPerQuad;
                int i1 = Math.Min(i0 + 1, Quads);
                int j1 = Math.Min(j0 + 1, Quads);
                float fx = (k % TexelsPerQuad) / (float)TexelsPerQuad;
                float fy = (l % TexelsPerQuad) / (float)TexelsPerQuad;
                float top = math.lerp(occlusion[j0 * Vertices + i0], occlusion[j0 * Vertices + i1], fx);
                float bottom = math.lerp(occlusion[j1 * Vertices + i0], occlusion[j1 * Vertices + i1], fx);

                Detail[t + 3] = Unorm(math.lerp(top, bottom, fy));

            }

        }

    }

    private static ushort Unorm(float x) => (ushort)math.round(math.saturate(x) * 65_535.0f);

    private static float2 Octahedral(float3 n) {

        n /= math.abs(n.x) + math.abs(n.y) + math.abs(n.z);

        if (n.y >= 0.0f) {

            return n.xz;

        }

        return (1.0f - math.abs(n.zx)) * math.select(new float2(-1.0f), new float2(1.0f), n.xz >= 0.0f);

    }

}
