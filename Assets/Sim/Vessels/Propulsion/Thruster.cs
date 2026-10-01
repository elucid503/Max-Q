using MaxQ.Sim.Numerics;
using MaxQ.Sim.Vessels.Parts;

namespace MaxQ.Sim.Vessels.Propulsion;

/// <summary>Where a nozzle sits on its part and which way it exhausts, in the part's frame (from its bottom node).</summary>
public readonly struct Port {

    public readonly Vector3d Position;

    /// <summary>Unit vector the exhaust leaves along; the thrust pushes the opposite way.</summary>
    public readonly Vector3d Direction;

    public Port(Vector3d position, Vector3d direction) {

        Position = position;
        Direction = direction.Normalized;

    }

}

/// <summary>One on-off monopropellant RCS nozzle. It fires in whole steps, so pulses are real pulses.</summary>
public sealed class Thruster {

    internal Thruster(Part owner, Vector3d localPosition, Vector3d direction, double thrust, double specificImpulse) {

        Owner = owner;
        LocalPosition = localPosition;
        Direction = direction;
        Thrust = thrust;
        SpecificImpulse = specificImpulse;

    }

    public Part Owner { get; }
    public Vector3d LocalPosition { get; }
    public Vector3d Direction { get; }
    public double Thrust { get; }
    public double SpecificImpulse { get; }

    /// <summary>Position on the stack, body frame.</summary>
    public Vector3d Position => LocalPosition + Owner.Station * Vector3d.UnitZ;

    public double MassFlow => Thrust / (SpecificImpulse * Engine.StandardGravity);

    /// <summary>Whether it fired over the last step.</summary>
    public bool Firing { get; internal set; }

    // Sigma-delta: carries the unfired part of the commanded duty into the next step.
    internal double Accumulator { get; set; }

}
