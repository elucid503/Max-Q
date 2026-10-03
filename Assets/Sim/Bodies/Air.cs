using System;

namespace MaxQ.Sim.Bodies;

/// <summary>An isothermal atmosphere turning with its body: pressure and density fall by e every scale height, the same
/// air the sky draws, and nothing is left of it above its top.</summary>
public sealed class Air {

    // Specific gas constant of dry air (J/kg/K) and its ratio of specific heats.
    private const double GasConstant = 287.05;
    private const double HeatRatio = 1.4;

    public double SurfacePressure { get; }
    public double ScaleHeight { get; }

    /// <summary>Height above the reference radius past which the air is taken as gone, m.</summary>
    public double Top { get; }

    /// <summary>The temperature that makes the scale height hold under the body's surface gravity, K.</summary>
    public double Temperature { get; }

    public double SpeedOfSound { get; }

    public Air(double surfacePressure, double scaleHeight, double top, double surfaceGravity) {

        SurfacePressure = surfacePressure;
        ScaleHeight = scaleHeight;
        Top = top;
        Temperature = scaleHeight * surfaceGravity / GasConstant;
        SpeedOfSound = Math.Sqrt(HeatRatio * GasConstant * Temperature);

    }

    /// <summary>Pa at a height above the reference radius.</summary>
    public double PressureAt(double height) => height >= Top ? 0.0 : SurfacePressure * Math.Exp(-height / ScaleHeight);

    /// <summary>kg/m^3 at a height above the reference radius.</summary>
    public double DensityAt(double height) => PressureAt(height) / (GasConstant * Temperature);

}
