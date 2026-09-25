using System;
using System.Collections.Generic;

using MaxQ.Sim.Surface;

using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Planet.Ground;

/// <summary>Draws the boulders the ground's patches strew: a few procedural stones, each in a near and a far detail, drawn
/// instanced. Boulders shrink away short of a distance that grows with their size, so none pops out.</summary>
public sealed class Rocks {

    private const int Shapes = 4;
    private const int Batch = 1023;

    // Metres of distance per metre of boulder at which a boulder is gone, and where it swaps to its far detail.
    private const float Reach = 120.0f;
    private const float NearReach = 25.0f;
    private const float Fade = 0.25f;

    private readonly Mesh[] _near = new Mesh[Shapes];
    private readonly Mesh[] _far = new Mesh[Shapes];
    private readonly Matrix4x4[][] _batches = new Matrix4x4[2 * Shapes][];
    private readonly int[] _counts = new int[2 * Shapes];
    private readonly Bounds[] _bounds = new Bounds[2 * Shapes];
    private RenderParams _params;

    public Rocks(Material material) {

        for (int shape = 0; shape < Shapes; shape++) {

            _near[shape] = Boulder(shape, 4);
            _far[shape] = Boulder(shape, 2);
            _batches[2 * shape] = new Matrix4x4[Batch];
            _batches[2 * shape + 1] = new Matrix4x4[Batch];

        }

        _params = new RenderParams(material) {

            shadowCastingMode = ShadowCastingMode.On,
            receiveShadows = true,

        };

    }

    /// <summary>Queues one patch's boulders, the patch standing at <paramref name="position"/> turned by
    /// <paramref name="rotation"/>, as seen from <paramref name="camera"/>.</summary>
    public void Add(float4[] rocks, int count, Vector3 position, Quaternion rotation, Vector3 camera) {

        for (int i = 0; i < count; i++) {

            float4 placed = rocks[2 * i];
            Vector3 at = position + rotation * new Vector3(placed.x, placed.y, placed.z);
            float distance = Vector3.Distance(at, camera) * 1_000.0f;
            float reach = placed.w * Reach;

            if (distance >= reach) {

                continue;

            }

            float4 turn = rocks[2 * i + 1];
            float size = placed.w * Mathf.Clamp01((reach - distance) / (Fade * reach)) / 1_000.0f;
            int batch = 2 * (i % Shapes) + (distance < placed.w * NearReach ? 0 : 1);

            // Each batch's bounds hug its boulders: URP fits the shadow cascades' depth range to what casts into them.
            if (_counts[batch] == 0) {

                _bounds[batch] = new Bounds(at, Vector3.one * size);

            } else {

                _bounds[batch].Encapsulate(new Bounds(at, Vector3.one * size));

            }

            _batches[batch][_counts[batch]++] = Matrix4x4.TRS(at, rotation * new Quaternion(turn.x, turn.y, turn.z, turn.w), Vector3.one * size);

            if (_counts[batch] == Batch) {

                Flush(batch);

            }

        }

    }

    /// <summary>Draws whatever is still queued.</summary>
    public void Draw() {

        for (int batch = 0; batch < _batches.Length; batch++) {

            Flush(batch);

        }

    }

    private void Flush(int batch) {

        if (_counts[batch] == 0) {

            return;

        }

        Mesh mesh = (batch & 1) == 0 ? _near[batch / 2] : _far[batch / 2];

        _params.worldBounds = _bounds[batch];
        Graphics.RenderMeshInstanced(_params, mesh, 0, _batches[batch], _counts[batch]);
        _counts[batch] = 0;

    }

    // A unit boulder: an icosphere swollen and pitted by noise, then cut by a few planes into the flat faces of broken
    // stone, squashed, and with its base (at -0.3) sunk into the ground.
    private static Mesh Boulder(int shape, int subdivisions) {

        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();

        Icosphere(vertices, triangles, subdivisions);

        System.Random random = new System.Random(shape * 7_919 + 17);
        Vector3[] cuts = new Vector3[6];
        float[] depths = new float[cuts.Length];

        for (int c = 0; c < cuts.Length; c++) {

            cuts[c] = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f).normalized;
            depths[c] = 0.6f + 0.25f * (float)random.NextDouble();

        }

        Vector3 squash = new Vector3(1.0f, 0.55f + 0.25f * (float)random.NextDouble(), 0.75f + 0.25f * (float)random.NextDouble());

        for (int v = 0; v < vertices.Count; v++) {

            Vector3 d = vertices[v];
            double swell = Relief.Noise(d.x * 1.3 + shape * 5.1, d.y * 1.3, d.z * 1.3, (uint)shape);
            double pits = Relief.Noise(d.x * 4.7, d.y * 4.7 + shape * 3.3, d.z * 4.7, (uint)(shape + 11));
            float r = 1.0f + 0.22f * (float)swell + 0.05f * (float)pits;

            for (int c = 0; c < cuts.Length; c++) {

                float facing = Vector3.Dot(d, cuts[c]);

                if (facing > 0.0f) {

                    r = Mathf.Min(r, depths[c] / facing);

                }

            }

            Vector3 p = Vector3.Scale(d * r, squash) * 0.5f;

            p.y = Mathf.Max(p.y, -0.3f);
            vertices[v] = p;

        }

        Mesh mesh = new Mesh { name = $"Boulder {shape}" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.UploadMeshData(true);

        return mesh;

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
