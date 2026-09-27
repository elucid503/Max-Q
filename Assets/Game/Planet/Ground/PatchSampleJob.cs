using System;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Ocean;
using MaxQ.Sim.Surface;

using Terrain = MaxQ.Sim.Surface.Terrain;

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace MaxQ.Game.Planet.Ground;

/// <summary>A patch build's first stage: its twenty-odd thousand terrain samples, a row to each index so the workers share
/// them and none is held long. Rows are the detail texture's, then the horizon grid's, whose middle is the vertex grid,
/// then every other row of the parent's posts, then the water detail's.</summary>
[BurstCompile]
internal struct PatchSampleJob : IJobParallelFor {

    public const int Rows = PatchSamples.FineSize + PatchSamples.GridSize + (PatchJob.Vertices + 1) / 2 + PatchJob.WaterTexels;

    // Real metres of shore distance past which the ground holds no sea, only rivers.
    private const double Inland = 10_000.0;

    [NativeDisableUnsafePtrRestriction]
    public Terrain Terrain;

    [NativeDisableUnsafePtrRestriction]
    public SeaState SeaState;

    public int Month;

    public int Face;
    public int Depth;
    public int X;
    public int Y;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<Vector3d> Grid;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<Vector3d> Directions;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<double> Heights;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<double> Levels;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<double> Shores;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<Vector3d> Coarse;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<Vector3d> Fine;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<double> Depths;

    [WriteOnly, NativeDisableParallelForRestriction]
    public NativeArray<ushort> WaterDetail;

    public void Execute(int row) {

        double span = 2.0 / (1L << Depth);
        double a0 = X * span - 1.0;
        double b0 = Y * span - 1.0;
        double footprint = PatchJob.Footprint(Terrain.Radius, Depth);

        if (row < PatchSamples.FineSize) {

            SampleFine(row, a0, b0, span, footprint);

            return;

        }

        row -= PatchSamples.FineSize;

        if (row < PatchSamples.GridSize) {

            SampleGrid(row, a0, b0, span, footprint);

            return;

        }

        row -= PatchSamples.GridSize;

        if (row < (PatchJob.Vertices + 1) / 2) {

            // A root has no parent; the assembly morphs it toward its own ground.
            if (Depth > 0) {

                SampleCoarse(2 * row, a0, b0, span, footprint);

            }

            return;

        }

        SampleWater(row - (PatchJob.Vertices + 1) / 2, a0, b0, span, footprint);

    }

    // Four samples to a quad, a texel of margin all round for the normals, and the water depth under each texel.
    private void SampleFine(int l, double a0, double b0, double span, double footprint) {

        double radius = Terrain.Radius;
        double step = span / (PatchJob.TexelsPerQuad * PatchJob.Quads);

        for (int k = 0; k < PatchSamples.FineSize; k++) {

            Vector3d direction = CubeFace.Direction(Face, a0 + (k - 1) * step, b0 + (l - 1) * step);
            double height = Terrain.HeightAt(direction, footprint / PatchJob.TexelsPerQuad);

            Fine[l * PatchSamples.FineSize + k] = direction * (radius + height);

            if (k >= 1 && l >= 1 && k <= PatchJob.Texels && l <= PatchJob.Texels) {

                double level = Terrain.WaterLevelAt(direction, footprint / PatchJob.TexelsPerQuad);

                Depths[(l - 1) * PatchJob.Texels + k - 1] = double.IsNaN(level) ? -PatchJob.WaterDepthRange : level - height;

            }

        }

    }

    // Ground at the vertex spacing, out to HorizonReach past the patch; the vertices keep their height and water level too.
    private void SampleGrid(int l, double a0, double b0, double span, double footprint) {

        int j = l - PatchJob.HorizonReach;

        for (int k = 0; k < PatchSamples.GridSize; k++) {

            int i = k - PatchJob.HorizonReach;
            Vector3d direction = CubeFace.Direction(Face, a0 + i * span / PatchJob.Quads, b0 + j * span / PatchJob.Quads);
            double height = Terrain.HeightAt(direction, footprint);

            Grid[l * PatchSamples.GridSize + k] = direction * (Terrain.Radius + height);

            if (i >= 0 && j >= 0 && i < PatchJob.Vertices && j < PatchJob.Vertices) {

                int v = j * PatchJob.Vertices + i;

                Directions[v] = direction;
                Heights[v] = height;
                Levels[v] = Terrain.WaterLevelAt(direction, footprint);
                Shores[v] = Terrain.ShoreDistanceAt(direction);

            }

        }

    }

    // The water detail, two texels to a quad: the river's current in the ground's east and north, and the shelter the
    // land gives from the wind's sea (the share of the open sea's height the fetch upwind lets it raise) and from swell
    // (the share that reaches in past the land toward where it comes from).
    private void SampleWater(int l, double a0, double b0, double span, double footprint) {

        double step = span / (PatchJob.WaterTexels - 1);

        for (int k = 0; k < PatchJob.WaterTexels; k++) {

            Vector3d direction = CubeFace.Direction(Face, a0 + k * step, b0 + l * step);
            double across = Math.Max(Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y), 1e-9);
            Vector3d east = new Vector3d(-direction.Y / across, direction.X / across, 0.0);
            Vector3d north = Vector3d.Cross(direction, east);
            Vector3d flow = Terrain.FlowAt(direction);
            double sea = 0.0;
            double swell = 0.0;

            if (Terrain.ShoreDistanceAt(direction) < Inland * Terrain.HorizontalScale) {

                SeaConditions open = SeaState.At(direction, Month);
                double windFrom = Math.Atan2(open.WindEast, open.WindNorth) + Math.PI;
                double swellFrom = Math.Atan2(open.SwellEast, open.SwellNorth) + Math.PI;
                SeaConditions sheltered = Spectrum.Sheltered(open, Terrain.FetchAt(direction, windFrom), Terrain.FetchAt(direction, swellFrom));

                sea = open.SeaHeight > 0.0 ? sheltered.SeaHeight / open.SeaHeight : 1.0;
                swell = open.SwellHeight > 0.0 ? sheltered.SwellHeight / open.SwellHeight : 1.0;

            }

            int t = (l * PatchJob.WaterTexels + k) * 4;

            WaterDetail[t] = PatchJob.EncodeFlow(Vector3d.Dot(flow, east));
            WaterDetail[t + 1] = PatchJob.EncodeFlow(Vector3d.Dot(flow, north));
            WaterDetail[t + 2] = PatchJob.EncodeUnit(sea);
            WaterDetail[t + 3] = PatchJob.EncodeUnit(swell);

        }

    }

    // The parent level's posts, which fall on this patch's even vertices, at the parent's footprint.
    private void SampleCoarse(int j, double a0, double b0, double span, double footprint) {

        for (int i = 0; i < PatchJob.Vertices; i += 2) {

            Vector3d direction = CubeFace.Direction(Face, a0 + i * span / PatchJob.Quads, b0 + j * span / PatchJob.Quads);

            Coarse[j * PatchJob.Vertices + i] = direction * (Terrain.Radius + Terrain.HeightAt(direction, 2.0 * footprint));

        }

    }

}
