using System;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Vessels.Hull;

/// <summary>Collects a mesh in metres and the stack's scene axes (Y up the stack), one submesh per material, out of
/// surfaces of revolution and boxes.</summary>
internal sealed class MeshBuilder {

    private readonly List<Vector3> _positions = new List<Vector3>();
    private readonly List<Vector3> _normals = new List<Vector3>();
    private readonly List<Vector2> _uvs = new List<Vector2>();
    private readonly List<int>[] _triangles;

    public MeshBuilder(int materials) {

        _triangles = new List<int>[materials];

        for (int i = 0; i < materials; i++) {

            _triangles[i] = new List<int>();

        }

    }

    /// <summary>Revolves a profile of (radius, height) points, listed bottom to top, about the stack axis. The profile's
    /// normals come from its own slope; <paramref name="bumps"/> adds radius round the circumference (angle to metres).
    /// UVs run in metres: round the circumference, then up.</summary>
    public void Lathe(IReadOnlyList<Vector2> profile, int segments, int material, Func<float, float> bumps = null, float startAngle = 0.0f,
        float sweep = 2.0f * Mathf.PI) {

        int first = _positions.Count;
        int columns = segments + 1;
        float[] arc = new float[profile.Count];

        for (int i = 1; i < profile.Count; i++) {

            arc[i] = arc[i - 1] + Vector2.Distance(profile[i], profile[i - 1]);

        }

        for (int i = 0; i < profile.Count; i++) {

            Vector2 before = profile[Math.Max(i - 1, 0)];
            Vector2 after = profile[Math.Min(i + 1, profile.Count - 1)];
            Vector2 slope = (after - before).normalized;

            // Outward normal of the profile: its tangent turned a quarter towards the outside.
            Vector2 outward = new Vector2(slope.y, -slope.x);

            for (int j = 0; j < columns; j++) {

                float angle = startAngle + sweep * j / segments;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);
                float radius = profile[i].x;
                Vector3 normal = new Vector3(outward.x * cos, outward.y, outward.x * sin);

                if (bumps != null && radius > 0.0f) {

                    float step = sweep / segments;
                    float lift = bumps(angle);
                    float lean = (bumps(angle + 0.5f * step) - bumps(angle - 0.5f * step)) / (radius * step);

                    radius += lift * outward.x;
                    normal = (normal - lean * outward.x * new Vector3(-sin, 0.0f, cos)).normalized;

                }

                _positions.Add(new Vector3(radius * cos, profile[i].y, radius * sin));
                _normals.Add(normal);
                _uvs.Add(new Vector2(angle * profile[i].x, arc[i]));

            }

        }

        List<int> triangles = _triangles[material];

        for (int i = 0; i + 1 < profile.Count; i++) {

            for (int j = 0; j < segments; j++) {

                int a = first + i * columns + j;
                int b = a + 1;
                int c = a + columns + 1;
                int d = a + columns;

                triangles.Add(a);
                triangles.Add(d);
                triangles.Add(c);
                triangles.Add(a);
                triangles.Add(c);
                triangles.Add(b);

            }

        }

    }

    /// <summary>A flat annulus or disc facing <paramref name="up"/> (true: +Y) at a height.</summary>
    public void Disc(float inner, float outer, float height, bool up, int segments, int material) {

        Vector2[] profile = up ? new[] { new Vector2(outer, height), new Vector2(inner, height) } : new[] { new Vector2(inner, height), new Vector2(outer, height) };

        Lathe(profile, segments, material);

        // A flat profile's slope gives a sideways normal; a disc faces straight along the axis.
        for (int i = _normals.Count - 2 * (segments + 1); i < _normals.Count; i++) {

            _normals[i] = up ? Vector3.up : Vector3.down;

        }

    }

    /// <summary>A box of the given size centred at <paramref name="centre"/>, its axes turned by <paramref name="rotation"/>;
    /// <paramref name="bevel"/> cuts its vertical edges at 45 degrees.</summary>
    public void Box(Vector3 centre, Vector3 size, Quaternion rotation, float bevel, int material) {

        Vector3 half = 0.5f * size;
        float b = Mathf.Min(bevel, 0.45f * Mathf.Min(size.x, size.z));

        // The outline in the box's XZ plane, counter-clockwise from above, beveled at its corners.
        Vector2[] outline = {

            new Vector2(half.x, -half.z + b), new Vector2(half.x, half.z - b), new Vector2(half.x - b, half.z), new Vector2(-half.x + b, half.z),
            new Vector2(-half.x, half.z - b), new Vector2(-half.x, -half.z + b), new Vector2(-half.x + b, -half.z), new Vector2(half.x - b, -half.z),

        };

        List<int> triangles = _triangles[material];

        for (int i = 0; i < outline.Length; i++) {

            Vector2 p = outline[i];
            Vector2 q = outline[(i + 1) % outline.Length];
            Vector2 edge = (q - p).normalized;
            Vector3 normal = rotation * new Vector3(edge.y, 0.0f, -edge.x);
            int start = _positions.Count;

            Add(new Vector3(p.x, -half.y, p.y), normal, new Vector2(0.0f, 0.0f));
            Add(new Vector3(q.x, -half.y, q.y), normal, new Vector2(Vector2.Distance(p, q), 0.0f));
            Add(new Vector3(q.x, half.y, q.y), normal, new Vector2(Vector2.Distance(p, q), size.y));
            Add(new Vector3(p.x, half.y, p.y), normal, new Vector2(0.0f, size.y));

            triangles.AddRange(new[] { start, start + 3, start + 2, start, start + 2, start + 1 });

        }

        foreach (bool top in new[] { false, true }) {

            Vector3 normal = rotation * (top ? Vector3.up : Vector3.down);
            int start = _positions.Count;

            foreach (Vector2 p in outline) {

                Add(new Vector3(p.x, top ? half.y : -half.y, p.y), normal, p);

            }

            for (int i = 1; i + 1 < outline.Length; i++) {

                triangles.AddRange(top ? new[] { start, start + i + 1, start + i } : new[] { start, start + i, start + i + 1 });

            }

        }

        void Add(Vector3 local, Vector3 normal, Vector2 uv) {

            _positions.Add(centre + rotation * local);
            _normals.Add(normal);
            _uvs.Add(uv);

        }

    }

    /// <summary>A flat band between two closed loops of as many points, lying in a plane and facing along
    /// <paramref name="normal"/>; UVs are the points' own X and Z in metres.</summary>
    public void Band(IReadOnlyList<Vector3> inner, IReadOnlyList<Vector3> outer, Vector3 normal, int material) {

        int first = _positions.Count;
        int count = inner.Count;

        for (int i = 0; i < count; i++) {

            _positions.Add(inner[i]);
            _positions.Add(outer[i]);
            _normals.Add(normal);
            _normals.Add(normal);
            _uvs.Add(new Vector2(inner[i].x, inner[i].z));
            _uvs.Add(new Vector2(outer[i].x, outer[i].z));

        }

        // Wound to face the normal, whichever way round the loops run.
        bool flip = Vector3.Dot(Vector3.Cross(outer[0] - inner[0], inner[1 % count] - inner[0]), normal) < 0.0f;
        List<int> triangles = _triangles[material];

        for (int i = 0; i < count; i++) {

            int a = first + 2 * i;
            int b = a + 1;
            int c = first + 2 * ((i + 1) % count);
            int d = c + 1;

            triangles.AddRange(flip ? new[] { a, c, b, b, c, d } : new[] { a, b, c, b, d, c });

        }

    }

    /// <summary>Copies what has been built so far turned and moved, as a part placed on another.</summary>
    public void Transform(int from, Vector3 offset, Quaternion rotation) {

        for (int i = from; i < _positions.Count; i++) {

            _positions[i] = offset + rotation * _positions[i];
            _normals[i] = rotation * _normals[i];

        }

    }

    public int VertexCount => _positions.Count;

    public Mesh Build(string name) {

        Mesh mesh = new Mesh { name = name, indexFormat = _positions.Count > 65_000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };

        mesh.SetVertices(_positions);
        mesh.SetNormals(_normals);
        mesh.SetUVs(0, _uvs);
        mesh.subMeshCount = _triangles.Length;

        for (int i = 0; i < _triangles.Length; i++) {

            mesh.SetTriangles(_triangles[i], i);

        }

        mesh.RecalculateTangents();
        mesh.RecalculateBounds();

        return mesh;

    }

}
