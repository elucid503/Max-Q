using System;
using System.Collections.Generic;

using MaxQ.Sim.Vessels.Motion;
using MaxQ.Sim.Vessels.Propulsion;

namespace MaxQ.Sim.Vessels.Parts;

/// <summary>The crewed capsule: a modelled asset whose mass, shape and RCS ports come from its catalogue entry.</summary>
public sealed class Capsule : Part {

    private Thruster[] _thrusters;

    public double Mass { get; init; }

    public double Length { get; init; }

    /// <summary>Radius at the base, where it sits on the stack.</summary>
    public double Radius { get; init; }

    /// <summary>Height of the centre of mass above the bottom node.</summary>
    public double CentreOfMassHeight { get; init; }

    public double Thrust { get; init; }
    public double SpecificImpulse { get; init; }
    public IReadOnlyList<Port> Ports { get; init; } = Array.Empty<Port>();

    public override double Height => Length;

    internal override double OuterRadius => Radius;

    public override IReadOnlyList<Thruster> Thrusters {

        get {

            if (_thrusters == null) {

                _thrusters = new Thruster[Ports.Count];

                for (int i = 0; i < Ports.Count; i++) {

                    _thrusters[i] = new Thruster(this, Ports[i].Position, Ports[i].Direction, Thrust, SpecificImpulse);

                }

            }

            return _thrusters;

        }

    }

    // Moments of a solid cone the capsule's size, about its centroid.
    internal override MassProperties AddTo(MassProperties sum) {

        double mass = Mass + Hydrazine;
        double z = Station + CentreOfMassHeight;

        return sum.Add(mass, z, 0.3 * mass * Radius * Radius, mass * (0.15 * Radius * Radius + 0.0375 * Length * Length));

    }

}
