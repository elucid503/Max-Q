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
/// on steep and bare ground, and nothing in the water or on its beaches. Each chance keeps a slot, empty when nothing
/// lands, so the assembly gathers them in the same order whichever worker placed them.</summary>
[BurstCompile]
internal struct PatchStrewJob : IJobParallelFor {

    // Grass tufts cover the finest patches. Trees stand on the patches of TreeDepth, a few hundred metres across; past
    // them, forest is groves, each standing for the trees of its cell, on the coarser levels' patches out to GroveDepth.
    // Tufts and groves stand on their own patch's triangles, so on exactly the ground drawn with them; trees and rocks
    // outlast their patch into finer ground, so they stand on the ground at its finest.
    public const int TuftDepth = GroundView.MaxDepth;
    public const int TreeDepth = 13;
    public const int GroveDepth = 10;

    // Boulders strew with the trees; outcrops of bedrock with the patches of OutcropDepth, a few kilometres across.
    public const int OutcropDepth = 11;

    // Chances a quad takes; tufts take all three, trees and groves two.
    public const int PlantChances = 3;
    public const int RockChances = 2;
    public const int PlantSlots = PlantChances * PatchJob.Quads * PatchJob.Quads;
    public const int RockSlots = RockChances * PatchJob.Quads * PatchJob.Quads;

    // Each plant is three float4s in the patch's scene axes, as the vegetation shaders read them: position in kilometres
    // from the patch centre and a random rank; the climate (aridity and warmth), a random number, and one for needles;
    // and its shape: a tuft's height as a share of full, or a tree's height and crown width in metres, and under a grove,
    // the ground's normal, octahedral. Each rock is three float4s: position and size in metres; orientation as a
    // quaternion; and a random number that picks its shape, the ground's vegetation and aridity, and one for an outcrop.
    public const int PlantLength = 3 * PlantSlots;
    public const int RockLength = 3 * RockSlots;

    [NativeDisableUnsafePtrRestriction]
    public Terrain Terrain;

    public int Face;
    public int Depth;
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

    public static bool Strews(int depth) => depth == TuftDepth || (depth >= GroveDepth && depth <= TreeDepth);

    private static Kind PlantAt(int depth) => depth == TuftDepth ? Kind.Tuft : depth == TreeDepth ? Kind.Tree : depth >= GroveDepth && depth < TreeDepth ? Kind.Grove : Kind.None;

    private static Kind RockAt(int depth) => depth == TreeDepth ? Kind.Boulder : depth == OutcropDepth ? Kind.Outcrop : Kind.None;

    /// <summary>Plant chances a quad takes at <paramref name="depth"/>.</summary>
    public static int PlantsPerQuad(int depth) => PlantAt(depth) == Kind.Tuft ? 3 : PlantAt(depth) == Kind.None ? 0 : 2;

    /// <summary>Rock chances a quad takes at <paramref name="depth"/>.</summary>
    public static int RocksPerQuad(int depth) => RockAt(depth) == Kind.Boulder ? 2 : RockAt(depth) == Kind.Outcrop ? 1 : 0;

    public void Execute(int j) {

        Vector3d centre = PatchJob.CentreDirection(Face, Depth, X, Y) * Terrain.Radius;
        Kind plant = PlantAt(Depth);
        Kind rock = RockAt(Depth);

        for (int i = 0; i < PatchJob.Quads; i++) {

            int quad = j * PatchJob.Quads + i;

            for (int c = 0; c < PlantsPerQuad(Depth); c++) {

                int slot = 3 * (quad * PlantChances + c);
                uint4 random = Hash(i, j, c, (int)plant);

                Plants[slot] = new float4(0.0f, 0.0f, 0.0f, -1.0f);

                if (Unit(random.x) < Chance(plant, i, j, random, out Place place, out Cover cover)) {

                    StrewPlant(plant, slot, place, cover, random, centre);

                }

            }

            for (int c = 0; c < RocksPerQuad(Depth); c++) {

                int slot = 3 * (quad * RockChances + c);
                uint4 random = Hash(i, j, c, (int)rock);

                Rocks[slot] = float4.zero;

                // Rocks are rare, so most chances end before anything is sampled.
                if (Unit(random.x) < (rock == Kind.Boulder ? 0.35f : 0.1f) && Unit(random.x) < Chance(rock, i, j, random, out Place place, out Cover cover)) {

                    StrewRock(rock, slot, place, cover, random, centre);

                }

            }

        }

    }

    // How likely a chance of a kind is to land where it falls in quad i, j, and the place and cover there.
    private double Chance(Kind kind, int i, int j, uint4 random, out Place place, out Cover cover) {

        place = Locate(i, j, Unit(random.y), Unit(random.z), kind == Kind.Tree || kind == Kind.Boulder || kind == Kind.Outcrop);
        cover = default;

        double level = Terrain.WaterLevelAt(place.Direction, PatchJob.Footprint(Terrain.Radius, Depth));

        // Nothing grows or lies in the water or on the beach the ground's materials lay above it (see Biome.hlsl).
        if (!double.IsNaN(level) && place.Height < level + math.lerp(1.0, 0.3, math.saturate(level / 5.0))) {

            return 0.0;

        }

        cover = Cover.At(Terrain, place.Direction, place.Height, 0.0);

        // Plants hold to about 45 degrees on vegetated ground and 35 on bare, as the ground's materials give way to rock.
        double steep = math.saturate((math.lerp(0.8, 0.7, cover.Vegetation) - place.Upness) / 0.1);
        double slope = Math.Sqrt(Math.Max(1.0 - place.Upness * place.Upness, 0.0)) / Math.Max(place.Upness, 0.05);
        double open = (1.0 - steep) * (1.0 - cover.Snow);

        return kind switch {

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
        double height = footing ? Terrain.HeightAt(direction, PatchJob.Footprint(Terrain.Radius, GroundView.MaxDepth)) : p.Length - Terrain.Radius;

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

            shape = new float4(tall, 0.0f, 0.0f, 0.0f);

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
    // stays buried.
    private void StrewRock(Kind kind, int slot, Place place, Cover cover, uint4 random, Vector3d centre) {

        bool outcrop = kind == Kind.Outcrop;
        uint4 more = Pcg(random);
        float4 unit = new float4(Unit(more.x), Unit(more.y), Unit(more.z), Unit(more.w));
        double smallest = outcrop ? 5.0 : 0.3;
        double largest = outcrop ? 20.0 : 3.0;
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
        Rocks[slot + 2] = new float4(Unit(random.w), cover.Vegetation, cover.Arid, outcrop ? 1.0f : 0.0f);

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
