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

    // A cluster's heat shield: edges round each engine's hole, and the boot closing the hole round the engine, rising this
    // far to this share of the hole's radius.
    private const int CellSegments = 96;
    private const float BootRise = 0.35f;
    private const float BootWaist = 0.55f;

    /// <summary>The tank; with an engine hung below it, the thrust structure's shroud round the engine's body, or round a
    /// cluster the engine section's skirt and heat shield.</summary>
    public static Mesh Tank(Tank tank, EngineEntry entry, Engine engine) {

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

        // The forward dome, inside whatever stands above; it shows where an open interstage is left behind.
        List<Vector2> forward = new List<Vector2>();

        for (int i = 0; i <= DomeRings; i++) {

            float t = 0.5f * Mathf.PI * i / DomeRings;

            forward.Add(new Vector2(radius * Mathf.Cos(t), top + (float)tank.DomeDepth * Mathf.Sin(t)));

        }

        mesh.Lathe(forward, Segments, Dark);

        if (engine is { Count: > 1 }) {

            EngineSection(mesh, engine, (float)entry.mountRadius, (float)entry.mountDrop, radius, depth);

        } else if (engine != null) {

            ThrustStructure(mesh, (float)entry.mountRadius, (float)entry.mountDrop, radius, depth);

        }

        return mesh.Build(tank.Name);

    }

    /// <summary>A skirt's shell in a finish, stringered outside and dark within, as an open interstage shows.</summary>
    public static Mesh Skirt(Skirt skirt, int finish) {

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

        mesh.Lathe(new[] { shell[0], shell[1], shell[2], shell[3] }, Segments, finish);
        mesh.Lathe(new[] { shell[3], frameTop[0] }, Stringers * 4, finish, StringerBump);
        mesh.Lathe(frameTop, Segments, finish);

        // Listed top to bottom, so it faces in.
        mesh.Lathe(new[] { new Vector2(top - FrameDepth, length), new Vector2(bottom - FrameDepth, 0.0f) }, Segments, Dark);

        return mesh.Build(skirt.Name);

    }

    /// <summary>The separation ring: a bare band split by its joint, with a dark deck closing the stage below it unless it is
    /// <paramref name="open"/> for an engine nested through it.</summary>
    public static Mesh Decoupler(Decoupler ring, bool open) {

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

        if (!open) {

            mesh.Disc(0.0f, radius, joint, true, Segments / 4, Dark);

        }

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

    // Under a cluster, as on Falcon 9's octaweb: the stage's own skirt carried down past the gimbals, closed by a dark heat
    // shield with a hole round each engine, each hole closed by a boot gathering in towards the engine. The shield is cut into
    // a cell per engine, the points nearer it than any other, so each cell is a band from its hole out to its edge.
    private static void EngineSection(MeshBuilder mesh, Engine engine, float mount, float drop, float radius, float depth) {

        mesh.Lathe(new[] { new Vector2(radius, -drop), new Vector2(radius, depth) }, Segments, Paint);

        Vector2[] sites = new Vector2[engine.Count];

        for (int i = 0; i < sites.Length; i++) {

            sites[i] = new Vector2((float)engine.Nozzles[i].X, (float)engine.Nozzles[i].Y);

        }

        for (int k = 0; k < sites.Length; k++) {

            Vector2 site = sites[k];
            List<Vector3> hole = new List<Vector3>();
            List<Vector3> edge = new List<Vector3>();

            for (int s = 0; s < CellSegments; s++) {

                float angle = 2.0f * Mathf.PI * s / CellSegments;
                Vector2 ray = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                float along = Vector2.Dot(site, ray);
                float reach = -along + Mathf.Sqrt(along * along - site.sqrMagnitude + radius * radius);

                for (int j = 0; j < sites.Length; j++) {

                    Vector2 apart = sites[j] - site;
                    float toward = Vector2.Dot(ray, apart);

                    if (j != k && toward > 1e-6f) {

                        reach = Mathf.Min(reach, 0.5f * apart.sqrMagnitude / toward);

                    }

                }

                // Sim X and Y are scene X and Z.
                hole.Add(new Vector3(site.x + mount * ray.x, -drop, site.y + mount * ray.y));
                edge.Add(new Vector3(site.x + reach * ray.x, -drop, site.y + reach * ray.y));

            }

            mesh.Band(hole, edge, Vector3.down, Dark);

            int boot = mesh.VertexCount;

            // Top to bottom, so it faces down into the hole.
            mesh.Lathe(new[] { new Vector2(BootWaist * mount, BootRise - drop), new Vector2(mount, -drop) }, Segments / 4, Dark);
            mesh.Transform(boot, new Vector3(site.x, 0.0f, site.y), Quaternion.identity);

        }

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
