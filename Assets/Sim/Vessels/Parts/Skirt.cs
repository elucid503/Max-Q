using System;

using MaxQ.Sim.Vessels.Motion;

namespace MaxQ.Sim.Vessels.Parts;

/// <summary>A procedural shell between two diameters: a forward skirt over a tank dome, an interstage, or an adapter
/// tapering to the payload above.</summary>
public sealed class Skirt : Part {

    public double BottomRadius { get; init; }
    public double TopRadius { get; init; }
    public double Length { get; init; }

    /// <summary>Shell mass per unit area, stringers and frames included, kg/m^2.</summary>
    public double ArealDensity { get; init; }

    public override double Height => Length;

    internal override double OuterRadius => Math.Max(BottomRadius, TopRadius);

    public double Mass {

        get {

            double rise = TopRadius - BottomRadius;

            return ArealDensity * Math.PI * (BottomRadius + TopRadius) * Math.Sqrt(Length * Length + rise * rise);

        }

    }

    internal override MassProperties AddTo(MassProperties sum) {

        double mean = 0.5 * (BottomRadius + TopRadius);

        // A frustum's surface centroid leans towards its wider end.
        double centre = Length * (BottomRadius + 2.0 * TopRadius) / (3.0 * (BottomRadius + TopRadius));

        return sum.AddShell(Mass, Station + centre, mean, Length);

    }

}
