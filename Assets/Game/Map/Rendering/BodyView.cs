using System;
using System.Collections.Generic;

using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;

using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Map.Rendering;

/// <summary>A body drawn as a cube-sphere, one textured face per cube side, placed and spun each frame.</summary>
public sealed class BodyView {

    // Normal, right and up of each cube face, in the sim frame; the face textures must be baked the same way.
    private static readonly (Vector3d Normal, Vector3d Right, Vector3d Up)[] Faces = {

        (new Vector3d(1.0, 0.0, 0.0), new Vector3d(0.0, 1.0, 0.0), new Vector3d(0.0, 0.0, 1.0)),
        (new Vector3d(-1.0, 0.0, 0.0), new Vector3d(0.0, 0.0, 1.0), new Vector3d(0.0, 1.0, 0.0)),
        (new Vector3d(0.0, 1.0, 0.0), new Vector3d(0.0, 0.0, 1.0), new Vector3d(1.0, 0.0, 0.0)),
        (new Vector3d(0.0, -1.0, 0.0), new Vector3d(1.0, 0.0, 0.0), new Vector3d(0.0, 0.0, 1.0)),
        (new Vector3d(0.0, 0.0, 1.0), new Vector3d(1.0, 0.0, 0.0), new Vector3d(0.0, 1.0, 0.0)),
        (new Vector3d(0.0, 0.0, -1.0), new Vector3d(0.0, 1.0, 0.0), new Vector3d(1.0, 0.0, 0.0)),

    };

    private const int FaceResolution = 96;

    private readonly Transform _transform;

    public CelestialBody Body { get; }

    public BodyView(CelestialBody body, Texture2D[] faces, Material template, float smoothness) {

        Body = body;

        GameObject go = new GameObject(body.Name);
        _transform = go.transform;

        go.AddComponent<MeshFilter>().sharedMesh = BuildMesh((float)(body.Radius / MapSpace.MetresPerUnit));

        Material[] materials = new Material[6];

        for (int i = 0; i < 6; i++) {

            materials[i] = new Material(template) { name = $"{body.Name} {i}" };
            materials[i].SetTexture("_BaseMap", faces[i]);
            materials[i].SetFloat("_Smoothness", smoothness);

        }

        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = materials;
        renderer.shadowCastingMode = ShadowCastingMode.Off;

    }

    public void Draw(double time) {

        _transform.position = MapSpace.ToScene(Body.PositionAt(time));
        _transform.rotation = Quaternion.AngleAxis(-(float)(RotationAngle(time) * 180.0 / Math.PI), Vector3.up);

    }

    /// <summary>Eastward rotation of the body-fixed frame from the sim frame, in radians.</summary>
    private double RotationAngle(double time) {

        if (Body.Orbit != null && Math.Abs(Body.RotationPeriodSeconds - Body.Orbit.Period) < 1.0) {

            // Tidally locked: the prime meridian always faces the parent.
            Vector3d toParent = -Body.Orbit.StateAt(time).Position;

            return Math.Atan2(toParent.Y, toParent.X);

        }

        return 2.0 * Math.PI * (time / Body.RotationPeriodSeconds % 1.0);

    }

    private static Mesh BuildMesh(float radius) {

        int n = FaceResolution + 1;

        List<Vector3> vertices = new List<Vector3>(6 * n * n);
        List<Vector3> normals = new List<Vector3>(6 * n * n);
        List<Vector2> uvs = new List<Vector2>(6 * n * n);

        Mesh mesh = new Mesh { name = "CubeSphere", indexFormat = IndexFormat.UInt32, subMeshCount = 6 };
        List<int>[] triangles = new List<int>[6];

        for (int face = 0; face < 6; face++) {

            int start = vertices.Count;
            (Vector3d normal, Vector3d right, Vector3d up) = Faces[face];

            for (int row = 0; row < n; row++) {

                for (int col = 0; col < n; col++) {

                    double s = col * 2.0 / FaceResolution - 1.0;
                    double t = 1.0 - row * 2.0 / FaceResolution;

                    Vector3 direction = MapSpace.Direction((normal + right * s + up * t).Normalized);

                    vertices.Add(direction * radius);
                    normals.Add(direction);
                    uvs.Add(new Vector2((float)(0.5 * (s + 1.0)), (float)(0.5 * (t + 1.0))));

                }

            }

            triangles[face] = new List<int>(FaceResolution * FaceResolution * 6);

            for (int row = 0; row < FaceResolution; row++) {

                for (int col = 0; col < FaceResolution; col++) {

                    int a = start + row * n + col;
                    int b = a + 1;
                    int c = a + n;
                    int d = c + 1;

                    AddOutward(triangles[face], vertices, a, b, c);
                    AddOutward(triangles[face], vertices, b, d, c);

                }

            }

        }

        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);

        for (int face = 0; face < 6; face++) {

            mesh.SetTriangles(triangles[face], face);

        }

        mesh.RecalculateBounds();

        return mesh;

    }

    // Unity treats clockwise as front; the axis swap mirrors faces, so wind each triangle to face out.
    private static void AddOutward(List<int> triangles, List<Vector3> vertices, int a, int b, int c) {

        Vector3 facing = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);

        if (Vector3.Dot(facing, vertices[a]) > 0.0f) {

            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);

            return;

        }

        triangles.Add(a);
        triangles.Add(c);
        triangles.Add(b);

    }

}
