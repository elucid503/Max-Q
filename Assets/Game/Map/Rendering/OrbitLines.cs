using System;
using System.Collections.Generic;

using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Orbits;

using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Map.Rendering;

/// <summary>Draws trajectory patches as screen-space anti-aliased lines (see MapLine.shader).</summary>
public sealed class OrbitLines {

    private const int SegmentsPerPatch = 384;

    private readonly Mesh _mesh;

    private readonly List<Vector3> _positions = new List<Vector3>();
    private readonly List<Vector3> _neighbours = new List<Vector3>();
    private readonly List<Vector2> _sides = new List<Vector2>();
    private readonly List<Color> _colors = new List<Color>();
    private readonly List<int> _indices = new List<int>();

    private readonly Vector3[] _points = new Vector3[SegmentsPerPatch + 1];

    public OrbitLines(string name, Material material) {

        _mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
        _mesh.MarkDynamic();

        GameObject go = new GameObject(name);
        go.AddComponent<MeshFilter>().sharedMesh = _mesh;

        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;

    }

    public void Draw(IReadOnlyList<Patch> patches, double now, Func<int, Color> colorOf) {

        _positions.Clear();
        _neighbours.Clear();
        _sides.Clear();
        _colors.Clear();
        _indices.Clear();

        for (int i = 0; i < patches.Count; i++) {

            AddPatch(patches[i], now, colorOf(i));

        }

        _mesh.Clear();
        _mesh.SetVertices(_positions);
        _mesh.SetNormals(_neighbours);
        _mesh.SetUVs(0, _sides);
        _mesh.SetColors(_colors);
        _mesh.SetTriangles(_indices, 0, false);

        // Lines circle the whole system; never let the camera cull them.
        _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e9f);

    }

    /// <summary>The body's position when the patch is drawn around it: now, or when the vessel will arrive.</summary>
    public static Vector3d FrameOrigin(Patch patch, double now) => patch.Body.PositionAt(Math.Max(now, patch.StartTime));

    private void AddPatch(Patch patch, double now, Color color) {

        if (color.a <= 0.0f) {

            return;

        }

        Orbit orbit = patch.Orbit;
        (double from, double to) = AnomalyRange(patch, orbit);
        Vector3d origin = FrameOrigin(patch, now);

        for (int i = 0; i <= SegmentsPerPatch; i++) {

            double nu = from + (to - from) * i / SegmentsPerPatch;

            _points[i] = MapSpace.ToScene(origin + orbit.PositionAtTrueAnomaly(nu));

        }

        for (int i = 0; i < SegmentsPerPatch; i++) {

            Vector3 a = _points[i];
            Vector3 b = _points[i + 1];
            int start = _positions.Count;

            // The far end points back along the segment; its direction sign keeps the offset on the same side.
            AddVertex(a, b, -1.0f, 1.0f, color);
            AddVertex(a, b, 1.0f, 1.0f, color);
            AddVertex(b, a, 1.0f, -1.0f, color);
            AddVertex(b, a, -1.0f, -1.0f, color);

            _indices.Add(start);
            _indices.Add(start + 1);
            _indices.Add(start + 2);
            _indices.Add(start);
            _indices.Add(start + 2);
            _indices.Add(start + 3);

        }

    }

    private void AddVertex(Vector3 position, Vector3 neighbour, float side, float direction, Color color) {

        _positions.Add(position);
        _neighbours.Add(neighbour);
        _sides.Add(new Vector2(side, direction));
        _colors.Add(color);

    }

    private static (double From, double To) AnomalyRange(Patch patch, Orbit orbit) {

        double from = orbit.TrueAnomalyAt(patch.StartTime);
        double span = patch.EndTime - patch.StartTime;

        if (orbit.IsClosed && span >= orbit.Period) {

            return (from, from + 2.0 * Math.PI);

        }

        if (!double.IsInfinity(patch.EndTime)) {

            double to = orbit.TrueAnomalyAt(patch.EndTime);

            while (to <= from) {

                to += 2.0 * Math.PI;

            }

            return (from, to);

        }

        // An open conic about the root: stop at twice the widest moon orbit.
        double reach = 0.0;

        foreach (CelestialBody child in patch.Body.Children) {

            reach = Math.Max(reach, 2.0 * child.Orbit.ApoapsisRadius);

        }

        double limit = orbit.TrueAnomalyLimit * 0.999;
        double cos = (orbit.SemiLatusRectum / reach - 1.0) / orbit.Eccentricity;

        double end = Math.Abs(cos) <= 1.0 ? Math.Min(Math.Acos(cos), limit) : limit;

        return (from, Math.Max(from, end));

    }

}
