using System;
using System.Collections.Generic;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Orbits;

namespace MaxQ.Sim.Bodies;

/// <summary>A body on rails. The root sits fixed at the origin; every other body follows a conic about its parent.</summary>
public sealed class CelestialBody {

    private readonly List<CelestialBody> _children = new List<CelestialBody>();

    public string Name { get; }
    public double Mu { get; }
    public double Radius { get; }
    public double RotationPeriodSeconds { get; }

    public CelestialBody Parent { get; }
    public Orbit Orbit { get; }
    public IReadOnlyList<CelestialBody> Children => _children;

    /// <summary>Laplace sphere of influence; infinite for the root.</summary>
    public double SoiRadius { get; }

    public CelestialBody(string name, double mu, double radius, double rotationPeriodSeconds, CelestialBody parent = null, Orbit orbit = null) {

        Name = name;
        Mu = mu;
        Radius = radius;
        RotationPeriodSeconds = rotationPeriodSeconds;

        Parent = parent;
        Orbit = orbit;

        SoiRadius = parent == null ? double.PositiveInfinity : orbit.SemiMajorAxis * Math.Pow(mu / parent.Mu, 0.4);

        parent?._children.Add(this);

    }

    public double SurfaceGravity => Mu / (Radius * Radius);

    /// <summary>Position relative to the root body.</summary>
    public Vector3d PositionAt(double time) => Parent == null ? Vector3d.Zero : Parent.PositionAt(time) + Orbit.StateAt(time).Position;

    public Vector3d VelocityAt(double time) => Parent == null ? Vector3d.Zero : Parent.VelocityAt(time) + Orbit.StateAt(time).Velocity;

    public override string ToString() => Name;

}
