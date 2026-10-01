using System;
using System.Collections.Generic;

using MaxQ.Sim.Vessels.Motion;
using MaxQ.Sim.Vessels.Propulsion;

namespace MaxQ.Sim.Vessels.Parts;

/// <summary>One piece of a vessel. Parts stack bottom to top along the vessel axis (+Z), each footed on the top node of
/// the one below; positions within a part are measured from its bottom node.</summary>
public abstract class Part {

    private double? _hydrazine;

    public string Name { get; init; } = "";

    /// <summary>Stack station of the bottom node, set when the vessel is assembled.</summary>
    public double Station { get; internal set; }

    /// <summary>Bottom node to top node: what the part adds to the stack. Zero for a part mounted on another's side.</summary>
    public abstract double Height { get; }

    /// <summary>Hydrazine carried for the reaction control system, kg.</summary>
    public double HydrazineCapacity { get; init; }

    public double Hydrazine {

        get => _hydrazine ?? HydrazineCapacity;
        internal set => _hydrazine = Math.Max(0.0, value);

    }

    public virtual IReadOnlyList<Thruster> Thrusters => Array.Empty<Thruster>();

    /// <summary>Which stage the part belongs to, counting up from the bottom; a decoupler closes its stage.</summary>
    internal int Segment { get; set; }

    internal abstract MassProperties AddTo(MassProperties sum);

}
