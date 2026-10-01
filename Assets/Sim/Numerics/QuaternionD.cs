using System;

namespace MaxQ.Sim.Numerics;

/// <summary>Double-precision unit quaternion; as an attitude it rotates body-frame vectors into the sim frame.</summary>
public readonly struct QuaternionD {

    public readonly double W;
    public readonly double X;
    public readonly double Y;
    public readonly double Z;

    public QuaternionD(double w, double x, double y, double z) {

        W = w;
        X = x;
        Y = y;
        Z = z;

    }

    public static readonly QuaternionD Identity = new QuaternionD(1.0, 0.0, 0.0, 0.0);

    public double Length => Math.Sqrt(W * W + X * X + Y * Y + Z * Z);
    public QuaternionD Normalized => this * (1.0 / Length);
    public QuaternionD Conjugate => new QuaternionD(W, -X, -Y, -Z);

    /// <summary>A rotation of <paramref name="angle"/> radians about a unit axis.</summary>
    public static QuaternionD AxisAngle(Vector3d axis, double angle) {

        double half = 0.5 * angle;
        double sin = Math.Sin(half);

        return new QuaternionD(Math.Cos(half), axis.X * sin, axis.Y * sin, axis.Z * sin);

    }

    /// <summary>The rotation about the vector's direction by its length in radians.</summary>
    public static QuaternionD FromRotationVector(Vector3d v) {

        double angle = v.Length;

        return angle < 1e-15 ? Identity : AxisAngle(v / angle, angle);

    }

    public static QuaternionD operator *(QuaternionD a, QuaternionD b) => new QuaternionD(

        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W

    );

    public static QuaternionD operator *(QuaternionD q, double s) => new QuaternionD(q.W * s, q.X * s, q.Y * s, q.Z * s);

    public static QuaternionD operator +(QuaternionD a, QuaternionD b) => new QuaternionD(a.W + b.W, a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public Vector3d Rotate(Vector3d v) {

        Vector3d u = new Vector3d(X, Y, Z);
        Vector3d t = 2.0 * Vector3d.Cross(u, v);

        return v + W * t + Vector3d.Cross(u, t);

    }

    public Vector3d InverseRotate(Vector3d v) => Conjugate.Rotate(v);

    public override string ToString() => $"({W:G6}, {X:G6}, {Y:G6}, {Z:G6})";

}
