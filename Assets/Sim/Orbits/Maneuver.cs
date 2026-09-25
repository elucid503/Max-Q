using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Orbits;

/// <summary>An impulsive burn, delta-v in m/s along the orbit's prograde, normal and radial-out axes.</summary>
public readonly struct Maneuver {

    public readonly double Time;

    public readonly double Prograde;
    public readonly double Normal;
    public readonly double Radial;

    public Maneuver(double time, double prograde, double normal, double radial) {

        Time = time;

        Prograde = prograde;
        Normal = normal;
        Radial = radial;

    }

    public double DeltaV => System.Math.Sqrt(Prograde * Prograde + Normal * Normal + Radial * Radial);

    public Orbit ApplyTo(Orbit orbit) {

        (Vector3d r, Vector3d v) = orbit.StateAt(Time);

        Vector3d prograde = v.Normalized;
        Vector3d normal = Vector3d.Cross(r, v).Normalized;
        Vector3d radial = Vector3d.Cross(prograde, normal);

        Vector3d deltaV = prograde * Prograde + normal * Normal + radial * Radial;

        return Orbit.FromStateVectors(r, v + deltaV, orbit.Mu, Time);

    }

}
