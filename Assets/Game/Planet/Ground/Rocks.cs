using System;
using System.Collections.Generic;

using MaxQ.Sim.Surface;

using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Planet.Ground;

/// <summary>Draws the rocks the ground's patches strew: a dozen procedural stones in three families (rounded, angular and
/// slabby), each in a near and a far detail. Every shape shares one topology, so each patch's rocks draw in one indexed
/// procedural call per detail, and the shader takes their colour from the patch's satellite tile. Rocks shrink away
/// short of a distance that grows with their size, so none pops out.</summary>
public sealed class Rocks : IDisposable {

    private const int Shapes = 12;
    private const int Capacity = 16_384;

    // Metres of distance per metre of rock at which a rock is gone, and where it swaps to its far detail.
    private const float Reach = 120.0f;
    private const float NearReach = 25.0f;
    private const float Fade = 0.25f;

    private static readonly int VerticesId = Shader.PropertyToID("_RockVertices");
    private static readonly int InstancesId = Shader.PropertyToID("_RockInstances");
    private static readonly int ShapeVerticesId = Shader.PropertyToID("_RockShapeVertices");
    private static readonly int OffsetId = Shader.PropertyToID("_RockInstanceOffset");
    private static readonly int ColourId = Shader.PropertyToID("_Colour");
    private static readonly int ColourRectId = Shader.PropertyToID("_ColourRect");

    private sealed class Detail {

        public GraphicsBuffer Vertices;
        public GraphicsBuffer Indices;
        public int ShapeVertices;

    }

    private struct Batch {

        public Detail Detail;
        public int Offset;
        public int Count;
        public Texture Colour;
        public Vector4 Rect;
        public Bounds Bounds;

    }

    private readonly Material _material;
    private readonly Detail _near;
    private readonly Detail _far;
    private readonly GraphicsBuffer _instances;
    private readonly float4[] _staging = new float4[3 * Capacity];
    private readonly List<Batch> _batches = new List<Batch>();
    private readonly List<MaterialPropertyBlock> _blocks = new List<MaterialPropertyBlock>();
    private int _count;

    public Rocks(Material material) {

        _material = material;
        _near = Build(4);
        _far = Build(2);
        _instances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3 * Capacity, 16);

    }

    /// <summary>Queues one patch's rocks, the patch standing at <paramref name="position"/> turned by
    /// <paramref name="rotation"/> and coloured by its satellite tile, as seen from <paramref name="camera"/>.</summary>
    public void Add(float4[] rocks, Texture colour, Vector4 rect, Vector3 position, Quaternion rotation, Vector3 camera) {

        AddDetail(_near, rocks, colour, rect, position, rotation, camera);
        AddDetail(_far, rocks, colour, rect, position, rotation, camera);

    }

    private void AddDetail(Detail detail, float4[] rocks, Texture colour, Vector4 rect, Vector3 position, Quaternion rotation, Vector3 camera) {

        Batch batch = new Batch { Detail = detail, Offset = _count, Colour = colour, Rect = rect };

        for (int i = 0; i < rocks.Length / 3 && _count < Capacity; i++) {

            float4 placed = rocks[3 * i];
            Vector3 at = position + rotation * new Vector3(placed.x, placed.y, placed.z);
            float distance = Vector3.Distance(at, camera) * 1_000.0f;
            float reach = placed.w * Reach;

            if (distance >= reach || (distance < placed.w * NearReach) != (detail == _near)) {

                continue;

            }

            float4 turn = rocks[3 * i + 1];
            Quaternion facing = rotation * new Quaternion(turn.x, turn.y, turn.z, turn.w);
            float size = placed.w * Mathf.Clamp01((reach - distance) / (Fade * reach)) / 1_000.0f;

            // Each batch's bounds hug its rocks: URP fits the shadow cascades' depth range to what casts into them.
            if (batch.Count == 0) {

                batch.Bounds = new Bounds(at, Vector3.one * size);

            } else {

                batch.Bounds.Encapsulate(new Bounds(at, Vector3.one * size));

            }

            _staging[3 * _count] = new float4(at.x, at.y, at.z, size);
            _staging[3 * _count + 1] = new float4(facing.x, facing.y, facing.z, facing.w);
            _staging[3 * _count + 2] = rocks[3 * i + 2];
            _count++;
            batch.Count++;

        }

        if (batch.Count > 0) {

            _batches.Add(batch);

        }

    }

    /// <summary>Draws whatever is queued and starts the next frame's queue.</summary>
    public void Draw() {

        if (_count > 0) {

            _instances.SetData(_staging, 0, 0, 3 * _count);

        }

        for (int i = 0; i < _batches.Count; i++) {

            Batch batch = _batches[i];

            if (_blocks.Count <= i) {

                _blocks.Add(new MaterialPropertyBlock());

            }

            MaterialPropertyBlock block = _blocks[i];

            block.SetBuffer(VerticesId, batch.Detail.Vertices);
            block.SetBuffer(InstancesId, _instances);
            block.SetInt(ShapeVerticesId, batch.Detail.ShapeVertices);
            block.SetInt(OffsetId, batch.Offset);
            block.SetTexture(ColourId, batch.Colour);
            block.SetVector(ColourRectId, batch.Rect);

            RenderParams parameters = new RenderParams(_material) {

                matProps = block,
                worldBounds = batch.Bounds,
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = true,

            };

            Graphics.RenderPrimitivesIndexed(parameters, MeshTopology.Triangles, batch.Detail.Indices, batch.Detail.Indices.count, 0, batch.Count);

        }

        _batches.Clear();
        _count = 0;

    }

    public void Dispose() {

        _instances.Release();

        foreach (Detail detail in new[] { _near, _far }) {

            detail.Vertices.Release();
            detail.Indices.Release();

        }

    }

    // Every shape at one detail, one after another in a vertex buffer, sharing the icosphere's triangles.
    private static Detail Build(int subdivisions) {

        List<Vector3> sphere = new List<Vector3>();
        List<int> triangles = new List<int>();

        Icosphere(sphere, triangles, subdivisions);

        Vector3[] vertices = new Vector3[2 * Shapes * sphere.Count];
        Mesh mesh = new Mesh();

        for (int shape = 0; shape < Shapes; shape++) {

            mesh.Clear();
            mesh.SetVertices(Stone(shape, sphere));
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();

            Vector3[] positions = mesh.vertices;
            Vector3[] normals = mesh.normals;

            for (int v = 0; v < sphere.Count; v++) {

                vertices[2 * (shape * sphere.Count + v)] = positions[v];
                vertices[2 * (shape * sphere.Count + v) + 1] = normals[v];

            }

        }

        UnityEngine.Object.Destroy(mesh);

        Detail detail = new Detail {

            Vertices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Shapes * sphere.Count, 24),
            Indices = new GraphicsBuffer(GraphicsBuffer.Target.Index, triangles.Count, sizeof(int)),
            ShapeVertices = sphere.Count,

        };

        detail.Vertices.SetData(vertices);
        detail.Indices.SetData(triangles);

        return detail;

    }

    // A unit rock: an icosphere swollen and pitted by noise, then cut by planes into the flat faces of broken stone,
    // squashed, and with its base (at -0.3) sunk into the ground. Rounded stones take a few shallow cuts, angular ones many
    // deep ones, and slabs are cut flat above and below.
    private static List<Vector3> Stone(int shape, List<Vector3> sphere) {

        System.Random random = new System.Random(shape * 7_919 + 17);
        int family = shape % 3;
        int count = family == 0 ? 3 : family == 1 ? 8 : 6;
        Vector3[] cuts = new Vector3[count];
        float[] depths = new float[count];

        for (int c = 0; c < count; c++) {

            cuts[c] = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f).normalized;
            depths[c] = family == 0 ? 0.75f + 0.2f * (float)random.NextDouble() : 0.55f + 0.2f * (float)random.NextDouble();

            if (family == 2 && c < 2) {

                cuts[c] = new Vector3(0.15f * ((float)random.NextDouble() - 0.5f), c == 0 ? 1.0f : -1.0f, 0.15f * ((float)random.NextDouble() - 0.5f)).normalized;
                depths[c] = 0.4f + 0.1f * (float)random.NextDouble();

            }

        }

        Vector3 squash = family == 2
            ? new Vector3(1.2f, 0.8f + 0.2f * (float)random.NextDouble(), 0.9f + 0.3f * (float)random.NextDouble())
            : new Vector3(1.0f, 0.55f + 0.25f * (float)random.NextDouble(), 0.75f + 0.25f * (float)random.NextDouble());
        float swelling = family == 0 ? 0.18f : 0.12f;
        List<Vector3> stone = new List<Vector3>(sphere.Count);

        foreach (Vector3 d in sphere) {

            double swell = Relief.Noise(d.x * 1.3 + shape * 5.1, d.y * 1.3, d.z * 1.3, (uint)shape);
            double pits = Relief.Noise(d.x * 4.7, d.y * 4.7 + shape * 3.3, d.z * 4.7, (uint)(shape + 11));
            float r = 1.0f + swelling * (float)swell + 0.05f * (float)pits;

            for (int c = 0; c < count; c++) {

                float facing = Vector3.Dot(d, cuts[c]);

                if (facing > 0.0f) {

                    r = Mathf.Min(r, depths[c] / facing);

                }

            }

            Vector3 p = Vector3.Scale(d * r, squash) * 0.5f;

            p.y = Mathf.Max(p.y, -0.3f);
            stone.Add(p);

        }

        return stone;

    }

    private static void Icosphere(List<Vector3> vertices, List<int> triangles, int subdivisions) {

        float t = (1.0f + Mathf.Sqrt(5.0f)) * 0.5f;

        foreach (Vector3 v in new[] {

            new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
            new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
            new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),

        }) {

            vertices.Add(v.normalized);

        }

        triangles.AddRange(new[] {

            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,

        });

        for (int level = 0; level < subdivisions; level++) {

            Dictionary<long, int> middles = new Dictionary<long, int>();
            List<int> finer = new List<int>(triangles.Count * 4);

            for (int i = 0; i < triangles.Count; i += 3) {

                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];
                int ab = Middle(vertices, middles, a, b);
                int bc = Middle(vertices, middles, b, c);
                int ca = Middle(vertices, middles, c, a);

                finer.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });

            }

            triangles.Clear();
            triangles.AddRange(finer);

        }

    }

    private static int Middle(List<Vector3> vertices, Dictionary<long, int> middles, int a, int b) {

        long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);

        if (middles.TryGetValue(key, out int index)) {

            return index;

        }

        vertices.Add(((vertices[a] + vertices[b]) * 0.5f).normalized);
        middles[key] = vertices.Count - 1;

        return vertices.Count - 1;

    }

}
