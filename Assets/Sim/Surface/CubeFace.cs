using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Surface;

/// <summary>The six faces of the cube-sphere, in the body-fixed frame, with an equal-angle projection.</summary>
public static class CubeFace {

    public static Vector3d Normal(int face) => face switch {

        0 => new Vector3d(1.0, 0.0, 0.0),
        1 => new Vector3d(-1.0, 0.0, 0.0),
        2 => new Vector3d(0.0, 1.0, 0.0),
        3 => new Vector3d(0.0, -1.0, 0.0),
        4 => new Vector3d(0.0, 0.0, 1.0),
        _ => new Vector3d(0.0, 0.0, -1.0),

    };

    public static Vector3d Right(int face) => face switch {

        0 => new Vector3d(0.0, 1.0, 0.0),
        1 => new Vector3d(0.0, 0.0, 1.0),
        2 => new Vector3d(0.0, 0.0, 1.0),
        3 => new Vector3d(1.0, 0.0, 0.0),
        4 => new Vector3d(1.0, 0.0, 0.0),
        _ => new Vector3d(0.0, 1.0, 0.0),

    };

    public static Vector3d Up(int face) => face switch {

        0 => new Vector3d(0.0, 0.0, 1.0),
        1 => new Vector3d(0.0, 1.0, 0.0),
        2 => new Vector3d(1.0, 0.0, 0.0),
        3 => new Vector3d(0.0, 0.0, 1.0),
        4 => new Vector3d(0.0, 1.0, 0.0),
        _ => new Vector3d(1.0, 0.0, 0.0),

    };

    /// <summary>Unit direction at face coordinates <paramref name="a"/> (right) and <paramref name="b"/> (up), each -1 to 1.</summary>
    public static Vector3d Direction(int face, double a, double b) {

        Vector3d v = Normal(face) + Right(face) * Math.Tan(a * Math.PI / 4.0) + Up(face) * Math.Tan(b * Math.PI / 4.0);

        return v / v.Length;

    }

}
