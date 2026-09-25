using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Surface;

/// <summary>A body's ground as one function of direction: the survey, scaled to the body, with procedural relief on it.
/// Rendering builds every patch from it and physics tests contact against it, so the two cannot disagree.</summary>
public readonly unsafe struct Terrain {

    /// <summary>Survey heights are real-world metres; at one-fifth scale they shrink with the radius, keeping real slopes.</summary>
    public const double VerticalScale = 0.2;

    private const int Columns = 86_400;
    private const int Rows = 43_200;
    private const int Mips = 7;

    private const int WaterColumns = 43_200;
    private const int WaterRows = 21_600;
    private const short NoWater = short.MinValue;

    // Land a water sheet can reach always stands this far clear of it, so the sheet never shows through the shore.
    private const double ShoreClearance = 0.05;

    // Mip whose posts the slope that shapes the relief is measured across (~370 m on Terra).
    private const int SlopeMip = 2;

    // Distance from the axis, as a share of the radius, inside which the slope stops shaping the relief (~2.5 km on Terra).
    private const double PoleFade = 0.002;

    private readonly IntPtr _elevation;
    private readonly IntPtr _water;
    private readonly double _spacing;

    public double Radius { get; }

    /// <summary>Wraps mapped survey data: <paramref name="elevation"/> is the 15" grid and its mips, <paramref name="water"/> the 30" levels.</summary>
    public Terrain(IntPtr elevation, IntPtr water, double radius) {

        _elevation = elevation;
        _water = water;
        _spacing = 2.0 * Math.PI * radius / Columns;
        Radius = radius;

    }

    /// <summary>Metres between survey posts at the equator.</summary>
    public double PostSpacing => _spacing;

    /// <summary>Ground height above the reference radius, metres, at a unit body-fixed direction. <paramref name="footprint"/>
    /// is the spacing the caller samples at; the ground keeps no detail finer than it. Zero is full detail.</summary>
    public double HeightAt(Vector3d direction, double footprint) {

        GridPosition(direction, out double row, out double column, out double latitude);

        double survey = Survey(row, column, latitude, footprint);
        double height = survey * VerticalScale + Relief.Detail(direction * Radius, footprint, Gradient(direction, row, column, latitude));
        double level = WaterLevel(row, column);

        if (!double.IsNaN(level) && survey >= level) {

            height = Math.Max(height, level * VerticalScale + ShoreClearance);

        }

        return height;

    }

    /// <summary>Height of the water surface above the reference radius, metres, or NaN where there is no water.</summary>
    public double WaterLevelAt(Vector3d direction) {

        GridPosition(direction, out double row, out double column, out _);

        return WaterLevel(row, column) * VerticalScale;

    }

    // Burst compiles this struct into the patch builder, so it keeps to out parameters rather than tuples.
    private static void GridPosition(Vector3d direction, out double row, out double column, out double latitude) {

        latitude = Math.Asin(Math.Min(Math.Max(direction.Z, -1.0), 1.0));
        row = (0.5 * Math.PI - latitude) / Math.PI * Rows - 0.5;
        column = (Math.Atan2(direction.Y, direction.X) + Math.PI) / (2.0 * Math.PI) * Columns - 0.5;

    }

    // Real metres, from the mip whose posts are no wider than the footprint. Past the coarsest mip, four taps box-filter it.
    private double Survey(double row, double column, double latitude, double footprint) {

        int mip = footprint <= _spacing ? 0 : Math.Min((int)Math.Floor(Math.Log(footprint / _spacing) / Math.Log(2.0)), Mips - 1);
        double coarsest = _spacing * (1 << (Mips - 1));

        if (footprint <= 2.0 * coarsest) {

            return Bicubic(mip, row, column);

        }

        double rows = 0.25 * footprint / _spacing;
        double columns = rows / Math.Max(Math.Cos(latitude), 0.01);

        return 0.25 * (Bicubic(mip, row - rows, column - columns) + Bicubic(mip, row - rows, column + columns) +
            Bicubic(mip, row + rows, column - columns) + Bicubic(mip, row + rows, column + columns));

    }

    // Uphill rise over run at SlopeMip, along the ground; horizontal and vertical are both at body scale, so its length
    // is the real-world slope.
    private Vector3d Gradient(Vector3d direction, double row, double column, double latitude) {

        double scale = 1 << SlopeMip;
        double r = (row + 0.5) / scale - 0.5;
        double c = (column + 0.5) / scale - 0.5;

        double east = Bilinear(SlopeMip, r, c + 1.0) - Bilinear(SlopeMip, r, c - 1.0);
        double north = Bilinear(SlopeMip, r - 1.0, c) - Bilinear(SlopeMip, r + 1.0, c);
        double run = 2.0 * _spacing * scale;

        east /= run * Math.Max(Math.Cos(latitude), 0.01);
        north /= run;

        double across = Math.Max(Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y), 1e-9);
        Vector3d eastward = new Vector3d(-direction.Y / across, direction.X / across, 0.0);
        Vector3d northward = Vector3d.Cross(direction, eastward);

        // A latitude-longitude gradient has no one direction at a pole, so it fades out just short of each.
        double pole = Math.Min(across / PoleFade, 1.0);

        return (eastward * east + northward * north) * (VerticalScale * pole);

    }

    private double WaterLevel(double row, double column) {

        int r = Math.Min(Math.Max((int)Math.Floor((row + 0.5) * 0.5), 0), WaterRows - 1);
        int c = (int)Math.Floor((column + 0.5) * 0.5);

        c = (c % WaterColumns + WaterColumns) % WaterColumns;

        short level = ((short*)_water)[(long)r * WaterColumns + c];

        return level == NoWater ? double.NaN : level;

    }

    // Catmull-Rom over 4x4 posts of a mip; row and column are in mip-0 posts.
    private double Bicubic(int mip, double row, double column) {

        double scale = 1 << mip;
        double r = (row + 0.5) / scale - 0.5;
        double c = (column + 0.5) / scale - 0.5;

        double fr = Math.Floor(r);
        double fc = Math.Floor(c);
        int r0 = (int)fr - 1;
        int c0 = (int)fc - 1;

        double tr = r - fr;
        double tc = c - fc;
        double sum = 0.0;

        for (int i = 0; i < 4; i++) {

            double line = CatmullRom(tc, Post(mip, r0 + i, c0), Post(mip, r0 + i, c0 + 1), Post(mip, r0 + i, c0 + 2), Post(mip, r0 + i, c0 + 3));

            sum += line * CatmullWeight(tr, i);

        }

        return sum;

    }

    private double Bilinear(int mip, double r, double c) {

        double fr = Math.Floor(r);
        double fc = Math.Floor(c);
        int r0 = (int)fr;
        int c0 = (int)fc;

        double tr = r - fr;
        double tc = c - fc;

        double top = Post(mip, r0, c0) + (Post(mip, r0, c0 + 1) - Post(mip, r0, c0)) * tc;
        double bottom = Post(mip, r0 + 1, c0) + (Post(mip, r0 + 1, c0 + 1) - Post(mip, r0 + 1, c0)) * tc;

        return top + (bottom - top) * tr;

    }

    // Rows past a pole continue down the far meridian; columns wrap at the date line.
    private double Post(int mip, int row, int column) {

        int rows = Rows >> mip;
        int columns = Columns >> mip;

        if (row < 0) {

            row = -1 - row;
            column += columns / 2;

        } else if (row >= rows) {

            row = 2 * rows - 1 - row;
            column += columns / 2;

        }

        column = (column % columns + columns) % columns;

        return ((short*)_elevation)[MipOffset(mip) + (long)row * columns + column];

    }

    private static long MipOffset(int mip) {

        long offset = 0;

        for (int i = 0; i < mip; i++) {

            offset += (long)(Rows >> i) * (Columns >> i);

        }

        return offset;

    }

    private static double CatmullRom(double t, double p0, double p1, double p2, double p3) =>
        p0 * CatmullWeight(t, 0) + p1 * CatmullWeight(t, 1) + p2 * CatmullWeight(t, 2) + p3 * CatmullWeight(t, 3);

    private static double CatmullWeight(double t, int i) => i switch {

        0 => ((-0.5 * t + 1.0) * t - 0.5) * t,
        1 => (1.5 * t - 2.5) * t * t + 1.0,
        2 => ((-1.5 * t + 2.0) * t + 0.5) * t,
        _ => (0.5 * t - 0.5) * t * t,

    };

}
