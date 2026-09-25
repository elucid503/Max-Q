using MaxQ.Sim.Numerics;

using UnityEngine;

namespace MaxQ.Game.Map;

/// <summary>Sim-to-scene conversion for the map: kilometre units, Z-up right-handed to Y-up left-handed.</summary>
public static class MapSpace {

    public const double MetresPerUnit = 1_000.0;

    /// <summary>The sim position that sits at the scene origin this frame.</summary>
    public static Vector3d Origin { get; set; }

    public static Vector3 ToScene(Vector3d simPosition) => Direction((simPosition - Origin) / MetresPerUnit);

    public static Vector3 Direction(Vector3d v) => new Vector3((float)v.X, (float)v.Z, (float)v.Y);

}
