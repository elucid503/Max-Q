using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Ocean;

/// <summary>The wind and waves at a place in one month. Directions are unit (east, north) vectors of travel.</summary>
public readonly struct SeaConditions {

    /// <summary>Mean wind 10 m over the sea, m/s east and north.</summary>
    public double WindEast { get; init; }

    public double WindNorth { get; init; }

    /// <summary>Mean wind speed, m/s; higher than the mean wind's length where the wind veers.</summary>
    public double WindSpeed { get; init; }

    /// <summary>Significant height (m), peak period (s) and heading of the sea the local wind raises.</summary>
    public double SeaHeight { get; init; }

    public double SeaPeriod { get; init; }

    public double SeaEast { get; init; }

    public double SeaNorth { get; init; }

    /// <summary>Significant height (m), peak period (s) and heading of swell arriving from distant storms.</summary>
    public double SwellHeight { get; init; }

    public double SwellPeriod { get; init; }

    public double SwellEast { get; init; }

    public double SwellNorth { get; init; }

    /// <summary>Share of the sea covered by ice, 0 to 1.</summary>
    public double Ice { get; init; }

}

/// <summary>The sea-state climatology baked from ERA5 (monthly means of 1991-2020) on a 0.5 degree grid, mapped from
/// sea_state.bin. Blittable, so the game's Burst jobs read it too.</summary>
public readonly unsafe struct SeaState {

    private const int Magic = 0x5353514D;
    private const int Channels = 12;
    private const double CellDegrees = 0.5;

    private readonly IntPtr _data;
    private readonly int _months;
    private readonly int _rows;
    private readonly int _columns;

    /// <summary>Wraps a mapped sea_state.bin.</summary>
    public SeaState(IntPtr data) {

        int* header = (int*)data;

        if (header[0] != Magic || header[4] != Channels) {

            throw new InvalidOperationException("sea_state.bin does not hold a sea state; rebake with tools/terra.sh");

        }

        _months = header[1];
        _rows = header[2];
        _columns = header[3];
        _data = (IntPtr)(header + 5);

    }

    /// <summary>Conditions at a unit body-fixed direction in <paramref name="month"/> (1 to 12).</summary>
    public SeaConditions At(Vector3d direction, int month) {

        double latitude = Math.Asin(Math.Min(Math.Max(direction.Z, -1.0), 1.0)) * 180.0 / Math.PI;
        double longitude = Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI;
        double row = (90.0 - latitude) / CellDegrees;
        double column = (longitude + 180.0) / CellDegrees;

        int r0 = Math.Min((int)Math.Floor(row), _rows - 2);
        int c0 = (int)Math.Floor(column);
        double tr = row - r0;
        double tc = column - c0;
        long plane = (long)(Math.Min(Math.Max(month, 1), _months) - 1) * _rows * _columns;

        short* a = Cell(plane, r0, c0);
        short* b = Cell(plane, r0, c0 + 1);
        short* c = Cell(plane, r0 + 1, c0);
        short* d = Cell(plane, r0 + 1, c0 + 1);
        double wa = (1.0 - tr) * (1.0 - tc);
        double wb = (1.0 - tr) * tc;
        double wc = tr * (1.0 - tc);
        double wd = tr * tc;

        double seaEast = Blend(a, b, c, d, wa, wb, wc, wd, 5);
        double seaNorth = Blend(a, b, c, d, wa, wb, wc, wd, 6);
        double swellEast = Blend(a, b, c, d, wa, wb, wc, wd, 9);
        double swellNorth = Blend(a, b, c, d, wa, wb, wc, wd, 10);
        double sea = Math.Max(Math.Sqrt(seaEast * seaEast + seaNorth * seaNorth), 1e-9);
        double swell = Math.Max(Math.Sqrt(swellEast * swellEast + swellNorth * swellNorth), 1e-9);

        return new SeaConditions {

            WindEast = Blend(a, b, c, d, wa, wb, wc, wd, 0) * 0.01,
            WindNorth = Blend(a, b, c, d, wa, wb, wc, wd, 1) * 0.01,
            WindSpeed = Blend(a, b, c, d, wa, wb, wc, wd, 2) * 0.01,
            SeaHeight = Blend(a, b, c, d, wa, wb, wc, wd, 3) * 0.001,
            SeaPeriod = Blend(a, b, c, d, wa, wb, wc, wd, 4) * 0.01,
            SeaEast = seaEast / sea,
            SeaNorth = seaNorth / sea,
            SwellHeight = Blend(a, b, c, d, wa, wb, wc, wd, 7) * 0.001,
            SwellPeriod = Blend(a, b, c, d, wa, wb, wc, wd, 8) * 0.01,
            SwellEast = swellEast / swell,
            SwellNorth = swellNorth / swell,
            Ice = Math.Min(Math.Max(Blend(a, b, c, d, wa, wb, wc, wd, 11) * 1e-4, 0.0), 1.0),

        };

    }

    // Columns wrap at the date line.
    private short* Cell(long plane, int row, int column) {

        column = (column % _columns + _columns) % _columns;

        return (short*)_data + (plane + (long)row * _columns + column) * Channels;

    }

    private static double Blend(short* a, short* b, short* c, short* d, double wa, double wb, double wc, double wd, int channel) =>
        a[channel] * wa + b[channel] * wb + c[channel] * wc + d[channel] * wd;

}
