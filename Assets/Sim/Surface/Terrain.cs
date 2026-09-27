using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Surface;

/// <summary>A body's ground as one function of direction: the survey, scaled to the body, with procedural relief on it,
/// and the water on it: lakes and seas at their levels, and rivers in channels cut down their valleys. Rendering builds
/// every patch from it and physics tests contact against it, so the two cannot disagree.</summary>
public readonly unsafe struct Terrain {

    /// <summary>Survey heights are real-world metres; at one-fifth scale they shrink with the radius, keeping real slopes.</summary>
    public const double VerticalScale = 0.2;

    private const double EarthRadius = 6_371_000.0;

    private const int Columns = 86_400;
    private const int Rows = 43_200;
    private const int Mips = 7;

    // Water levels, in quarter metres, and shore distances, in 16 m units, on the 30" grid.
    private const int LevelColumns = 43_200;
    private const int LevelRows = 21_600;
    private const short NoWater = short.MinValue;
    private const double LevelUnit = 0.25;
    private const double ShoreUnit = 16.0;

    // Fetch on the 2' grid: eight directions a cell (E, NE, N, NW, W, SW, S, SE), log-encoded from 100 m to 1000 km.
    private const int FetchScale = 8;
    private const int FetchColumns = Columns / FetchScale;
    private const int FetchRows = Rows / FetchScale;
    private const double FetchMinimum = 100.0;
    private const double FetchMaximum = 1_000_000.0;

    // Land a water sheet can reach always stands this far clear of it, so the sheet never shows through the shore.
    private const double ShoreClearance = 0.05;

    // A river's banks stand at least this far over it before its floodplain blends back into the valley.
    private const double BankRise = 0.3;

    // Manning's roughness of a natural channel with stones and pools, s/m^(1/3).
    private const double Roughness = 0.035;

    // Mip whose posts the slope that shapes the relief is measured across (~370 m on Terra).
    private const int SlopeMip = 2;

    // Distance from the axis, as a share of the radius, inside which the slope stops shaping the relief (~2.5 km on Terra).
    private const double PoleFade = 0.002;

    private readonly IntPtr _elevation;
    private readonly IntPtr _levels;
    private readonly IntPtr _shore;
    private readonly IntPtr _fetch;
    private readonly Rivers _rivers;
    private readonly double _spacing;

    public double Radius { get; }

    /// <summary>Wraps mapped survey data: <paramref name="elevation"/> is the 15" grid and its mips, <paramref name="levels"/>
    /// and <paramref name="shore"/> the 30" water levels and shore distances, <paramref name="fetch"/> the 2' fetch.</summary>
    public Terrain(IntPtr elevation, IntPtr levels, IntPtr shore, IntPtr fetch, Rivers rivers, double radius) {

        _elevation = elevation;
        _levels = levels;
        _shore = shore;
        _fetch = fetch;
        _rivers = rivers;
        _spacing = 2.0 * Math.PI * radius / Columns;
        Radius = radius;

    }

    /// <summary>Metres between survey posts at the equator.</summary>
    public double PostSpacing => _spacing;

    /// <summary>Metres on the body per real-world metre of the survey across the ground.</summary>
    public double HorizontalScale => Radius / EarthRadius;

    /// <summary>Ground height above the reference radius, metres, at a unit body-fixed direction. <paramref name="footprint"/>
    /// is the spacing the caller samples at; the ground keeps no detail finer than it. Zero is full detail.</summary>
    public double HeightAt(Vector3d direction, double footprint) {

        GridPosition(direction, out double row, out double column, out double latitude);

        double survey = Survey(row, column, latitude, footprint);
        Vector3d position = direction * Radius;
        Vector3d gradient = Gradient(direction, row, column, latitude);
        double height = Relief.Strata(position, survey * VerticalScale + Relief.Detail(position, footprint, gradient), gradient.Length, footprint);
        double level = WaterLevel(row, column);

        if (!double.IsNaN(level)) {

            return survey >= level ? Math.Max(height, level * VerticalScale + ShoreClearance) : height;

        }

        if (_rivers.Nearest(direction, row, column, Radius, out RiverPoint river)) {

            return Channel(height, river, footprint);

        }

        return height;

    }

    /// <summary>Height of the water surface above the reference radius, metres, or NaN where there is no water. Rivers
    /// narrower than half of <paramref name="footprint"/> are left out, as the ground leaves out their channels.</summary>
    public double WaterLevelAt(Vector3d direction, double footprint) {

        GridPosition(direction, out double row, out double column, out _);

        double level = WaterLevel(row, column);

        if (!double.IsNaN(level)) {

            return level * VerticalScale;

        }

        if (_rivers.Nearest(direction, row, column, Radius, out RiverPoint river) && Visibility(river.HalfWidth, footprint) > 0.0 &&
            river.Distance < river.HalfWidth + SheetMargin(river.HalfWidth)) {

            return river.Level * VerticalScale;

        }

        return double.NaN;

    }

    /// <summary>The nearest river whose banks reach <paramref name="direction"/>, if any.</summary>
    public bool RiverAt(Vector3d direction, out RiverPoint river) {

        GridPosition(direction, out double row, out double column, out _);

        return _rivers.Nearest(direction, row, column, Radius, out river);

    }

    /// <summary>Velocity of the current, m/s along the ground: fastest mid-channel, still at the banks, and nothing in
    /// lakes and seas. Where the river falls steeply it runs as fast as Manning's law has water that deep fall.</summary>
    public Vector3d FlowAt(Vector3d direction) {

        if (!RiverAt(direction, out RiverPoint river) || river.InBody || river.Distance >= river.HalfWidth) {

            return Vector3d.Zero;

        }

        double across = river.Distance / river.HalfWidth;

        return river.Downstream * (1.5 * RiverSpeed(river) * (1.0 - across * across));

    }

    /// <summary>Mean speed of a river's current, m/s: its reach's, or faster where it falls steeply.</summary>
    public double RiverSpeed(RiverPoint river) {

        double slope = Math.Max(river.Fall * HorizontalScale, 0.0);
        double depth = river.Depth / VerticalScale;

        return Math.Max(river.Speed, Math.Pow(depth, 2.0 / 3.0) * Math.Sqrt(slope) / Roughness);

    }

    /// <summary>Metres on the body to the nearest shore, negative over water.</summary>
    public double ShoreDistanceAt(Vector3d direction) {

        GridPosition(direction, out double row, out double column, out _);

        double r = (row + 0.5) * 0.5 - 0.5;
        double c = (column + 0.5) * 0.5 - 0.5;
        double fr = Math.Floor(r);
        double fc = Math.Floor(c);
        int r0 = (int)fr;
        int c0 = (int)fc;
        double tr = r - fr;
        double tc = c - fc;

        double top = Shore(r0, c0) + (Shore(r0, c0 + 1) - Shore(r0, c0)) * tc;
        double bottom = Shore(r0 + 1, c0) + (Shore(r0 + 1, c0 + 1) - Shore(r0 + 1, c0)) * tc;

        return (top + (bottom - top) * tr) * ShoreUnit * HorizontalScale;

    }

    /// <summary>Real-world metres of open water from <paramref name="direction"/> toward <paramref name="bearing"/>
    /// (radians clockwise from north): the fetch of a wind blowing from that bearing. Zero on land.</summary>
    public double FetchAt(Vector3d direction, double bearing) {

        GridPosition(direction, out double row, out double column, out double latitude);

        double r = (row + 0.5) / FetchScale - 0.5;
        double c = (column + 0.5) / FetchScale - 0.5;
        double fr = Math.Floor(r);
        double fc = Math.Floor(c);
        int r0 = (int)fr;
        int c0 = (int)fc;
        double tr = r - fr;
        double tc = c - fc;

        // The grid's diagonals lean toward north and south by the latitude's shrinking of east-west.
        double diagonal = Math.Atan(Math.Cos(latitude));
        double northWest = LogFetch(r0, c0, bearing, diagonal);
        double southWest = LogFetch(r0 + 1, c0, bearing, diagonal);
        double top = northWest + (LogFetch(r0, c0 + 1, bearing, diagonal) - northWest) * tc;
        double bottom = southWest + (LogFetch(r0 + 1, c0 + 1, bearing, diagonal) - southWest) * tc;
        double log = top + (bottom - top) * tr;

        return log <= 0.0 ? 0.0 : FetchMinimum * Math.Exp(log);

    }

    // Ground where a river runs: a rounded channel inside its banks, then a floodplain kept between just over the water
    // and a little above it, blending back into the valley. Near the channel the ground never dips under the water
    // level, so the sheet stays buried.
    private static double Channel(double height, RiverPoint river, double footprint) {

        double visible = Visibility(river.HalfWidth, footprint);

        if (visible <= 0.0) {

            return height;

        }

        double level = river.Level * VerticalScale;

        if (river.Distance < river.HalfWidth) {

            double across = river.Distance / river.HalfWidth;

            return Math.Min(height, level - river.Depth * (1.0 - across * across) * visible);

        }

        double bank = river.Distance - river.HalfWidth;
        double floodplain = Rivers.Reach(2.0 * river.HalfWidth) - river.HalfWidth;
        double margin = SheetMargin(river.HalfWidth);
        double floor = level + ShoreClearance;
        double flat = Math.Min(Math.Max(height, floor), level + Math.Max(2.0 * river.Depth, BankRise));
        double shaped = height + (flat - height) * visible * (1.0 - Smooth(bank / floodplain));

        return shaped + (Math.Max(shaped, floor) - shaped) * (1.0 - Smooth((bank - margin) / (floodplain - margin)));

    }

    // A river shows once it is half as wide as the footprint, fully once it spans it.
    private static double Visibility(double halfWidth, double footprint) =>
        footprint <= 0.0 ? 1.0 : Math.Min(Math.Max(4.0 * halfWidth / footprint - 1.0, 0.0), 1.0);

    // How far past its banks a river's sheet reaches, buried under them.
    private static double SheetMargin(double halfWidth) => Math.Max(halfWidth, 3.0);

    private static double Smooth(double x) {

        double t = Math.Min(Math.Max(x, 0.0), 1.0);

        return t * t * (3.0 - 2.0 * t);

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

    // Real metres. Water reaches as far as the nearest cell does; within it, the level blends across the cells around
    // that hold water, so rivers slope smoothly.
    private double WaterLevel(double row, double column) {

        double r = (row + 0.5) * 0.5 - 0.5;
        double c = (column + 0.5) * 0.5 - 0.5;

        if (Level((int)Math.Floor(r + 0.5), (int)Math.Floor(c + 0.5)) == NoWater) {

            return double.NaN;

        }

        double fr = Math.Floor(r);
        double fc = Math.Floor(c);
        int r0 = (int)fr;
        int c0 = (int)fc;
        double tr = r - fr;
        double tc = c - fc;
        double sum = 0.0;
        double weights = 0.0;

        for (int i = 0; i < 4; i++) {

            short level = Level(r0 + (i >> 1), c0 + (i & 1));
            double weight = ((i >> 1) == 1 ? tr : 1.0 - tr) * ((i & 1) == 1 ? tc : 1.0 - tc);

            if (level != NoWater) {

                sum += level * weight;
                weights += weight;

            }

        }

        return sum / weights * LevelUnit;

    }

    private short Level(int row, int column) {

        row = Math.Min(Math.Max(row, 0), LevelRows - 1);
        column = (column % LevelColumns + LevelColumns) % LevelColumns;

        return ((short*)_levels)[(long)row * LevelColumns + column];

    }

    private double Shore(int row, int column) {

        row = Math.Min(Math.Max(row, 0), LevelRows - 1);
        column = (column % LevelColumns + LevelColumns) % LevelColumns;

        return ((short*)_shore)[(long)row * LevelColumns + column];

    }

    // Natural log of fetch over FetchMinimum toward bearing, blended between the grid directions either side of it.
    private double LogFetch(int row, int column, double bearing, double diagonal) {

        row = Math.Min(Math.Max(row, 0), FetchRows - 1);
        column = (column % FetchColumns + FetchColumns) % FetchColumns;

        byte* cell = (byte*)_fetch + ((long)row * FetchColumns + column) * 8;
        double turn = bearing / (2.0 * Math.PI);
        double angle = (turn - Math.Floor(turn)) * 2.0 * Math.PI;
        int sector = 0;

        while (sector < 7 && angle >= GridBearing(sector + 1, diagonal)) {

            sector++;

        }

        double start = GridBearing(sector, diagonal);
        double t = (angle - start) / (GridBearing(sector + 1, diagonal) - start);
        double from = Decode(cell[(10 - sector) % 8]);

        return from + (Decode(cell[(9 - sector) % 8]) - from) * t;

    }

    // Bearing of the k-th grid direction clockwise from north (N, NE, E, SE, S, SW, W, NW, then N again), which sit
    // in a cell's slots (10 - k) mod 8.
    private static double GridBearing(int k, double diagonal) {

        int quarter = k >> 1;

        if ((k & 1) == 0) {

            return quarter * 0.5 * Math.PI;

        }

        return quarter * 0.5 * Math.PI + ((quarter & 1) == 0 ? diagonal : 0.5 * Math.PI - diagonal);

    }

    // Land reads as the shortest fetch, so blends across a shore stay finite.
    private static double Decode(byte code) => code <= 1 ? 0.0 : (code - 1) / 254.0 * Math.Log(FetchMaximum / FetchMinimum);

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
