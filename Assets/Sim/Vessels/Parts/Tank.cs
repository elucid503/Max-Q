using System;

using MaxQ.Sim.Vessels.Motion;

namespace MaxQ.Sim.Vessels.Parts;

/// <summary>A procedural bipropellant tank: a barrel closed by ellipsoidal domes, oxidiser below a common bulkhead and fuel
/// above. The bulkhead sits wherever both run dry together. The bottom node is the aft dome's apex (or the flat aft
/// bulkhead), where an engine mounts; the top node is the barrel's top, so the forward dome stands up inside whatever
/// stacks above.</summary>
public sealed class Tank : Part {

    // Acceleration below which the liquid is taken as floating free, and how fast it then drifts off the outlets.
    private const double SettlingAcceleration = 1e-4;
    private const double DriftSeconds = 30.0;

    // The narrowest gap the liquid still has to cross to cover the outlet when the tank is all but full.
    private const double MinimumUllage = 0.1;

    private double? _oxidiser;
    private double? _fuel;

    public double Radius { get; init; }
    public double BarrelLength { get; init; }

    /// <summary>Depth over radius of every dome: aft, forward and the common bulkhead.</summary>
    public double DomeRatio { get; init; }

    /// <summary>Whether the aft end is a flat bulkhead rather than a dome.</summary>
    public bool FlatBottom { get; init; }

    /// <summary>Oxidiser over fuel by mass.</summary>
    public double MixtureRatio { get; init; }

    public double OxidiserDensity { get; init; }
    public double FuelDensity { get; init; }

    /// <summary>Wall mass per unit area, stringers, frames and insulation included, kg/m^2.</summary>
    public double WallArealDensity { get; init; }

    public double DomeDepth => DomeRatio * Radius;

    /// <summary>Depth of the aft dome: nothing when the bottom is flat.</summary>
    public double AftDomeDepth => FlatBottom ? 0.0 : DomeDepth;

    public override double Height => AftDomeDepth + BarrelLength;

    internal override double OuterRadius => Radius;

    /// <summary>Height of the common bulkhead's rim above the bottom node.</summary>
    public double BulkheadHeight => AftDomeDepth + (OxidiserVolume - AftDomeVolume + DomeVolume) / CrossSection;

    public double OxidiserCapacity => OxidiserVolume * OxidiserDensity;
    public double FuelCapacity => FuelVolume * FuelDensity;

    public double Oxidiser {

        get => _oxidiser ?? OxidiserCapacity;
        internal set => _oxidiser = Math.Max(0.0, value);

    }

    public double Fuel {

        get => _fuel ?? FuelCapacity;
        internal set => _fuel = Math.Max(0.0, value);

    }

    /// <summary>How fully the propellant lies settled over the outlets, 0 floating free to 1 settled.</summary>
    public double Settled { get; private set; }

    public double DryMass => WallArealDensity * (2.0 * Math.PI * Radius * BarrelLength + 2.0 * DomeArea + AftArea);

    public double Fill => (Oxidiser + Fuel) / (OxidiserCapacity + FuelCapacity);

    private double CrossSection => Math.PI * Radius * Radius;
    private double DomeVolume => 2.0 / 3.0 * CrossSection * DomeDepth;
    private double AftDomeVolume => FlatBottom ? 0.0 : DomeVolume;
    private double AftArea => FlatBottom ? CrossSection : DomeArea;
    private double TotalVolume => CrossSection * BarrelLength + DomeVolume + AftDomeVolume;
    private double OxidiserVolume => TotalVolume * OxidiserShare;
    private double FuelVolume => TotalVolume - OxidiserVolume;

    // Volume share that empties both together.
    private double OxidiserShare => MixtureRatio / OxidiserDensity / (MixtureRatio / OxidiserDensity + 1.0 / FuelDensity);

    private double DomeArea {

        get {

            // Half an oblate spheroid of semi-axes radius and depth.
            double e = Math.Sqrt(Math.Max(0.0, 1.0 - DomeRatio * DomeRatio));

            if (e < 1e-6) {

                return 2.0 * Math.PI * Radius * Radius;

            }

            return Math.PI * Radius * Radius * (1.0 + (1.0 - e * e) / e * 0.5 * Math.Log((1.0 + e) / (1.0 - e)));

        }

    }

    /// <summary>Whether the bulkhead clears the aft dome; a barrel too short for its oxidiser cannot be built.</summary>
    public bool IsValid => Radius > 0.0 && BarrelLength >= 0.0 && DomeRatio > 0.0 && DomeRatio <= 1.0 && BulkheadHeight - DomeDepth >= AftDomeDepth
        && BulkheadHeight <= Height;

    internal void Draw(double oxidiser, double fuel) {

        Oxidiser -= oxidiser;
        Fuel -= fuel;

    }

    /// <summary>Moves the liquid under an acceleration along the stack axis: it takes sqrt(2 gap / a) to cross the ullage.</summary>
    internal void Settle(double dt, double axialAcceleration) {

        double gap = Math.Max(MinimumUllage, (1.0 - Fill) * TotalVolume / CrossSection);

        if (axialAcceleration > SettlingAcceleration) {

            Settled = Math.Min(1.0, Settled + dt / Math.Sqrt(2.0 * gap / axialAcceleration));

        } else if (axialAcceleration < -SettlingAcceleration) {

            Settled = Math.Max(0.0, Settled - dt / Math.Sqrt(2.0 * gap / -axialAcceleration));

        } else {

            Settled *= Math.Exp(-dt / DriftSeconds);

        }

    }

    internal override MassProperties AddTo(MassProperties sum) {

        double h = DomeDepth;
        double aft = AftDomeDepth;
        double top = Height;
        double bulkhead = BulkheadHeight;
        double radius = Radius;
        double wall = WallArealDensity;
        double domeMass = wall * DomeArea;

        sum = sum.AddShell(wall * 2.0 * Math.PI * radius * BarrelLength, Station + aft + 0.5 * BarrelLength, radius, BarrelLength);

        sum = FlatBottom ? sum.AddCylinder(wall * CrossSection, Station, radius, 0.0) : AddDome(sum, 0.5 * h);
        sum = AddDome(sum, bulkhead - 0.5 * h);
        sum = AddDome(sum, top + 0.5 * h);

        // Half-spheroid volumes centre 3/8 of their depth off the flat face. Each compartment's liquid is taken as a
        // cylinder of the same volume centred where the full compartment is, filled from its aft end.
        double barrel = CrossSection;
        double dome = DomeVolume;
        double oxidiserCentre = (AftDomeVolume * 0.625 * aft + barrel * (bulkhead - aft) * 0.5 * (aft + bulkhead) - dome * (bulkhead - 0.375 * h)) / OxidiserVolume;
        double fuelCentre = (barrel * (top - bulkhead) * 0.5 * (bulkhead + top) + dome * (bulkhead - 0.375 * h) + dome * (top + 0.375 * h)) / FuelVolume;

        sum = AddLiquid(sum, Oxidiser, OxidiserCapacity, OxidiserVolume, oxidiserCentre);

        return AddLiquid(sum, Fuel, FuelCapacity, FuelVolume, fuelCentre);

        MassProperties AddDome(MassProperties total, double z) =>
            total.Add(domeMass, Station + z, 2.0 / 3.0 * domeMass * radius * radius, domeMass * radius * radius / 3.0);

    }

    // ponytail: liquid is carried as a rigid slab; slosh modes would need their own pendulum states.
    private MassProperties AddLiquid(MassProperties sum, double mass, double capacity, double volume, double centre) {

        if (mass <= 0.0) {

            return sum;

        }

        double length = volume / CrossSection;
        double depth = length * mass / capacity;

        return sum.AddCylinder(mass, Station + centre - 0.5 * length + 0.5 * depth, Radius, depth);

    }

}
