using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Orbits;

namespace MaxQ.Sim.Vessels;

/// <summary>A vessel on rails: a conic about one body, handed over at each SOI boundary as time advances.</summary>
public sealed class Vessel {

    public string Name { get; }
    public Patch Current { get; private set; }

    public CelestialBody Body => Current.Body;
    public Orbit Orbit => Current.Orbit;
    public bool HasImpacted { get; private set; }

    public Vessel(string name, CelestialBody body, Orbit orbit, double time) {

        Name = name;
        Current = Trajectory.NextPatch(body, orbit, time);

    }

    public void Advance(double time) {

        while (time >= Current.EndTime) {

            if (!Current.Continues) {

                HasImpacted = Current.End == PatchEnd.Impact;

                return;

            }

            (CelestialBody body, Orbit orbit) = Trajectory.Transition(Current);
            Current = Trajectory.NextPatch(body, orbit, Current.EndTime);

        }

    }

    /// <summary>Position relative to the root body.</summary>
    public Vector3d PositionAt(double time) => Body.PositionAt(time) + Orbit.StateAt(time).Position;

    public void Execute(Maneuver maneuver) {

        Advance(maneuver.Time);
        Current = Trajectory.NextPatch(Body, maneuver.ApplyTo(Orbit), maneuver.Time);

    }

}
