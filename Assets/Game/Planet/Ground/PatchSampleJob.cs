using System;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Surface;

using Terrain = MaxQ.Sim.Surface.Terrain;

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace MaxQ.Game.Planet.Ground;

/// <summary>A patch build's first stage: its twenty-odd thousand terrain samples, a row to each index so the workers share
/// them and none is held long. Rows are the detail texture's, then the horizon grid's, whose middle is the vertex grid,
/// then every other row of the parent's posts.</summary>
[BurstCompile]
internal struct PatchSampleJob : IJobParallelFor {

    public const int Rows = PatchSamples.FineSize + PatchSamples.GridSize + (PatchJob.Vertices + 1) / 2;

    [NativeDisableUnsafePtrRestriction]
    public Terrain Terrain;

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
    public NativeArray<byte> CoverTexels;

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

        // A root has no parent; the assembly morphs it toward its own ground.
        if (Depth > 0) {

            SampleCoarse(2 * (row - PatchSamples.GridSize), a0, b0, span, footprint);

        }

    }

    // Four samples to a quad, a texel of margin all round for the normals, and the water depth and cover of each texel.
    private void SampleFine(int l, double a0, double b0, double span, double footprint) {

        double radius = Terrain.Radius;
        double step = span / (PatchJob.TexelsPerQuad * PatchJob.Quads);

        for (int k = 0; k < PatchSamples.FineSize; k++) {

            Vector3d direction = CubeFace.Direction(Face, a0 + (k - 1) * step, b0 + (l - 1) * step);
            double height = Terrain.HeightAt(direction, footprint / PatchJob.TexelsPerQuad);

            Fine[l * PatchSamples.FineSize + k] = direction * (radius + height);

            if (k >= 1 && l >= 1 && k <= PatchJob.Texels && l <= PatchJob.Texels) {

                int t = (l - 1) * PatchJob.Texels + k - 1;
                double level = Terrain.WaterLevelAt(direction, footprint / PatchJob.TexelsPerQuad);
                Cover cover = Cover.At(Terrain, direction, height, footprint / PatchJob.TexelsPerQuad);

                Depths[t] = double.IsNaN(level) ? -PatchJob.WaterDepthRange : level - height;
                CoverTexels[4 * t] = Unorm(cover.Vegetation);
                CoverTexels[4 * t + 1] = Unorm(cover.Forest);
                CoverTexels[4 * t + 2] = Unorm(cover.Arid);
                CoverTexels[4 * t + 3] = Unorm(cover.Snow);

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

    // The parent level's posts, which fall on this patch's even vertices, at the parent's footprint.
    private void SampleCoarse(int j, double a0, double b0, double span, double footprint) {

        for (int i = 0; i < PatchJob.Vertices; i += 2) {

            Vector3d direction = CubeFace.Direction(Face, a0 + i * span / PatchJob.Quads, b0 + j * span / PatchJob.Quads);

            Coarse[j * PatchJob.Vertices + i] = direction * (Terrain.Radius + Terrain.HeightAt(direction, 2.0 * footprint));

        }

    }

    private static byte Unorm(float x) => (byte)Math.Round(Math.Min(Math.Max(x, 0.0f), 1.0f) * 255.0f);

}
