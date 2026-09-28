using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Surface;

/// <summary>A body's ground as one function of direction: the survey, scaled to the body, with procedural relief on it,
/// and the lakes and seas on it at their levels. The coast is wherever the ground crosses its water's level, so the two
/// cannot disagree. Rendering builds every patch from it and physics tests contact against it.</summary>
public readonly unsafe struct Terrain {

    /// <summary>Survey heights are real-world metres; at one-fifth scale they shrink with the radius, keeping real slopes.</summary>
    public const double VerticalScale = 0.2;

    /// <summary>Bounds on the ground's height, metres on the body: Earth's deepest trench and highest peak, scaled, with
    /// room for the relief.</summary>
    public const double Lowest = -11_000.0 * VerticalScale - 200.0;
    public const double Highest = 8_900.0 * VerticalScale + 200.0;

    private const double EarthRadius = 6_371_000.0;

    // Heights on 2.5' posts and five 2x2 mips; water levels on 5' cells, one level of them to each height mip past the
    // first; shore distances on the 5' cells, in 32 m units; moisture on 0.25 degree cells.
    private const int Columns = 8_640;
    private const int Rows = 4_320;
    private const int Mips = 6;
    private const short NoWater = short.MinValue;
    private const double ShoreUnit = 32.0;
    private const int MoistureColumns = 1_440;
    private const int MoistureRows = 720;

    // Metres either side of the waterline over which the relief fades in.
    private const double ReliefFade = 5.0;

    // Within WanderReach of the shore (metres on the body) the coastline wanders from the survey's by noise this share
    // of each wavelength, from WanderLongest down; slopes gentler than WanderSlope wander as far as it would.
    private const double WanderReach = 2_500.0;
    private const double WanderLongest = 2_048.0;
    private const double WanderRatio = 0.12;
    private const double WanderSlope = 0.002;
    private const int WanderOctaves = 8;

    private readonly IntPtr _heights;
    private readonly IntPtr _levels;
    private readonly IntPtr _shore;
    private readonly IntPtr _moisture;
    private readonly double _spacing;

    public double Radius { get; }

    /// <summary>Wraps mapped survey data, as tools/terra_bake.py lays it out: <paramref name="heights"/> the 2.5' posts and
    /// their mips, <paramref name="levels"/> the 5' water levels and theirs, <paramref name="shore"/> the 5' shore
    /// distances and <paramref name="moisture"/> the quarter-degree moisture.</summary>
    public Terrain(IntPtr heights, IntPtr levels, IntPtr shore, IntPtr moisture, double radius) {

        _heights = heights;
        _levels = levels;
        _shore = shore;
        _moisture = moisture;
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

        GridPosition(direction, out double row, out double column);

        int mip = Mip(footprint);
        double survey = Survey(mip, row, column, direction, footprint) * VerticalScale;
        Vector3d position = direction * Radius;
        Vector3d gradient = Gradient(direction);
        double slope = gradient.Length;
        double relief = Relief.Strata(position, survey + Relief.Detail(position, footprint, gradient), slope, footprint) - survey;
        double reach = Reach(mip, row, column, out double level);

        if (reach <= 0.0) {

            return survey + relief;

        }

        // Near water the ground is kept against its level: the coastline wanders, and the relief never carries the ground
        // across the waterline. Wherever a level cell holds, fully; past a body's reach, less and less.
        double held = level + Held(survey - level + Wander(position, direction, slope, footprint), relief);

        return survey + relief + (held - survey - relief) * reach;

    }

    /// <summary>Height of the water surface above the reference radius, metres, or NaN where no water is near. The
    /// ground shows where it stands over the water.</summary>
    public double WaterLevelAt(Vector3d direction, double footprint) {

        GridPosition(direction, out double row, out double column);

        return Level(Mip(footprint), row, column);

    }

    /// <summary>Metres on the body to the nearest shore, negative over water.</summary>
    public double ShoreDistanceAt(Vector3d direction) {

        GridPosition(direction, out double row, out double column);

        return Bilinear((short*)_shore, Rows / 2, Columns / 2, 0.5 * (row + 0.5) - 0.5, 0.5 * (column + 0.5) - 0.5) * ShoreUnit * HorizontalScale;

    }

    /// <summary>How wet the climate keeps the ground, from 0 in the deserts to 1 under the tropical rain belt.</summary>
    public double MoistureAt(Vector3d direction) {

        GridPosition(direction, out double row, out double column);

        double scale = (double)MoistureRows / Rows;

        return Bilinear((short*)_moisture, MoistureRows, MoistureColumns, scale * (row + 0.5) - 0.5, scale * (column + 0.5) - 0.5) / 1_000.0;

    }

    // The survey's mip whose posts are no wider than the footprint.
    private int Mip(double footprint) =>
        footprint <= _spacing ? 0 : Math.Min((int)Math.Floor(Math.Log(footprint / _spacing) / Math.Log(2.0)), Mips - 1);

    // Burst compiles this struct into the patch builder, so it keeps to out parameters rather than tuples.
    private static void GridPosition(Vector3d direction, out double row, out double column) {

        row = (0.5 - Math.Asin(Math.Min(Math.Max(direction.Z, -1.0), 1.0)) / Math.PI) * Rows - 0.5;
        column = (Math.Atan2(direction.Y, direction.X) + Math.PI) / (2.0 * Math.PI) * Columns - 0.5;

    }

    // Real metres, a cubic B-spline over the mip's posts: smooth, and never outside the posts it blends, so ground that
    // the bake holds over the water stays over it between posts too. Past the coarsest mip, four taps box-filter it.
    private double Survey(int mip, double row, double column, Vector3d direction, double footprint) {

        double coarsest = _spacing * (1 << (Mips - 1));

        if (footprint <= 2.0 * coarsest) {

            return Spline(mip, row, column);

        }

        double rows = 0.25 * footprint / _spacing;
        double columns = rows / Math.Max(Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y), 0.01);

        return 0.25 * (Spline(mip, row - rows, column - columns) + Spline(mip, row - rows, column + columns) +
            Spline(mip, row + rows, column - columns) + Spline(mip, row + rows, column + columns));

    }

    // Water level in metres on the body at the cell of the mip's level grid the point falls in, or NaN.
    private double Level(int mip, double row, double column) {

        int size = 2 << Math.Max(mip - 1, 0);
        short cell = LevelCell(mip, (int)Math.Floor((row + 0.5) / size), (int)Math.Floor((column + 0.5) / size));

        return cell == NoWater ? double.NaN : cell * VerticalScale;

    }

    // How far the water's levels hold at a point, blended bilinearly between the four level cells around it, and their
    // level there: fully wherever the point's own cell has one, which always weighs at least a quarter, and falling to
    // nothing across the cells around a body's reach.
    private double Reach(int mip, double row, double column, out double level) {

        int size = 2 << Math.Max(mip - 1, 0);
        double r = (row + 0.5) / size - 0.5;
        double c = (column + 0.5) / size - 0.5;
        double fr = Math.Floor(r);
        double fc = Math.Floor(c);
        double tr = r - fr;
        double tc = c - fc;
        double sum = 0.0;
        double weights = 0.0;

        for (int i = 0; i < 4; i++) {

            short cell = LevelCell(mip, (int)fr + (i >> 1), (int)fc + (i & 1));
            double weight = ((i >> 1) == 1 ? tr : 1.0 - tr) * ((i & 1) == 1 ? tc : 1.0 - tc);

            if (cell != NoWater) {

                sum += cell * weight;
                weights += weight;

            }

        }

        level = weights > 0.0 ? sum / weights * VerticalScale : double.NaN;

        return Math.Min(4.0 * weights, 1.0);

    }

    // A cell of the level grid that serves the mip; rows stop at the poles, columns wrap at the date line.
    private short LevelCell(int mip, int row, int column) {

        int level = Math.Max(mip - 1, 0);
        int rows = Rows / (2 << level);
        int columns = Columns / (2 << level);
        long offset = 0;

        for (int i = 0; i < level; i++) {

            offset += (long)(Rows / (2 << i)) * (Columns / (2 << i));

        }

        row = Math.Min(Math.Max(row, 0), rows - 1);
        column = (column % columns + columns) % columns;

        return ((short*)_levels)[offset + (long)row * columns + column];

    }

    // Height over the water (negative under it) once the relief is added to ground above metres over it, kept on the same
    // side of the waterline, so the relief leaves no puddles or specks: it fades in over the first ReliefFade metres
    // either side, and where it would reach across, it squeezes into the half next to the water instead.
    private static double Held(double above, double relief) {

        double u = Math.Abs(above);
        double outward = Math.Sign(above) * relief * Smooth(u / ReliefFade);
        double held = outward >= -0.5 * u ? u + outward : 0.5 * u * Math.Exp(1.0 + 2.0 * outward / u);

        return Math.Sign(above) * held;

    }

    // Metres to raise the ground by so the coast wanders: noise whose height moves the waterline across the slope by a
    // share of each wavelength the footprint can carry, fading out away from the shore.
    private double Wander(Vector3d position, Vector3d direction, double slope, double footprint) {

        double shore = Math.Abs(ShoreDistanceAt(direction));
        double fade = 1.0 - Smooth((shore - 0.5 * WanderReach) / (0.5 * WanderReach));

        if (fade <= 0.0) {

            return 0.0;

        }

        double wavelength = WanderLongest;
        double sum = 0.0;

        for (int octave = 0; octave < WanderOctaves; octave++) {

            double weight = footprint <= 0.0 ? 1.0 : Math.Min(Math.Max(wavelength / footprint - 1.0, 0.0), 1.0);

            if (weight <= 0.0) {

                break;

            }

            Vector3d p = position / wavelength;

            sum += weight * WanderRatio * wavelength * Relief.Noise(p.X, p.Y, p.Z, 71u + (uint)octave);
            wavelength *= 0.5;

        }

        return sum * fade * Math.Max(slope, WanderSlope);

    }

    // Uphill rise over run of the survey across a post either way, along the ground; horizontal and vertical are both
    // at body scale, so its length is the real-world slope. Stepped along the body's axes and laid onto the ground,
    // rather than north and east, so it holds up at the poles and never switches frames.
    private Vector3d Gradient(Vector3d direction) {

        double angle = _spacing / Radius;
        Vector3d sum = new Vector3d(0.0, 0.0, 0.0);

        for (int i = 0; i < 3; i++) {

            Vector3d axis = new Vector3d(i == 0 ? 1.0 : 0.0, i == 1 ? 1.0 : 0.0, i == 2 ? 1.0 : 0.0);

            sum += axis * (SurveyAt(direction + axis * angle) - SurveyAt(direction - axis * angle));

        }

        return (sum - direction * Vector3d.Dot(sum, direction)) * (VerticalScale / (2.0 * _spacing));

    }

    // Real metres, bilinear between the posts.
    private double SurveyAt(Vector3d direction) {

        GridPosition(direction.Normalized, out double row, out double column);

        double fr = Math.Floor(row);
        double fc = Math.Floor(column);
        int r = (int)fr;
        int c = (int)fc;
        double tr = row - fr;
        double tc = column - fc;
        double top = Post(0, r, c) + (Post(0, r, c + 1) - Post(0, r, c)) * tc;
        double bottom = Post(0, r + 1, c) + (Post(0, r + 1, c + 1) - Post(0, r + 1, c)) * tc;

        return top + (bottom - top) * tr;

    }

    private double Spline(int mip, double row, double column) {

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

            double line = 0.0;

            for (int j = 0; j < 4; j++) {

                line += Post(mip, r0 + i, c0 + j) * SplineWeight(tc, j);

            }

            sum += line * SplineWeight(tr, i);

        }

        return sum;

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

        long offset = 0;

        for (int i = 0; i < mip; i++) {

            offset += (long)(Rows >> i) * (Columns >> i);

        }

        return ((short*)_heights)[offset + (long)row * columns + column];

    }

    private static double Bilinear(short* grid, int rows, int columns, double r, double c) {

        double fr = Math.Floor(r);
        double fc = Math.Floor(c);
        double tr = r - fr;
        double tc = c - fc;
        int r0 = Math.Min(Math.Max((int)fr, 0), rows - 1);
        int r1 = Math.Min(Math.Max((int)fr + 1, 0), rows - 1);
        int c0 = ((int)fc % columns + columns) % columns;
        int c1 = (c0 + 1) % columns;

        double top = grid[(long)r0 * columns + c0] + (grid[(long)r0 * columns + c1] - grid[(long)r0 * columns + c0]) * tc;
        double bottom = grid[(long)r1 * columns + c0] + (grid[(long)r1 * columns + c1] - grid[(long)r1 * columns + c0]) * tc;

        return top + (bottom - top) * tr;

    }

    private static double Smooth(double x) {

        double t = Math.Min(Math.Max(x, 0.0), 1.0);

        return t * t * (3.0 - 2.0 * t);

    }

    // Uniform cubic B-spline weights of the four posts around a point t of the way between the middle two.
    private static double SplineWeight(double t, int i) => i switch {

        0 => (1.0 - t) * (1.0 - t) * (1.0 - t) / 6.0,
        1 => ((3.0 * t - 6.0) * t * t + 4.0) / 6.0,
        2 => (((-3.0 * t + 3.0) * t + 3.0) * t + 1.0) / 6.0,
        _ => t * t * t / 6.0,

    };

}
