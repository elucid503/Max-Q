using System;

namespace MaxQ.Sim.Numerics;

/// <summary>Double-precision 3-vector. Sim frame is right-handed, Z up (north pole).</summary>
public readonly struct Vector3d {

    public readonly double X;
    public readonly double Y;
    public readonly double Z;

    public Vector3d(double x, double y, double z) {

        X = x;
        Y = y;
        Z = z;

    }

    public static readonly Vector3d Zero = new Vector3d(0.0, 0.0, 0.0);
    public static readonly Vector3d UnitX = new Vector3d(1.0, 0.0, 0.0);
    public static readonly Vector3d UnitY = new Vector3d(0.0, 1.0, 0.0);
    public static readonly Vector3d UnitZ = new Vector3d(0.0, 0.0, 1.0);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSquared => X * X + Y * Y + Z * Z;
    public Vector3d Normalized => this / Length;

    public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3d operator -(Vector3d a) => new Vector3d(-a.X, -a.Y, -a.Z);
    public static Vector3d operator *(Vector3d a, double s) => new Vector3d(a.X * s, a.Y * s, a.Z * s);
    public static Vector3d operator *(double s, Vector3d a) => new Vector3d(a.X * s, a.Y * s, a.Z * s);
    public static Vector3d operator /(Vector3d a, double s) => new Vector3d(a.X / s, a.Y / s, a.Z / s);

    public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vector3d Cross(Vector3d a, Vector3d b) => new Vector3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static double Distance(Vector3d a, Vector3d b) => (a - b).Length;

    public override string ToString() => $"({X:G6}, {Y:G6}, {Z:G6})";

}
