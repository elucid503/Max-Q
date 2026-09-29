using System;

using MaxQ.Sim.Numerics;

using Unity.Collections;
using Unity.Mathematics;

namespace MaxQ.Game.Planet.Ground;

/// <summary>What one patch build gathers across the workers before it assembles, kept between builds so none allocates:
/// the vertex grid and the ring of ground around it that the horizons search, the parent's posts the vertices morph
/// toward, the detail texture's finer grid and the water depth under it, the cover at the vertices where the texels take
/// theirs from them, and the plants and rocks strewn.</summary>
internal sealed class PatchSamples : IDisposable {

    // The vertex grid with HorizonReach vertices of ground around it; the detail texture's grid with a texel of margin
    // for its normals.
    public const int GridSize = PatchJob.Vertices + 2 * PatchJob.HorizonReach;
    public const int FineSize = PatchJob.Texels + 2;

    public readonly NativeArray<Vector3d> Grid = new NativeArray<Vector3d>(GridSize * GridSize, Allocator.Persistent);
    public readonly NativeArray<Vector3d> Directions = new NativeArray<Vector3d>(PatchJob.Vertices * PatchJob.Vertices, Allocator.Persistent);
    public readonly NativeArray<double> Heights = new NativeArray<double>(PatchJob.Vertices * PatchJob.Vertices, Allocator.Persistent);
    public readonly NativeArray<double> Levels = new NativeArray<double>(PatchJob.Vertices * PatchJob.Vertices, Allocator.Persistent);
    public readonly NativeArray<double> Shores = new NativeArray<double>(PatchJob.Vertices * PatchJob.Vertices, Allocator.Persistent);
    public readonly NativeArray<Vector3d> Coarse = new NativeArray<Vector3d>(PatchJob.Vertices * PatchJob.Vertices, Allocator.Persistent);
    public readonly NativeArray<Vector3d> Fine = new NativeArray<Vector3d>(FineSize * FineSize, Allocator.Persistent);
    public readonly NativeArray<double> Depths = new NativeArray<double>(PatchJob.Texels * PatchJob.Texels, Allocator.Persistent);
    public readonly NativeArray<float4> Covers = new NativeArray<float4>(PatchJob.Vertices * PatchJob.Vertices, Allocator.Persistent);
    public readonly NativeArray<float4> Plants = new NativeArray<float4>(PatchStrewJob.PlantLength, Allocator.Persistent);
    public readonly NativeArray<float4> Rocks = new NativeArray<float4>(PatchStrewJob.RockLength, Allocator.Persistent);

    public void Dispose() {

        Grid.Dispose();
        Directions.Dispose();
        Heights.Dispose();
        Levels.Dispose();
        Shores.Dispose();
        Coarse.Dispose();
        Fine.Dispose();
        Depths.Dispose();
        Covers.Dispose();
        Plants.Dispose();
        Rocks.Dispose();

    }

}
