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

/// <summary>A patch build's second stage, on the levels that strew: places its rocks and the spots where trees may stand,
/// a row of quads to each index. Every chance keeps a slot, empty when nothing lands there, so the assembly gathers them
/// in the same order whichever worker placed them.</summary>
[BurstCompile]
internal struct PatchStrewJob : IJobParallelFor {

    // Slots for the most rock chances any level takes.
    public const int RockChances = 2 * PatchJob.Quads * PatchJob.Quads;

    // Metres of ground detail a tree's footing keeps; finer relief would not move a trunk.
    private const double TreeFootprint = 1.0;

    private readonly struct Strewing {

        public readonly int Chances;
        public readonly double FlatChance;
        public readonly double SteepChance;
        public readonly double SlopeStart;
        public readonly double SlopeFull;
        public readonly double Smallest;
        public readonly double Largest;
        public readonly bool Outcrops;

        public Strewing(int chances, double flatChance, double steepChance, double slopeStart, double slopeFull, double smallest, double largest, bool outcrops) {

            Chances = chances;
            FlatChance = flatChance;
            SteepChance = steepChance;
            SlopeStart = slopeStart;
            SlopeFull = slopeFull;
            Smallest = smallest;
            Largest = largest;
            Outcrops = outcrops;

        }

    }

    // Stones and boulders anywhere, gathering on steep ground; outcrops only where the ground is steep enough to be bare.
    private static readonly Strewing Boulders = new Strewing(2, 0.01, 0.35, 0.3, 0.9, 0.3, 3.0, false);
    private static readonly Strewing Outcrops = new Strewing(1, 0.0, 0.1, 0.6, 1.2, 5.0, 20.0, true);

    [NativeDisableUnsafePtrRestriction]
    public Terrain Terrain;

    public int Face;
    public int Depth;
    public int X;
    public int Y;

    [ReadOnly]
    public NativeArray<Vector3d> Grid;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<float4> Rocks;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<float4> Trees;

    public static bool Strews(int depth) => depth == PatchJob.RockDepth || depth == PatchJob.OutcropDepth || depth == PatchJob.TreeDepth;

    /// <summary>Rock slots a patch at <paramref name="depth"/> fills.</summary>
    public static int RockSlots(int depth) => depth == PatchJob.RockDepth ? Boulders.Chances * PatchJob.Quads * PatchJob.Quads :
        depth == PatchJob.OutcropDepth ? Outcrops.Chances * PatchJob.Quads * PatchJob.Quads : 0;

    public void Execute(int j) {

        double span = 2.0 / (1L << Depth);
        double a0 = X * span - 1.0;
        double b0 = Y * span - 1.0;
        Vector3d centre = PatchJob.CentreDirection(Face, Depth, X, Y) * Terrain.Radius;

        if (Depth == PatchJob.TreeDepth) {

            PlaceTrees(j, a0, b0, span, centre);

        }

        if (Depth == PatchJob.RockDepth) {

            PlaceRocks(j, a0, b0, span, centre, Boulders);

        } else if (Depth == PatchJob.OutcropDepth) {

            PlaceRocks(j, a0, b0, span, centre, Outcrops);

        }

    }

    // Where rocks lie: the same everywhere a patch of this level is built, as each chance hashes its place on the planet.
    // An empty slot has no size.
    private void PlaceRocks(int j, double a0, double b0, double span, Vector3d centre, Strewing strewing) {

        double finest = PatchJob.Footprint(Terrain.Radius, GroundView.MaxDepth);

        for (int i = 0; i < PatchJob.Quads; i++) {

            int v = (j + PatchJob.HorizonReach) * PatchSamples.GridSize + i + PatchJob.HorizonReach;
            Vector3d up = Grid[v].Normalized;
            Vector3d normal = Vector3d.Cross(Grid[v + 1] - Grid[v], Grid[v + PatchSamples.GridSize] - Grid[v]).Normalized;
            double cosine = Math.Abs(Vector3d.Dot(normal, up));
            double slope = Math.Sqrt(Math.Max(1.0 - cosine * cosine, 0.0)) / Math.Max(cosine, 0.05);
            double steepness = Math.Clamp((slope - strewing.SlopeStart) / (strewing.SlopeFull - strewing.SlopeStart), 0.0, 1.0);
            double chance = strewing.FlatChance + (strewing.SteepChance - strewing.FlatChance) * steepness;

            for (int c = 0; c < strewing.Chances; c++) {

                int slot = 3 * ((j * PatchJob.Quads + i) * strewing.Chances + c);
                uint4 hash = new uint4(math.hash(new int4(Face, X * PatchJob.Quads + i, Y * PatchJob.Quads + j, c + (strewing.Outcrops ? 16 : 0))), 0u, 0u, 0u);

                hash.y = math.hash(hash.xx + 0x68E31DA4u);
                hash.z = math.hash(hash.yy + 0xB5297A4Du);
                hash.w = math.hash(hash.zz + 0x1B56C4E9u);

                float4 unit = (float4)(hash >> 8) / 16_777_216.0f;

                Rocks[slot] = float4.zero;

                if (unit.x >= chance) {

                    continue;

                }

                Vector3d direction = CubeFace.Direction(Face, a0 + (i + unit.y) * span / PatchJob.Quads, b0 + (j + unit.z) * span / PatchJob.Quads);
                double height = Terrain.HeightAt(direction, finest);
                double level = Terrain.WaterLevelAt(direction);

                if (!double.IsNaN(level) && height < level + 0.5) {

                    continue;

                }

                // Mostly small ones, now and then a big one. Outcrops lean with the slope and sink into it, so the
                // downhill side stays buried.
                double size = strewing.Smallest * Math.Pow(strewing.Largest / strewing.Smallest, unit.w * unit.w * unit.w);
                float3 upScene = math.normalize(PatchJob.Scene(direction));

                if (strewing.Outcrops) {

                    upScene = math.normalize(math.lerp(upScene, math.normalize(PatchJob.Scene(normal * Math.Sign(Vector3d.Dot(normal, up)))), 0.7f));
                    height -= size * 0.25 * Math.Min(slope, 1.5);

                }

                float3 side = math.normalize(math.cross(upScene, math.abs(upScene.y) < 0.9f ? new float3(0.0f, 1.0f, 0.0f) : new float3(1.0f, 0.0f, 0.0f)));
                float yaw = unit.y * 1_000.0f;
                float3 forward = math.cross(side, upScene) * math.cos(yaw) + side * math.sin(yaw);
                quaternion tilt = quaternion.Euler((unit.z - 0.5f) * 0.5f, 0.0f, (unit.w - 0.5f) * 0.5f);

                Rocks[slot] = new float4(PatchJob.Scene(direction * (Terrain.Radius + height) - centre), (float)size);
                Rocks[slot + 1] = math.mul(quaternion.LookRotation(forward, upScene), tilt).value;
                Rocks[slot + 2] = new float4(math.hash(hash) / 4_294_967_296.0f, (i + unit.y) / PatchJob.Quads, (j + unit.z) / PatchJob.Quads, strewing.Outcrops ? 1.0f : 0.0f);

            }

        }

    }

    // Places where trees may stand, as the vegetation reads them (see PatchJob); an empty slot has a negative rank.
    private void PlaceTrees(int j, double a0, double b0, double span, Vector3d centre) {

        for (int i = 0; i < PatchJob.Quads; i++) {

            for (int t = 0; t < PatchJob.TreesPerQuad; t++) {

                int slot = 2 * ((j * PatchJob.Quads + i) * PatchJob.TreesPerQuad + t);
                uint hash = math.hash(new int4(Face, X * PatchJob.Quads + i, Y * PatchJob.Quads + j, t + 48));
                float s = (hash & 0xFFFFu) / 65_536.0f;
                float u = (hash >> 16) / 65_536.0f;
                Vector3d direction = CubeFace.Direction(Face, a0 + (i + s) * span / PatchJob.Quads, b0 + (j + u) * span / PatchJob.Quads);
                double height = Terrain.HeightAt(direction, TreeFootprint);
                double level = Terrain.WaterLevelAt(direction);

                if (!double.IsNaN(level) && height < level + 0.5) {

                    Trees[slot] = new float4(0.0f, 0.0f, 0.0f, -1.0f);

                    continue;

                }

                Trees[slot] = new float4(PatchJob.Scene(direction * (Terrain.Radius + height) - centre), math.hash(new uint2(hash, 11u)) / 4_294_967_296.0f);
                Trees[slot + 1] = new float4((i + s) / PatchJob.Quads, (j + u) / PatchJob.Quads, 0.0f, 0.0f);

            }

        }

    }

}
