using System;
using System.Collections.Generic;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Orbits;
using MaxQ.Sim.Surface;

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

    /// <summary>Surveyed ground, or null for a body drawn as a plain sphere.</summary>
    public Terrain? Terrain { get; }

    /// <summary>The air, or null for an airless body.</summary>
    public Air Air { get; }
    public IReadOnlyList<CelestialBody> Children => _children;

    /// <summary>Laplace sphere of influence; infinite for the root.</summary>
    public double SoiRadius { get; }

    public CelestialBody(string name, double mu, double radius, double rotationPeriodSeconds, CelestialBody parent = null, Orbit orbit = null, Terrain? terrain = null,
        Air air = null) {

        Name = name;
        Mu = mu;
        Radius = radius;
        RotationPeriodSeconds = rotationPeriodSeconds;

        Parent = parent;
        Orbit = orbit;
        Terrain = terrain;
        Air = air;

        SoiRadius = parent == null ? double.PositiveInfinity : orbit.SemiMajorAxis * Math.Pow(mu / parent.Mu, 0.4);

        parent?._children.Add(this);

    }

    public double SurfaceGravity => Mu / (Radius * Radius);

    /// <summary>The body's spin, rad/s about the sim frame's Z; zero for a tidally locked body, whose turn follows its orbit.</summary>
    public double SpinRate => Orbit != null && Math.Abs(RotationPeriodSeconds - Orbit.Period) < 1.0 ? 0.0 : 2.0 * Math.PI / RotationPeriodSeconds;

    /// <summary>Velocity of the air at a sim-frame position relative to the body: it turns with the ground.</summary>
    public Vector3d AirVelocityAt(Vector3d position) => Vector3d.Cross(SpinRate * Vector3d.UnitZ, position);

    /// <summary>A body-fixed attitude in the sim frame.</summary>
    public QuaternionD FromBodyFixed(QuaternionD attitude, double time) => QuaternionD.AxisAngle(Vector3d.UnitZ, RotationAt(time)) * attitude;

    /// <summary>Position relative to the root body.</summary>
    public Vector3d PositionAt(double time) => Parent == null ? Vector3d.Zero : Parent.PositionAt(time) + Orbit.StateAt(time).Position;

    public Vector3d VelocityAt(double time) => Parent == null ? Vector3d.Zero : Parent.VelocityAt(time) + Orbit.StateAt(time).Velocity;

    /// <summary>Eastward rotation of the body-fixed frame from the sim frame, in radians.</summary>
    public double RotationAt(double time) {

        if (Orbit != null && Math.Abs(RotationPeriodSeconds - Orbit.Period) < 1.0) {

            // Tidally locked: the prime meridian always faces the parent.
            Vector3d toParent = -Orbit.StateAt(time).Position;

            return Math.Atan2(toParent.Y, toParent.X);

        }

        return 2.0 * Math.PI * (time / RotationPeriodSeconds % 1.0);

    }

    /// <summary>A sim-frame vector relative to the body, in the body-fixed frame.</summary>
    public Vector3d ToBodyFixed(Vector3d v, double time) => RotateZ(v, -RotationAt(time));

    public Vector3d FromBodyFixed(Vector3d v, double time) => RotateZ(v, RotationAt(time));

    public override string ToString() => Name;

    private static Vector3d RotateZ(Vector3d v, double angle) {

        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);

        return new Vector3d(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, v.Z);

    }

}
