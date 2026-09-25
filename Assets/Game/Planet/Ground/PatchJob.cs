using System;
using System.Runtime.InteropServices;

using MaxQ.Sim.Numerics;
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

/// <summary>Builds one quadtree node's ground and water mesh plus its detail texture from the sim's terrain function.</summary>
[BurstCompile]
internal struct PatchJob : IJob {

    public const int Quads = 32;
    public const int Vertices = Quads + 1;
    public const int Texels = 2 * Quads + 1;

    public const int GroundVertices = Vertices * Vertices + 4 * Vertices;
    public const int WaterVertices = Vertices * Vertices;
    public const int MaxIndices = 2 * Quads * Quads * 6 + 4 * Quads * 6;

    // Water depth range the detail texture encodes, metres either side of the surface. Depth is stored as a signed
    // square root: fine near the shore, and it never saturates, so coarse patches keep smooth coastlines.
    public const double WaterDepthRange = 16_384.0;

    public const int InfoMinHeight = 0;
    public const int InfoMaxHeight = 1;
    public const int InfoRadius = 2;
    public const int InfoGroundIndices = 3;
    public const int InfoWaterIndices = 4;
    public const int InfoLength = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vertex {

        public float3 Position;
        public float2 Uv;
        public float3 Morph;

    }

    public static readonly VertexAttributeDescriptor[] Layout = {

        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 3),

    };

    // Read-only views of the memory-mapped survey, shared by every job.
    [NativeDisableUnsafePtrRestriction]
    public Terrain Terrain;

    public int Face;
    public int Depth;
    public int X;
    public int Y;

    public Mesh.MeshData Mesh;
    public NativeArray<ushort> Detail;
    public NativeArray<double> Info;

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

    public void Execute() {

        double radius = Terrain.Radius;
        double span = 2.0 / (1L << Depth);
        double a0 = X * span - 1.0;
        double b0 = Y * span - 1.0;
        double footprint = Footprint(radius, Depth);
        Vector3d centre = CentreDirection(Face, Depth, X, Y) * radius;

        NativeArray<Vector3d> directions = new NativeArray<Vector3d>(Vertices * Vertices, Allocator.Temp);
        NativeArray<Vector3d> ground = new NativeArray<Vector3d>(Vertices * Vertices, Allocator.Temp);
        NativeArray<Vector3d> coarse = new NativeArray<Vector3d>(Vertices * Vertices, Allocator.Temp);
        NativeArray<double> heights = new NativeArray<double>(Vertices * Vertices, Allocator.Temp);
        NativeArray<double> levels = new NativeArray<double>(Vertices * Vertices, Allocator.Temp);

        double minHeight = double.MaxValue;
        double maxHeight = double.MinValue;

        for (int j = 0; j < Vertices; j++) {

            for (int i = 0; i < Vertices; i++) {

                int v = j * Vertices + i;
                Vector3d direction = CubeFace.Direction(Face, a0 + i * span / Quads, b0 + j * span / Quads);
                double height = Terrain.HeightAt(direction, footprint);
                double level = Terrain.WaterLevelAt(direction);

                directions[v] = direction;
                heights[v] = height;
                levels[v] = level;
                ground[v] = direction * (radius + height);

                minHeight = Math.Min(minHeight, height);
                maxHeight = Math.Max(maxHeight, double.IsNaN(level) ? height : Math.Max(height, level));

            }

        }

        // Where the parent level would put each vertex: its own posts at the parent's footprint, and the
        // points between them on the parent's edges and diagonals.
        for (int j = 0; j < Vertices; j += 2) {

            for (int i = 0; i < Vertices; i += 2) {

                int v = j * Vertices + i;

                coarse[v] = Depth == 0 ? ground[v] : directions[v] * (radius + Terrain.HeightAt(directions[v], 2.0 * footprint));

            }

        }

        Between(coarse);

        // Triangles are judged on levels spread one vertex past each body; the sheet's positions on two, so every
        // vertex of a kept triangle has levelled neighbours to morph between.
        NativeArray<double> reach1 = Spread(levels);
        NativeArray<double> reach2 = Spread(reach1);

        WriteMesh(ground, coarse, directions, heights, reach1, reach2, centre, footprint);
        WriteDetail(a0, b0, span, footprint);

        double reach = 0.0;
        Vector3d middle = centre / radius * (radius + 0.5 * (minHeight + maxHeight));

        for (int v = 0; v < ground.Length; v++) {

            reach = Math.Max(reach, (ground[v] - middle).Length);

        }

        Info[InfoMinHeight] = minHeight;
        Info[InfoMaxHeight] = maxHeight;
        Info[InfoRadius] = reach + 0.5 * (maxHeight - minHeight);

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

                vertices[v] = MakeVertex(ground[v], coarse[v], centre, i, j, ref low, ref high);

            }

        }

        // Skirts hang below each edge to hide the cracks of a neighbour that is still streaming in.
        for (int edge = 0; edge < 4; edge++) {

            for (int k = 0; k < Vertices; k++) {

                int v = EdgeVertex(edge, k);
                Vector3d drop = directions[v] * skirt;

                vertices[Vertices * Vertices + edge * Vertices + k] = MakeVertex(ground[v] - drop, coarse[v] - drop, centre, v % Vertices, v / Vertices, ref low, ref high);

            }

        }

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

                vertices[GroundVertices + v] = MakeVertex(sheet[v], sheetCoarse[v], centre, i, j, ref low, ref high);

            }

        }

        int count = 0;
        double deep = Math.Max(2.0, 0.05 * footprint);

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

        }

        Bounds bounds = new Bounds((Vector3)((low + high) * 0.5f), (Vector3)(high - low));

        Mesh.subMeshCount = 2;
        Mesh.SetSubMesh(0, new SubMeshDescriptor(0, groundCount) { bounds = bounds, vertexCount = GroundVertices }, Flags);
        Mesh.SetSubMesh(1, new SubMeshDescriptor(groundCount, count - groundCount) { bounds = bounds, firstVertex = GroundVertices, vertexCount = WaterVertices }, Flags);

        Info[InfoGroundIndices] = groundCount;
        Info[InfoWaterIndices] = count - groundCount;

    }

    private const MeshUpdateFlags Flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;

    private static Vertex MakeVertex(Vector3d position, Vector3d coarse, Vector3d centre, int i, int j, ref float3 low, ref float3 high) {

        float3 local = Scene(position - centre);
        float3 target = Scene(coarse - centre);

        low = math.min(low, math.min(local, target));
        high = math.max(high, math.max(local, target));

        return new Vertex { Position = local, Uv = new float2(i / (float)Quads, j / (float)Quads), Morph = target - local };

    }

    // Sim frame (Z up, metres) to the patch's object space: kilometres, Y up, as MapSpace.
    private static float3 Scene(Vector3d v) => new float3((float)(v.X / 1_000.0), (float)(v.Z / 1_000.0), (float)(v.Y / 1_000.0));

    private static int EdgeVertex(int edge, int k) => edge switch {

        0 => k,
        1 => Quads * Vertices + k,
        2 => k * Vertices,
        _ => k * Vertices + Quads,

    };

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

    // Normals from the terrain sampled at twice the vertex density, and the signed water depth under each texel.
    private void WriteDetail(double a0, double b0, double span, double footprint) {

        const int samples = Texels + 2;

        NativeArray<Vector3d> points = new NativeArray<Vector3d>(samples * samples, Allocator.Temp);
        NativeArray<double> depths = new NativeArray<double>(Texels * Texels, Allocator.Temp);
        double radius = Terrain.Radius;
        double step = span / (2 * Quads);

        for (int l = 0; l < samples; l++) {

            for (int k = 0; k < samples; k++) {

                Vector3d direction = CubeFace.Direction(Face, a0 + (k - 1) * step, b0 + (l - 1) * step);
                double height = Terrain.HeightAt(direction, 0.5 * footprint);

                points[l * samples + k] = direction * (radius + height);

                if (k >= 1 && l >= 1 && k <= Texels && l <= Texels) {

                    double level = Terrain.WaterLevelAt(direction);

                    depths[(l - 1) * Texels + k - 1] = double.IsNaN(level) ? -WaterDepthRange : level - height;

                }

            }

        }

        for (int l = 0; l < Texels; l++) {

            for (int k = 0; k < Texels; k++) {

                int p = (l + 1) * samples + k + 1;
                Vector3d east = points[p + 1] - points[p - 1];
                Vector3d north = points[p + samples] - points[p - samples];
                Vector3d normal = Vector3d.Cross(east, north).Normalized;

                if (Vector3d.Dot(normal, points[p]) < 0.0) {

                    normal = -normal;

                }

                float2 octahedral = Octahedral(new float3((float)normal.X, (float)normal.Z, (float)normal.Y));
                int t = (l * Texels + k) * 4;

                Detail[t] = Unorm(octahedral.x * 0.5f + 0.5f);
                Detail[t + 1] = Unorm(octahedral.y * 0.5f + 0.5f);
                double depth = depths[l * Texels + k];

                Detail[t + 2] = Unorm((float)(0.5 + 0.5 * Math.Sign(depth) * Math.Sqrt(Math.Min(Math.Abs(depth), WaterDepthRange) / WaterDepthRange)));
                Detail[t + 3] = ushort.MaxValue;

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
