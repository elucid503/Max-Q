using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Game.Planet.Water.Waves;

/// <summary>The plane the waves are laid out on, tangent to the body under the camera, in body-fixed coordinates. As the
/// camera moves, the axes are carried along the body's surface without turning (parallel transport) and the distance
/// travelled is added to the offset, so the waves stay put on the sea while the plane follows the camera.</summary>
internal sealed class WaveFrame {

    private double _u;
    private double _v;

    /// <summary>Unit direction from the body's centre to the frame's origin.</summary>
    public Vector3d Origin { get; private set; } = Vector3d.UnitX;

    public Vector3d East { get; private set; } = Vector3d.UnitY;

    public Vector3d North { get; private set; } = Vector3d.UnitZ;

    /// <summary>Starts over at <paramref name="direction"/>, axes along geographic east and north; the same place
    /// always gives the same waves.</summary>
    public void Reset(Vector3d direction) {

        Origin = direction;
        East = GeographicEast(direction);
        North = Vector3d.Cross(direction, East);
        _u = 0.0;
        _v = 0.0;

    }

    /// <summary>Moves the origin to <paramref name="direction"/> on a body of <paramref name="radius"/> metres.</summary>
    public void Follow(Vector3d direction, double radius) {

        Vector3d step = (direction - Origin) * radius;

        _u += Vector3d.Dot(step, East);
        _v += Vector3d.Dot(step, North);

        Vector3d axis = Vector3d.Cross(Origin, direction);
        double sine = axis.Length;

        if (sine > 1e-15) {

            Vector3d k = axis / sine;
            double cosine = Vector3d.Dot(Origin, direction);

            East = Rotate(East, k, cosine, sine);

        }

        Origin = direction;
        East = (East - Origin * Vector3d.Dot(East, Origin)).Normalized;
        North = Vector3d.Cross(Origin, East);

    }

    /// <summary>Where the origin falls in a tile of <paramref name="size"/> metres turned by <paramref name="angle"/>
    /// radians from the frame: the tile's coordinates of the origin, 0 to 1.</summary>
    public void TileOffset(double size, double angle, out float x, out float y) {

        double c = Math.Cos(angle);
        double s = Math.Sin(angle);
        double u = (c * _u + s * _v) / size;
        double v = (-s * _u + c * _v) / size;

        x = (float)(u - Math.Floor(u));
        y = (float)(v - Math.Floor(v));

    }

    /// <summary>Where the origin falls on patterns repeating every <paramref name="period"/> metres along the frame's
    /// axes: the distance travelled, wrapped.</summary>
    public void PatternOffset(double period, out float x, out float y) {

        x = (float)(_u - Math.Floor(_u / period) * period);
        y = (float)(_v - Math.Floor(_v / period) * period);

    }

    /// <summary>Radians anticlockwise from the frame's east axis to a heading given as geographic east and north at the
    /// origin.</summary>
    public double Heading(double east, double north) {

        Vector3d geographicEast = GeographicEast(Origin);
        Vector3d heading = geographicEast * east + Vector3d.Cross(Origin, geographicEast) * north;

        return Math.Atan2(Vector3d.Dot(heading, North), Vector3d.Dot(heading, East));

    }

    /// <summary>Radians clockwise from geographic north to a heading given as east and north components.</summary>
    public static double Bearing(double east, double north) => Math.Atan2(east, north);

    private static Vector3d GeographicEast(Vector3d direction) {

        double across = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);

        // At a pole any direction is east; take one.
        return across < 1e-9 ? Vector3d.UnitY : new Vector3d(-direction.Y / across, direction.X / across, 0.0);

    }

    // Rodrigues' rotation of v about the unit axis k.
    private static Vector3d Rotate(Vector3d v, Vector3d k, double cosine, double sine) =>
        v * cosine + Vector3d.Cross(k, v) * sine + k * (Vector3d.Dot(k, v) * (1.0 - cosine));

}
