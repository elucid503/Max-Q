using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Surface;

/// <summary>The nearest river to a point, and its channel there.</summary>
public readonly struct RiverPoint {

    /// <summary>Metres on the body from the point to the river's centreline.</summary>
    public double Distance { get; init; }

    /// <summary>Water surface above the reference radius, real-world metres.</summary>
    public double Level { get; init; }

    /// <summary>Half the channel's width, metres on the body.</summary>
    public double HalfWidth { get; init; }

    /// <summary>Depth at the middle of the channel, metres on the body.</summary>
    public double Depth { get; init; }

    /// <summary>Mean speed of the current, m/s.</summary>
    public double Speed { get; init; }

    /// <summary>Real-world metres the surface falls per metre on the body along the centreline, downstream.</summary>
    public double Fall { get; init; }

    /// <summary>Unit vector along the centreline, downstream.</summary>
    public Vector3d Downstream { get; init; }

    /// <summary>Unit body-fixed direction of the nearest point on the centreline.</summary>
    public Vector3d Centre { get; init; }

    /// <summary>Whether the river is crossing a lake or the sea here, which hold the water and have no current.</summary>
    public bool InBody { get; init; }

}

/// <summary>Rivers mapped from rivers.bin: centrelines smoothed as quadratic B-splines through their vertices, running on
/// through the junctions of a main stem, a surface level, width and depth at each vertex, and each reach's speed, found
/// through an index of 0.1 degree cells.</summary>
public readonly unsafe struct Rivers {

    private const int Magic = 0x5652514F;
    private const double Fixed = 1.0 / (1 << 30);
    private const double LevelUnit = 0.25;
    private const double WidthUnit = 0.1;
    private const double DepthUnit = 0.01;
    private const double SpeedUnit = 0.01;

    // Survey posts (15") per index cell (0.1 degree).
    private const int PostsPerCell = 24;

    // Straight pieces each curve is measured against.
    private const int Pieces = 4;

    /// <summary>A river reshapes the ground out to its banks and a floodplain beyond them of this many widths, and
    /// at least <see cref="FloodplainMinimum"/> metres; the bake indexes each river as far.</summary>
    public const double FloodplainWidths = 2.0;

    public const double FloodplainMinimum = 20.0;

    private readonly IntPtr _owner;
    private readonly IntPtr _before;
    private readonly IntPtr _after;
    private readonly IntPtr _positions;
    private readonly IntPtr _levels;
    private readonly IntPtr _flags;
    private readonly IntPtr _widths;
    private readonly IntPtr _depths;
    private readonly IntPtr _speeds;
    private readonly IntPtr _starts;
    private readonly IntPtr _entries;
    private readonly int _rows;
    private readonly int _columns;

    /// <summary>Wraps a mapped rivers.bin.</summary>
    public Rivers(IntPtr data) {

        int* header = (int*)data;

        if (header[0] != Magic) {

            throw new InvalidOperationException("rivers.bin does not hold rivers; rebake with tools/terra.sh");

        }

        long reaches = header[1];
        long vertices = header[2];
        _rows = header[3];
        _columns = header[4];

        // Past the header and each reach's first vertex, which the neighbours below make redundant here.
        int* next = header + 6 + reaches + 1;

        _owner = (IntPtr)next;
        next += vertices;
        _before = (IntPtr)next;
        next += vertices;
        _after = (IntPtr)next;
        next += vertices;
        _positions = (IntPtr)next;
        next += 3 * vertices;
        _levels = (IntPtr)next;
        next += vertices;
        _flags = (IntPtr)next;
        next += (vertices + 3) / 4;
        _widths = (IntPtr)next;
        next += vertices;
        _depths = (IntPtr)next;
        next += vertices;
        _speeds = (IntPtr)next;
        next += reaches;
        _starts = (IntPtr)next;
        next += (long)_rows * _columns + 1;
        _entries = (IntPtr)next;

    }

    /// <summary>Metres of ground a river of <paramref name="width"/> metres reshapes either side of its centreline.</summary>
    public static double Reach(double width) => 0.5 * width + Math.Max(FloodplainWidths * width, FloodplainMinimum);

    /// <summary>The river whose channel edge is nearest <paramref name="direction"/>, among those that reach it.
    /// <paramref name="row"/> and <paramref name="column"/> are the direction's position in survey posts.</summary>
    public bool Nearest(Vector3d direction, double row, double column, double radius, out RiverPoint point) {

        point = default;

        int cellRow = Math.Min(Math.Max((int)Math.Floor((row + 0.5) / PostsPerCell), 0), _rows - 1);
        int cellColumn = (int)Math.Floor((column + 0.5) / PostsPerCell);

        cellColumn = (cellColumn % _columns + _columns) % _columns;

        long cell = (long)cellRow * _columns + cellColumn;
        uint start = ((uint*)_starts)[cell];
        uint end = ((uint*)_starts)[cell + 1];

        if (start == end) {

            return false;

        }

        Vector3d here = direction * radius;
        double best = double.MaxValue;
        bool found = false;

        for (uint e = start; e < end; e++) {

            int vertex = (int)((uint*)_entries)[e];
            int reach = ((int*)_owner)[vertex];
            int before = ((int*)_before)[vertex];
            int after = ((int*)_after)[vertex];
            double widthV = ((int*)_widths)[vertex] * WidthUnit;
            double widthA = 0.5 * (((int*)_widths)[before] * WidthUnit + widthV);
            double widthB = 0.5 * (((int*)_widths)[after] * WidthUnit + widthV);
            double widest = Math.Max(widthV, Math.Max(widthA, widthB));

            Vector3d v = Position(vertex) * radius;
            Vector3d a = before != vertex ? 0.5 * (Position(before) * radius + v) : v;
            Vector3d b = after != vertex ? 0.5 * (Position(after) * radius + v) : v;
            double span = Math.Max((a - v).Length, (b - v).Length);

            if ((here - v).Length - span > Reach(widest)) {

                continue;

            }

            Closest(here, a, v, b, out double t, out double distance);

            double halfWidth = 0.5 * Spline(widthA, widthV, widthB, t);

            if (distance > Reach(2.0 * halfWidth) || distance - halfWidth >= best) {

                continue;

            }

            double levelV = ((int*)_levels)[vertex] * LevelUnit;
            double levelA = 0.5 * (((int*)_levels)[before] * LevelUnit + levelV);
            double levelB = 0.5 * (((int*)_levels)[after] * LevelUnit + levelV);
            double depthV = ((int*)_depths)[vertex] * DepthUnit;
            Vector3d along = 2.0 * (1.0 - t) * (v - a) + 2.0 * t * (b - v);
            Vector3d centre = Bezier(a, v, b, t);

            if (along.LengthSquared < 1e-12) {

                along = b - a;

            }

            double lengthAlong = along.Length;

            best = distance - halfWidth;
            found = true;
            point = new RiverPoint {

                Distance = distance,
                Level = Spline(levelA, levelV, levelB, t),
                HalfWidth = halfWidth,
                Depth = Spline(0.5 * (((int*)_depths)[before] * DepthUnit + depthV), depthV, 0.5 * (((int*)_depths)[after] * DepthUnit + depthV), t),
                Speed = ((int*)_speeds)[reach] * SpeedUnit,
                Fall = lengthAlong > 1e-6 ? -(2.0 * (1.0 - t) * (levelV - levelA) + 2.0 * t * (levelB - levelV)) / lengthAlong : 0.0,
                Downstream = along.LengthSquared > 1e-12 ? along.Normalized : Vector3d.Zero,
                Centre = centre.Normalized,
                InBody = ((byte*)_flags)[vertex] != 0,

            };

        }

        return found;

    }

    private Vector3d Position(int vertex) {

        int* p = (int*)_positions + 3L * vertex;

        return new Vector3d(p[0] * Fixed, p[1] * Fixed, p[2] * Fixed);

    }

    // A value carried along a vertex's piece of the curve as its position is: from the midpoint before, through the vertex's,
    // to the midpoint after.
    private static double Spline(double a, double v, double b, double t) => (1.0 - t) * (1.0 - t) * a + 2.0 * (1.0 - t) * t * v + t * t * b;

    private static Vector3d Bezier(Vector3d a, Vector3d v, Vector3d b, double t) =>
        (1.0 - t) * (1.0 - t) * a + 2.0 * (1.0 - t) * t * v + t * t * b;

    // Nearest point of the curve to p, measured against a few straight pieces of it; the sagitta a piece leaves is a
    // few centimetres on the tightest bends the smoothed centrelines make.
    private static void Closest(Vector3d p, Vector3d a, Vector3d v, Vector3d b, out double t, out double distance) {

        t = 0.0;
        distance = double.MaxValue;
        Vector3d from = a;

        for (int i = 1; i <= Pieces; i++) {

            double t1 = (double)i / Pieces;
            Vector3d to = Bezier(a, v, b, t1);
            Vector3d piece = to - from;
            double lengthSquared = piece.LengthSquared;
            double s = lengthSquared > 1e-12 ? Math.Min(Math.Max(Vector3d.Dot(p - from, piece) / lengthSquared, 0.0), 1.0) : 0.0;
            double d = (p - (from + piece * s)).Length;

            if (d < distance) {

                distance = d;
                t = (i - 1 + s) / Pieces;

            }

            from = to;

        }

    }

}
