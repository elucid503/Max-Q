using System;
using System.Collections.Generic;

using MaxQ.Game.Vessels.Craft;
using MaxQ.Sim.Vessels.Parts;
using MaxQ.Sim.Vessels.Propulsion;

using UnityEngine;

namespace MaxQ.Game.Vessels.Hull;

/// <summary>Meshes for the procedural parts, in metres from each part's bottom node, the stack's scene axes. Submeshes are
/// the hull's three finishes, in the order of <see cref="Finishes"/>.</summary>
public static class HullMesh {

    public const int Paint = 0;
    public const int Metal = 1;
    public const int Dark = 2;
    public const int Finishes = 3;

    // Edges round the stack: a circumference of 11.6 m in 4 cm steps keeps silhouettes round at the chase distance.
    private const int Segments = 288;
    private const int DomeRings = 24;

    // Weld lands stand proud of the painted barrel; a land every barrel section, at most this far apart.
    private const float WeldSpacing = 1.8f;
    private const float WeldWidth = 0.05f;
    private const float WeldHeight = 0.003f;

    // The raceway carrying cables down the tank, and where round the stack it runs.
    private const float RacewayWidth = 0.16f;
    private const float RacewayDepth = 0.07f;
    private const float RacewayAngle = 0.5f * Mathf.PI;

    // Skirts: hat stringers round the outside and a frame ring at each end.
    private const int Stringers = 96;
    private const float StringerHeight = 0.012f;
    private const float FrameHeight = 0.06f;
    private const float FrameDepth = 0.01f;

    // The thrust structure's shroud: a thin shell, open underneath.
    private const float ShroudThickness = 0.04f;

    /// <summary>The tank; with an engine hung below it, the thrust structure's shroud round the engine's body.</summary>
    public static Mesh Tank(Tank tank, EngineEntry engine) {

        MeshBuilder mesh = new MeshBuilder(Finishes);
        float radius = (float)tank.Radius;
        float depth = (float)tank.AftDomeDepth;
        float top = (float)tank.Height;

        // The aft dome, apex to barrel, painted with the rest where it shows below the stage; a flat end is a dark bulkhead.
        if (depth > 0.0f) {

            List<Vector2> dome = new List<Vector2>();

            for (int i = 0; i <= DomeRings; i++) {

                float t = 0.5f * Mathf.PI * i / DomeRings;

                dome.Add(new Vector2(radius * Mathf.Sin(t), depth * (1.0f - Mathf.Cos(t))));

            }

            mesh.Lathe(dome, Segments, Paint);

        } else {

            mesh.Disc(0.0f, radius, 0.0f, false, Segments / 4, Dark);

        }

        List<Vector2> barrel = new List<Vector2> { new Vector2(radius, depth) };
        int sections = Mathf.Max(1, Mathf.CeilToInt((float)tank.BarrelLength / WeldSpacing));

        // Lands between sections; the barrel's ends are welded under the dome joint and the skirt.
        for (int i = 1; i < sections; i++) {

            AddLand(barrel, radius, Mathf.Lerp(depth, top, (float)i / sections), depth, top);

        }

        barrel.Add(new Vector2(radius, top));
        mesh.Lathe(barrel, Segments, Paint);

        Raceway(mesh, radius, depth + 0.1f, top);

        if (engine != null) {

            ThrustStructure(mesh, (float)engine.mountRadius, (float)engine.mountDrop, radius, depth);

        }

        return mesh.Build(tank.Name);

    }

    public static Mesh Skirt(Skirt skirt) {

        MeshBuilder mesh = new MeshBuilder(Finishes);
        float bottom = (float)skirt.BottomRadius;
        float top = (float)skirt.TopRadius;
        float length = (float)skirt.Length;

        List<Vector2> shell = new List<Vector2> {

            new Vector2(bottom, 0.0f),
            new Vector2(bottom + FrameDepth, 0.0f),
            new Vector2(bottom + FrameDepth, FrameHeight),
            new Vector2(Mathf.Lerp(bottom, top, FrameHeight / length), FrameHeight),

        };

        List<Vector2> frameTop = new List<Vector2> {

            new Vector2(Mathf.Lerp(bottom, top, 1.0f - FrameHeight / length), length - FrameHeight),
            new Vector2(top + FrameDepth, length - FrameHeight),
            new Vector2(top + FrameDepth, length),
            new Vector2(top, length),

        };

        mesh.Lathe(new[] { shell[0], shell[1], shell[2], shell[3] }, Segments, Paint);
        mesh.Lathe(new[] { shell[3], frameTop[0] }, Stringers * 4, Paint, StringerBump);
        mesh.Lathe(frameTop, Segments, Paint);

        return mesh.Build(skirt.Name);

    }

    /// <summary>The separation ring: a bare band split by its joint, with a dark deck closing the stage below it.</summary>
    public static Mesh Decoupler(Decoupler ring) {

        MeshBuilder mesh = new MeshBuilder(Finishes);
        float radius = (float)ring.Radius;
        float length = (float)ring.Length;
        float joint = 0.5f * length;

        mesh.Lathe(new[] {

            new Vector2(radius, 0.0f),
            new Vector2(radius + 0.02f, 0.01f),
            new Vector2(radius + 0.02f, joint - 0.008f),
            new Vector2(radius + 0.008f, joint - 0.004f),
            new Vector2(radius + 0.008f, joint + 0.004f),
            new Vector2(radius + 0.02f, joint + 0.008f),
            new Vector2(radius + 0.02f, length - 0.01f),
            new Vector2(radius, length),

        }, Segments, Metal);

        mesh.Disc(0.0f, radius, joint, true, Segments / 4, Dark);

        return mesh.Build(ring.Name);

    }

    /// <summary>One pod of a ring, in its own frame turned into the stack's: housing on the skin, a bell per port.</summary>
    public static Mesh Pods(RcsBlock block, PodEntry pod) {

        MeshBuilder mesh = new MeshBuilder(Finishes);

        for (int i = 0; i < block.Count; i++) {

            int first = mesh.VertexCount;
            Vector3 size = new Vector3((float)pod.housingDepth, (float)pod.housingHeight, (float)pod.housingWidth);

            // Pod axes (X out, Y round, Z up) are scene X, Z and Y.
            mesh.Box(new Vector3(0.5f * size.x - 0.02f, 0.0f, 0.0f), size, Quaternion.identity, 0.04f, Paint);

            foreach (PortEntry port in pod.ports) {

                Vector3 at = Scene(port.position);
                Vector3 along = Scene(port.direction).normalized;
                int bell = mesh.VertexCount;

                mesh.Lathe(BellOutside(pod), 24, Metal);
                mesh.Lathe(BellInside(pod), 24, Dark);
                // A port is its nozzle's exit; the bell stands back from it towards the housing.
                mesh.Transform(bell, at - along * (float)pod.bellLength, Quaternion.FromToRotation(Vector3.up, along));

            }

            float angle = (float)block.PodAngle(i);

            // About scene Y, a positive angle turns X towards -Z, so the pod angle (X towards body Y = scene Z) is negated.
            mesh.Transform(first, new Vector3((float)block.RingRadius * Mathf.Cos(angle), (float)block.Offset, (float)block.RingRadius * Mathf.Sin(angle)),
                Quaternion.AngleAxis(-angle * Mathf.Rad2Deg, Vector3.up));

        }

        return mesh.Build(block.Name);

    }

    /// <summary>A sim-axis vector in the stack's scene axes.</summary>
    public static Vector3 Scene(double[] v) => v is { Length: 3 } ? new Vector3((float)v[0], (float)v[2], (float)v[1]) : Vector3.zero;

    // A thin conical shell from round the engine's body, below the dome's apex, out to where it meets the dome tangentially
    // (a flat end's rim), so it seats without a step; open underneath, so the powerhead shows inside it.
    private static void ThrustStructure(MeshBuilder mesh, float mount, float drop, float radius, float depth) {

        // The dome is the quarter ellipse (R sin t, d (1 - cos t)); bisect for the t whose tangent passes the mount ring.
        float low = 0.0f;
        float high = 0.5f * Mathf.PI;

        for (int i = 0; i < 40 && depth > 0.0f; i++) {

            float t = 0.5f * (low + high);
            float side = (radius * Mathf.Sin(t) - mount) * depth * Mathf.Sin(t) - (depth * (1.0f - Mathf.Cos(t)) + drop) * radius * Mathf.Cos(t);

            if (side < 0.0f) {

                low = t;

            } else {

                high = t;

            }

        }

        float seat = depth > 0.0f ? 0.5f * (low + high) : high;

        Vector2 bottom = new Vector2(mount, -drop);
        Vector2 top = new Vector2(radius * Mathf.Sin(seat), depth * (1.0f - Mathf.Cos(seat)));
        Vector2 inset = new Vector2(ShroudThickness, 0.0f);

        // Outside bottom to top; inside top to bottom, so it faces in; the lip between them faces down.
        mesh.Lathe(new[] { bottom, top }, Segments, Dark);
        mesh.Lathe(new[] { top - inset, bottom - inset }, Segments, Dark);
        mesh.Disc(mount - ShroudThickness, mount, -drop, false, Segments, Dark);

    }

    // A weld land: a low step out and back over its width.
    private static void AddLand(List<Vector2> profile, float radius, float height, float bottom, float top) {

        float low = Mathf.Max(bottom, height - 0.5f * WeldWidth);
        float high = Mathf.Min(top, height + 0.5f * WeldWidth);

        profile.Add(new Vector2(radius, low));
        profile.Add(new Vector2(radius + WeldHeight, low + 0.25f * (high - low)));
        profile.Add(new Vector2(radius + WeldHeight, high - 0.25f * (high - low)));
        profile.Add(new Vector2(radius, high));

    }

    // Hat-section stringers: a flat-topped rise over half of each pitch.
    private static float StringerBump(float angle) {

        float phase = angle * Stringers / (2.0f * Mathf.PI);
        float x = phase - Mathf.Floor(phase);
        float hat = Mathf.Clamp01((0.25f - Mathf.Abs(x - 0.5f)) / 0.08f + 0.5f);

        return StringerHeight * hat;

    }

    // A cable tray down the barrel's side: a box tapered off at both ends by ramps.
    private static void Raceway(MeshBuilder mesh, float radius, float bottom, float top) {

        float length = top - bottom - 0.1f;
        Quaternion facing = Quaternion.AngleAxis(-RacewayAngle * Mathf.Rad2Deg, Vector3.up);
        Vector3 centre = facing * new Vector3(radius + 0.5f * RacewayDepth - 0.005f, bottom + 0.5f * length, 0.0f);

        mesh.Box(centre, new Vector3(RacewayDepth, length, RacewayWidth), facing, 0.015f, Paint);

    }

    // The outside closes over the lip to the inside at the exit.
    private static Vector2[] BellOutside(PodEntry pod) {

        List<Vector2> profile = new List<Vector2>(Bell(pod, 0.004f, false)) { new Vector2((float)pod.exitRadius, (float)pod.bellLength) };

        return profile.ToArray();

    }

    private static Vector2[] BellInside(PodEntry pod) => Bell(pod, 0.0f, true);

    // A contoured bell from throat to exit along +Y; the inside is listed exit to throat so it faces inwards.
    private static Vector2[] Bell(PodEntry pod, float wall, bool inside) {

        const int steps = 10;
        Vector2[] profile = new Vector2[steps + 1];

        for (int i = 0; i <= steps; i++) {

            float t = (float)i / steps;
            float radius = Mathf.Lerp((float)pod.throatRadius, (float)pod.exitRadius, Mathf.Sqrt(t)) + wall;

            profile[inside ? steps - i : i] = new Vector2(radius, t * (float)pod.bellLength);

        }

        return profile;

    }

}
