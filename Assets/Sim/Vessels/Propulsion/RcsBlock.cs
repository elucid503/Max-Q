using System;
using System.Collections.Generic;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Vessels.Motion;
using MaxQ.Sim.Vessels.Parts;

namespace MaxQ.Sim.Vessels.Propulsion;

/// <summary>A ring of identical RCS pods mounted on the side of the part below it in the stack. Each pod's ports are in
/// its own frame: X radially out, Y round the ring, Z up the stack, from the pod's centre.</summary>
public sealed class RcsBlock : Part {

    private Thruster[] _thrusters;

    /// <summary>Height of the pods' centres relative to this part's station, usually negative to sit on the part below.</summary>
    public double Offset { get; init; }

    public double RingRadius { get; init; }
    public int Count { get; init; }

    /// <summary>Angle of the first pod about the stack axis, from body X, radians.</summary>
    public double Phase { get; init; }

    /// <summary>Dry mass of one pod, kg.</summary>
    public double PodMass { get; init; }

    public double Thrust { get; init; }
    public double SpecificImpulse { get; init; }
    public IReadOnlyList<Port> Ports { get; init; } = Array.Empty<Port>();

    public override double Height => 0.0;

    /// <summary>Angle of pod <paramref name="pod"/> about the stack axis.</summary>
    public double PodAngle(int pod) => Phase + 2.0 * Math.PI * pod / Count;

    public override IReadOnlyList<Thruster> Thrusters {

        get {

            if (_thrusters == null) {

                _thrusters = new Thruster[Count * Ports.Count];

                for (int pod = 0; pod < Count; pod++) {

                    double angle = PodAngle(pod);
                    double cos = Math.Cos(angle);
                    double sin = Math.Sin(angle);

                    Vector3d Turn(Vector3d v) => new Vector3d(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, v.Z);

                    Vector3d centre = new Vector3d(RingRadius * cos, RingRadius * sin, Offset);

                    for (int i = 0; i < Ports.Count; i++) {

                        _thrusters[pod * Ports.Count + i] = new Thruster(this, centre + Turn(Ports[i].Position), Turn(Ports[i].Direction), Thrust, SpecificImpulse);

                    }

                }

            }

            return _thrusters;

        }

    }

    internal override MassProperties AddTo(MassProperties sum) => sum.AddRing(Count * PodMass + Hydrazine, Station + Offset, RingRadius);

}
