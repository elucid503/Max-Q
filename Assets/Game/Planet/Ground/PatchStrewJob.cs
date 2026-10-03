using System;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Surface;

using Terrain = MaxQ.Sim.Surface.Terrain;

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace MaxQ.Game.Planet.Ground;

/// <summary>A patch build's second stage, on the levels that strew: a row of quads to each index, each quad taking a few
/// chances of a plant and of a rock at places hashed from where they lie on the planet, so every build of a patch strews
/// the same. A chance lands as often as the ground's cover and lie call for it: grass in meadows, trees in forest, stones
/// on steep and bare ground, and nothing in the water or on its beaches. Cratered ground grows nothing: pebbles and clods
/// lie everywhere, boulders and blocks gather on young craters' ejecta and on steep slopes. Each chance keeps a slot,
/// empty when nothing lands, so the assembly gathers them in the same order whichever worker placed them.</summary>
[BurstCompile]
internal struct PatchStrewJob : IJobParallelFor {

    // Levels below the finest: tufts or pebbles on the finest, trees TreeLevels up, groves out to GroveLevels. Trees and
    // rocks stand on the finest ground; tufts, pebbles and groves on their own patch.
    public const int TreeLevels = 3;
    private const int GroveLevels = 6;

    // Boulders strew with the trees; outcrops, or crater blocks, OutcropLevels up.
    private const int OutcropLevels = 5;

    // Chances a quad takes; tufts take all three, trees and groves two.
    public const int PlantChances = 3;
    public const int RockChances = 2;
    public const int PlantSlots = PlantChances * PatchJob.Quads * PatchJob.Quads;
    public const int RockSlots = RockChances * PatchJob.Quads * PatchJob.Quads;

    // Each plant is three float4s in the patch's scene axes, as the vegetation shaders read them: position in kilometres
    // from the patch centre and a random rank; the climate (aridity and warmth), a random number, and one for needles;
    // and its shape: a tuft's height as a share of full, or a tree's height and crown width in metres, and under a grove,
    // the ground's normal, octahedral. Each rock is three float4s: position and size in metres; orientation as a
    // quaternion; and a random number that picks its shape, the ground's vegetation and aridity (on cratered ground, how
    // far it is mare and how fresh), and one for an outcrop.
    public const int PlantLength = 3 * PlantSlots;
    public const int RockLength = 3 * RockSlots;

    [NativeDisableUnsafePtrRestriction]
    public Terrain Terrain;

    public int Face;
    public int Depth;
    public int MaxDepth;
    public int X;
    public int Y;

    [ReadOnly]
    public NativeArray<Vector3d> Grid;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<float4> Plants;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<float4> Rocks;

    private enum Kind {

        None,
        Tuft,
        Tree,
        Grove,
        Boulder,
        Outcrop,
        Pebble,

    }

    // Where a chance lands on the patch's mesh, the mesh's normal and how far it faces up there, and the ground's height
    // at the chance's footing.
    private readonly struct Place {

        public readonly Vector3d Direction;
        public readonly Vector3d Normal;
        public readonly double Height;
        public readonly double Upness;

        public Place(Vector3d direction, Vector3d normal, double height) {

            Direction = direction;
            Normal = normal;
            Height = height;
            Upness = Vector3d.Dot(normal, direction);

        }

    }

    /// <summary>Whether a patch at <paramref name="depth"/> strews anything.</summary>
    public static bool Strews(int depth, int maxDepth) => depth == maxDepth || (depth >= maxDepth - GroveLevels && depth <= maxDepth - TreeLevels);

    private static Kind PlantAt(int depth, int maxDepth, bool cratered) =>
        cratered ? Kind.None : depth == maxDepth ? Kind.Tuft : depth == maxDepth - TreeLevels ? Kind.Tree :
        depth >= maxDepth - GroveLevels && depth < maxDepth - TreeLevels ? Kind.Grove : Kind.None;

    private static Kind RockAt(int depth, int maxDepth, bool cratered) =>
        depth == maxDepth - TreeLevels ? Kind.Boulder : depth == maxDepth - OutcropLevels ? Kind.Outcrop : cratered && depth == maxDepth ? Kind.Pebble : Kind.None;

    /// <summary>Plant chances a quad takes at <paramref name="depth"/>.</summary>
    public static int PlantsPerQuad(int depth, int maxDepth, bool cratered) => PlantAt(depth, maxDepth, cratered) switch {

        Kind.Tuft => 3,
        Kind.None => 0,
        _ => 2,

    };

    /// <summary>Rock chances a quad takes at <paramref name="depth"/>.</summary>
    public static int RocksPerQuad(int depth, int maxDepth, bool cratered) => RockAt(depth, maxDepth, cratered) switch {

        Kind.Boulder or Kind.Pebble => 2,
        Kind.Outcrop => 1,
        _ => 0,

    };

    public void Execute(int j) {

        Vector3d centre = PatchJob.CentreDirection(Face, Depth, X, Y) * Terrain.Radius;
        bool cratered = Terrain.IsCratered;
        Kind plant = PlantAt(Depth, MaxDepth, cratered);
        Kind rock = RockAt(Depth, MaxDepth, cratered);
        int plants = PlantsPerQuad(Depth, MaxDepth, cratered);
        int rocks = RocksPerQuad(Depth, MaxDepth, cratered);

        // Rocks are rare but for pebbles, so most chances end before anything is sampled.
        float rare = rock switch {

            Kind.Boulder => 0.35f,
            Kind.Pebble => 1.0f,
            _ => 0.1f,

        };

        for (int i = 0; i < PatchJob.Quads; i++) {

            int quad = j * PatchJob.Quads + i;

            for (int c = 0; c < plants; c++) {

                int slot = 3 * (quad * PlantChances + c);
                uint4 random = Hash(i, j, c, (int)plant);

                Plants[slot] = new float4(0.0f, 0.0f, 0.0f, -1.0f);

                if (Unit(random.x) < Chance(plant, i, j, random, out Place place, out Cover cover, out _)) {

                    StrewPlant(plant, slot, place, cover, random, centre);

                }

            }

            for (int c = 0; c < rocks; c++) {

                int slot = 3 * (quad * RockChances + c);
                uint4 random = Hash(i, j, c, (int)rock);

                Rocks[slot] = float4.zero;

                if (Unit(random.x) < rare && Unit(random.x) < Chance(rock, i, j, random, out Place place, out _, out float2 ground)) {

                    StrewRock(rock, slot, place, ground, random, centre);

                }

            }

        }

    }

    // Odds a chance lands in quad i, j; ground is what a rock carries (vegetation and aridity, or mare and freshness).
    private double Chance(Kind kind, int i, int j, uint4 random, out Place place, out Cover cover, out float2 ground) {

        bool footing = kind == Kind.Tree || kind == Kind.Boulder || kind == Kind.Outcrop;

        place = Locate(i, j, Unit(random.y), Unit(random.z), footing);
        cover = default;

        double slope = Math.Sqrt(Math.Max(1.0 - place.Upness * place.Upness, 0.0)) / Math.Max(place.Upness, 0.05);

        if (Terrain.IsCratered) {

            Terrain.HeightAt(place.Direction, PatchJob.Footprint(Terrain.Radius, MaxDepth), out double fresh);

            Terrain.RegolithAt(place.Direction, 0.0, out double maria, out _);

            ground = new float2((float)maria, (float)fresh);

            // Boulders and blocks favour fresh ejecta and steep walls; quads are half Terra's width, so odds are quartered.
            return kind switch {

                Kind.Pebble => 0.04 + 0.025 * fresh,
                Kind.Boulder => 0.004 + 0.1 * fresh + 0.075 * math.saturate((slope - 0.35) / 0.5),
                Kind.Outcrop => 0.1 * fresh * fresh + 0.025 * math.saturate((slope - 0.6) / 0.6),
                _ => 0.0,

            };

        }

        // The level the ground was held against where its height was found: the point's own level cell alone can be dry
        // where a neighbour's water covers the ground, and trees would stand in it up to the cell's straight edge.
        double level = Terrain.HeldWaterLevelAt(place.Direction, PatchJob.Footprint(Terrain.Radius, footing ? MaxDepth : Depth));

        ground = float2.zero;

        if (!double.IsNaN(level) && place.Height < level) {

            return 0.0;

        }

        cover = Cover.At(Terrain, place.Direction, place.Height, 0.0);

        // Nothing grows or lies on the beach the ground's materials lay above the water (see Biome.hlsl), as wide as the
        // climate makes it.
        if (!double.IsNaN(level)) {

            double width = Cover.BeachWidth(level, cover.Arid, Cover.WarmthAt(place.Direction, place.Height));

            if (place.Height < level + Math.Min(math.lerp(1.0, 0.3, math.saturate(level / 5.0)), slope * width)) {

                return 0.0;

            }

        }

        ground = new float2(cover.Vegetation, cover.Arid);

        // Plants hold to about 45 degrees on vegetated ground and 35 on bare, as the ground's materials give way to rock.
        double steep = math.saturate((math.lerp(0.8, 0.7, cover.Vegetation) - place.Upness) / 0.1);
        double open = (1.0 - steep) * (1.0 - cover.Snow);

        // Nothing grows or lies on a levelled site.
        return (1.0 - Terrain.Clearing(place.Direction)) * kind switch {

            Kind.Tuft => math.saturate(1.2 * cover.Vegetation * (1.0 - cover.Forest) + 0.25 * (1.0 - cover.Vegetation) * (1.0 - cover.Arid) +
                0.15 * cover.Vegetation * cover.Forest) * open,
            Kind.Tree or Kind.Grove => 1.15 * cover.Vegetation * cover.Forest * open,
            Kind.Boulder => math.lerp(0.01 + 0.04 * (1.0 - cover.Vegetation), 0.35, math.saturate((slope - 0.3) / 0.6)),
            Kind.Outcrop => 0.1 * math.saturate((slope - 0.6) / 0.6),
            _ => 0.0,

        };

    }

    // The point s, u of the way across quad i, j on the mesh, which splits each quad into (a, b, c) and (b, d, c), and the
    // ground's height there: the mesh's own, or with a true footing, the ground's at its finest.
    private Place Locate(int i, int j, double s, double u, bool footing) {

        const int size = PatchSamples.GridSize;

        int a = (j + PatchJob.HorizonReach) * size + i + PatchJob.HorizonReach;
        Vector3d pa = Grid[a];
        Vector3d pb = Grid[a + 1];
        Vector3d pc = Grid[a + size];
        Vector3d pd = Grid[a + size + 1];
        bool first = s + u < 1.0;
        Vector3d p = first ? pa + (pb - pa) * s + (pc - pa) * u : pd + (pc - pd) * (1.0 - s) + (pb - pd) * (1.0 - u);
        Vector3d normal = first ? Vector3d.Cross(pb - pa, pc - pa).Normalized : Vector3d.Cross(pc - pd, pb - pd).Normalized;
        Vector3d direction = p.Normalized;
        double height = footing ? Terrain.HeightAt(direction, PatchJob.Footprint(Terrain.Radius, MaxDepth)) : p.Length - Terrain.Radius;

        return new Place(direction, Vector3d.Dot(normal, p) < 0.0 ? -normal : normal, height);

    }

    // Grass, taller in meadows and where summers are long; trees, conifers taking over as the year cools, as tall and
    // wide as a closed stand leaves room for.
    private void StrewPlant(Kind kind, int slot, Place place, Cover cover, uint4 random, Vector3d centre) {

        uint4 more = Pcg(random);
        float3 position = PatchJob.Scene(place.Direction * (Terrain.Radius + place.Height) - centre);
        float needles = Unit(more.x) < math.saturate((0.6f - cover.Warmth) / 0.3f) ? 1.0f : 0.0f;
        float4 climate = new float4(cover.Arid, cover.Warmth, Unit(more.y), needles);
        float4 shape;

        if (kind == Kind.Tuft) {

            float tall = (0.6f + 0.4f * cover.Vegetation) * math.lerp(0.5f, 1.0f, math.saturate(cover.Warmth / 0.4f));

            shape = new float4(tall, cover.Forest, 0.0f, 0.0f);

        } else {

            float height = (needles > 0.0f ? math.lerp(14.0f, 30.0f, Unit(more.z)) : math.lerp(10.0f, 24.0f, Unit(more.z))) * (0.75f + 0.25f * cover.Forest);
            float crown = height * (needles > 0.0f ? math.lerp(0.12f, 0.17f, Unit(more.w)) : math.lerp(0.17f, 0.25f, Unit(more.w)));
            float2 normal = kind == Kind.Grove ? PatchJob.Octahedral(PatchJob.Scene(place.Normal)) : float2.zero;

            shape = new float4(height, crown, normal);

        }

        Plants[slot] = new float4(position, Unit(random.w));
        Plants[slot + 1] = climate;
        Plants[slot + 2] = shape;

    }

    // Mostly small stones, now and then a big one. Outcrops lean with the slope and sink into it, so the downhill side
    // stays buried; blocks thrown out of craters are smaller.
    private void StrewRock(Kind kind, int slot, Place place, float2 ground, uint4 random, Vector3d centre) {

        bool outcrop = kind == Kind.Outcrop;
        uint4 more = Pcg(random);
        float4 unit = new float4(Unit(more.x), Unit(more.y), Unit(more.z), Unit(more.w));
        double smallest = kind switch {

            Kind.Outcrop => Terrain.IsCratered ? 3.0 : 5.0,
            Kind.Pebble => 0.03,
            _ => 0.3,

        };
        double largest = kind switch {

            Kind.Outcrop => Terrain.IsCratered ? 12.0 : 20.0,
            Kind.Pebble => 0.25,
            _ => 3.0,

        };
        double size = smallest * Math.Pow(largest / smallest, unit.w * unit.w * unit.w);
        double slope = Math.Sqrt(Math.Max(1.0 - place.Upness * place.Upness, 0.0)) / Math.Max(place.Upness, 0.05);
        double height = place.Height - (outcrop ? size * 0.25 * Math.Min(slope, 1.5) : 0.0);
        float3 up = math.normalize(PatchJob.Scene(place.Direction));

        if (outcrop) {

            up = math.normalize(math.lerp(up, math.normalize(PatchJob.Scene(place.Normal)), 0.7f));

        }

        float3 side = math.normalize(math.cross(up, math.abs(up.y) < 0.9f ? new float3(0.0f, 1.0f, 0.0f) : new float3(1.0f, 0.0f, 0.0f)));
        float yaw = unit.x * 2.0f * math.PI;
        float3 forward = math.cross(side, up) * math.cos(yaw) + side * math.sin(yaw);
        quaternion tilt = quaternion.Euler((unit.y - 0.5f) * 0.5f, 0.0f, (unit.z - 0.5f) * 0.5f);

        Rocks[slot] = new float4(PatchJob.Scene(place.Direction * (Terrain.Radius + height) - centre), (float)size);
        Rocks[slot + 1] = math.mul(quaternion.LookRotation(forward, up), tilt).value;
        Rocks[slot + 2] = new float4(Unit(random.w), ground, outcrop ? 1.0f : 0.0f);

    }

    // Four independent random words for a chance, from its quad's place on the planet, its number and its kind.
    private uint4 Hash(int i, int j, int chance, int kind) =>
        Pcg(new uint4((uint)Face, (uint)(X * PatchJob.Quads + i), (uint)(Y * PatchJob.Quads + j), (uint)(chance + 8 * kind)));

    // Jarzynski and Olano's pcg4d: every output word depends on every input word, where Unity's math.hash is too nearly
    // linear and neighbouring chances would line up.
    private static uint4 Pcg(uint4 v) {

        v = v * 1_664_525u + 1_013_904_223u;
        v.x += v.y * v.w;
        v.y += v.z * v.x;
        v.z += v.x * v.y;
        v.w += v.y * v.z;
        v ^= v >> 16;
        v.x += v.y * v.w;
        v.y += v.z * v.x;
        v.z += v.x * v.y;
        v.w += v.y * v.z;

        return v;

    }

    private static float Unit(uint bits) => (bits >> 8) / 16_777_216.0f;

}
