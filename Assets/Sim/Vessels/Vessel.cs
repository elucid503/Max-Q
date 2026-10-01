using System;
using System.Collections.Generic;

using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Orbits;
using MaxQ.Sim.Vessels.Motion;
using MaxQ.Sim.Vessels.Parts;
using MaxQ.Sim.Vessels.Propulsion;

namespace MaxQ.Sim.Vessels;

/// <summary>A rigid stack of parts. Coasting, it rides its conic and turns torque-free, exactly over any interval; while
/// anything fires it integrates in short steps, handed between spheres of influence as it goes.</summary>
public sealed class Vessel {

    /// <summary>Longest integration step under power.</summary>
    public const double StepSeconds = 0.02;

    private readonly List<Part> _parts = new List<Part>();
    private readonly List<Engine> _engines = new List<Engine>();
    private readonly List<Tank> _tanks = new List<Tank>();
    private readonly List<Thruster> _thrusters = new List<Thruster>();
    private readonly List<Thruster> _active = new List<Thruster>();
    private double[] _duties = Array.Empty<double>();

    // The conic being coasted and where the torque-free turn is reckoned from; no patch while under power.
    private Patch _patch;
    private double _coastTime;
    private QuaternionD _coastAttitude;
    private Vector3d _coastSpin;

    public string Name { get; }
    public double Time { get; private set; }
    public CelestialBody Body { get; private set; }

    /// <summary>The centre of mass relative to <see cref="Body"/>, and the attitude and spin about it.</summary>
    public RigidState State { get; private set; }

    public MassProperties MassProperties { get; private set; }
    public bool HasImpacted { get; private set; }

    /// <summary>Bottom to top.</summary>
    public IReadOnlyList<Part> Parts => _parts;
    public IReadOnlyList<Engine> Engines => _engines;
    public IReadOnlyList<Tank> Tanks => _tanks;
    public IReadOnlyList<Thruster> Thrusters => _thrusters;

    /// <summary>The conic being coasted, or null while under power.</summary>
    public Patch Current => _patch;

    public Orbit Orbit => _patch?.Orbit ?? Orbit.FromStateVectors(State.Position, State.Velocity, Body.Mu, Time);

    /// <summary>Stack station zero relative to <see cref="Body"/>: the origin the parts are drawn from.</summary>
    public Vector3d Datum => State.Position - State.Attitude.Rotate(MassProperties.CentreOfMass * Vector3d.UnitZ);

    /// <summary>Centre of mass relative to the root body.</summary>
    public Vector3d RootPosition => Body.PositionAt(Time) + State.Position;

    /// <summary>Stacks the parts bottom to top and sets them coasting on the orbit at the given time and attitude.</summary>
    public Vessel(string name, IReadOnlyList<Part> parts, CelestialBody body, Orbit orbit, double time, QuaternionD attitude) {

        if (parts.Count == 0) {

            throw new ArgumentException("A vessel needs at least one part.", nameof(parts));

        }

        Name = name;
        Body = body;
        Time = time;

        Stack(parts);
        Collect(parts);

        (Vector3d position, Vector3d velocity) = orbit.StateAt(time);

        State = new RigidState(position, velocity, attitude, Vector3d.Zero);

    }

    // A piece split off at staging, already stacked and moving.
    private Vessel(string name, IReadOnlyList<Part> parts, CelestialBody body, RigidState state, double time) {

        Name = name;
        Body = body;
        Time = time;

        Collect(parts);

        State = state;

    }

    public void Advance(double time) => Advance(time, default);

    public void Advance(double time, Controls controls) {

        while (Time < time && !HasImpacted) {

            if (controls.IsIdle && EnginesOff()) {

                Coast(time);

                return;

            }

            Fly(Math.Min(StepSeconds, time - Time), controls);

        }

    }

    /// <summary>Fires the lowest decoupler. Everything up to and including it leaves as a new vessel, which is returned;
    /// null when there is nothing to separate.</summary>
    public Vessel Stage() {

        int cut = _parts.FindIndex(part => part is Decoupler);

        if (cut < 0 || cut == _parts.Count - 1) {

            return null;

        }

        Decoupler decoupler = (Decoupler)_parts[cut];
        List<Part> lower = _parts.GetRange(0, cut + 1);
        List<Part> upper = _parts.GetRange(cut + 1, _parts.Count - cut - 1);

        RigidState lowerState = Piece(Measure(lower), -decoupler.Impulse);
        RigidState upperState = Piece(Measure(upper), decoupler.Impulse);

        Vessel spent = new Vessel(Name + " Stage", lower, Body, lowerState, Time);

        Collect(upper);
        State = upperState;
        _patch = null;

        return spent;

    }

    private void Stack(IReadOnlyList<Part> parts) {

        double station = 0.0;
        int segment = 0;

        foreach (Part part in parts) {

            if (part is Tank { IsValid: false }) {

                throw new ArgumentException($"Tank '{part.Name}' is too short for its oxidiser or has no valid shape.", nameof(parts));

            }

            part.Station = station;
            part.Segment = segment;
            station += part.Height;

            if (part is Decoupler) {

                segment++;

            }

        }

    }

    private void Collect(IReadOnlyList<Part> parts) {

        _parts.Clear();
        _engines.Clear();
        _tanks.Clear();
        _thrusters.Clear();

        foreach (Part part in parts) {

            _parts.Add(part);
            _thrusters.AddRange(part.Thrusters);

            if (part is Engine engine) {

                _engines.Add(engine);

            } else if (part is Tank tank) {

                _tanks.Add(tank);

            }

        }

        _duties = new double[_thrusters.Count];
        MassProperties = Measure(_parts);

    }

    private static MassProperties Measure(IReadOnlyList<Part> parts) {

        MassProperties sum = default;

        foreach (Part part in parts) {

            sum = part.AddTo(sum);

        }

        return sum;

    }

    // Where a piece of this vessel goes when cut loose: it keeps the motion of its own points, plus a push along the axis.
    private RigidState Piece(MassProperties piece, double impulse) {

        Vector3d offset = (piece.CentreOfMass - MassProperties.CentreOfMass) * Vector3d.UnitZ;
        Vector3d swing = State.Attitude.Rotate(Vector3d.Cross(State.AngularVelocity, offset));
        Vector3d push = State.Attitude.Rotate(Vector3d.UnitZ) * (impulse / piece.Mass);

        return new RigidState(State.Position + State.Attitude.Rotate(offset), State.Velocity + swing + push, State.Attitude, State.AngularVelocity);

    }

    private bool EnginesOff() {

        foreach (Engine engine in _engines) {

            if (engine.Phase != EnginePhase.Off) {

                return false;

            }

        }

        return true;

    }

    private void Coast(double time) {

        if (_patch == null) {

            _patch = Trajectory.NextPatch(Body, Orbit.FromStateVectors(State.Position, State.Velocity, Body.Mu, Time), Time);
            _coastTime = Time;
            _coastAttitude = State.Attitude;
            _coastSpin = State.AngularVelocity;

        }

        foreach (Thruster thruster in _thrusters) {

            thruster.Firing = false;
            thruster.Accumulator = 0.0;

        }

        while (time >= _patch.EndTime) {

            if (!_patch.Continues) {

                HasImpacted = _patch.End == PatchEnd.Impact;
                time = _patch.EndTime;

                break;

            }

            (CelestialBody next, Orbit orbit) = Trajectory.Transition(_patch);

            _patch = Trajectory.NextPatch(next, orbit, _patch.EndTime);
            Body = next;

        }

        (Vector3d position, Vector3d velocity) = _patch.Orbit.StateAt(time);
        (QuaternionD attitude, Vector3d spin) = RigidBody.Coast(_coastAttitude, _coastSpin, MassProperties.Axial, MassProperties.Transverse, time - _coastTime);

        foreach (Tank tank in _tanks) {

            tank.Settle(time - Time, 0.0);

        }

        State = new RigidState(position, velocity, attitude, spin);
        Time = time;

    }

    private void Fly(double dt, Controls controls) {

        MassProperties mass = MassProperties;
        double centre = mass.CentreOfMass;
        Vector3d force = Vector3d.Zero;
        Vector3d torque = Vector3d.Zero;

        foreach (Engine engine in _engines) {

            (double settled, bool fed) = Feed(engine.Segment);

            engine.Update(dt, controls.Throttle, Steer(engine, centre, controls.Rotation), fed, settled, Time);

            Vector3d thrust = engine.Direction * engine.Output;

            force += thrust;
            torque += Vector3d.Cross(engine.Mount - centre * Vector3d.UnitZ, thrust);

            DrawPropellant(engine.Segment, engine.MassFlow * dt, engine.MixtureRatio);

        }

        SelectThrusters();
        RcsMixer.Allocate(_active, centre, controls.Rotation, controls.Translation, _duties);

        foreach (Thruster thruster in _thrusters) {

            thruster.Firing = false;

        }

        for (int i = 0; i < _active.Count; i++) {

            Thruster thruster = _active[i];
            double duty = _duties[i];

            thruster.Accumulator = duty > 0.0 ? thruster.Accumulator + duty : 0.0;

            if (thruster.Accumulator < 0.5 || Hydrazine(thruster.Owner.Segment) < thruster.MassFlow * dt) {

                continue;

            }

            Vector3d push = thruster.Direction * -thruster.Thrust;

            thruster.Accumulator -= 1.0;
            thruster.Firing = true;
            force += push;
            torque += Vector3d.Cross(thruster.Position - centre * Vector3d.UnitZ, push);

            DrawHydrazine(thruster.Owner.Segment, thruster.MassFlow * dt);

        }

        foreach (Tank tank in _tanks) {

            tank.Settle(dt, force.Z / mass.Mass);

        }

        State = RigidBody.Step(State, Body.Mu, mass.Mass, mass.Axial, mass.Transverse, force, torque, dt);
        Time += dt;
        _patch = null;

        // Draining propellant moves the centre of mass through the structure, not the structure through space.
        MassProperties = Measure(_parts);

        Vector3d shift = State.Attitude.Rotate((MassProperties.CentreOfMass - centre) * Vector3d.UnitZ);

        State = new RigidState(State.Position + shift, State.Velocity, State.Attitude, State.AngularVelocity);

        Rehome();

    }

    // The gimbal deflection, as a fraction of range, that turns the stack the way the pilot asks.
    private static Vector3d Steer(Engine engine, double centre, Vector3d rotation) {

        double lever = centre - engine.Mount.Z;

        if (Math.Abs(lever) < 1e-6) {

            return Vector3d.Zero;

        }

        double side = Math.Sign(lever);

        return new Vector3d(-rotation.Y * side, rotation.X * side, 0.0);

    }

    // How settled an engine's propellant is and whether both propellants are still there.
    private (double Settled, bool Fed) Feed(int segment) {

        double settled = 1.0;
        double oxidiser = 0.0;
        double fuel = 0.0;

        foreach (Tank tank in _tanks) {

            if (tank.Segment != segment || tank.Oxidiser + tank.Fuel <= 0.0) {

                continue;

            }

            settled = Math.Min(settled, tank.Settled);
            oxidiser += tank.Oxidiser;
            fuel += tank.Fuel;

        }

        return (settled, oxidiser > 0.0 && fuel > 0.0);

    }

    private void DrawPropellant(int segment, double mass, double mixtureRatio) {

        if (mass <= 0.0) {

            return;

        }

        double oxidiser = 0.0;
        double fuel = 0.0;

        foreach (Tank tank in _tanks) {

            if (tank.Segment == segment) {

                oxidiser += tank.Oxidiser;
                fuel += tank.Fuel;

            }

        }

        double oxidiserDrawn = mass * mixtureRatio / (1.0 + mixtureRatio);
        double fuelDrawn = mass - oxidiserDrawn;

        foreach (Tank tank in _tanks) {

            if (tank.Segment == segment) {

                tank.Draw(oxidiser > 0.0 ? oxidiserDrawn * tank.Oxidiser / oxidiser : 0.0, fuel > 0.0 ? fuelDrawn * tank.Fuel / fuel : 0.0);

            }

        }

    }

    // The lowest stage with RCS propellant flies the vessel; a capsule's own nozzles wait until it is alone.
    private void SelectThrusters() {

        int lowest = int.MaxValue;

        foreach (Thruster thruster in _thrusters) {

            int segment = thruster.Owner.Segment;

            if (segment < lowest && Hydrazine(segment) > 0.0) {

                lowest = segment;

            }

        }

        _active.Clear();

        foreach (Thruster thruster in _thrusters) {

            if (thruster.Owner.Segment == lowest) {

                _active.Add(thruster);

            }

        }

    }

    private double Hydrazine(int segment) {

        double sum = 0.0;

        foreach (Part part in _parts) {

            if (part.Segment == segment) {

                sum += part.Hydrazine;

            }

        }

        return sum;

    }

    private void DrawHydrazine(int segment, double mass) {

        double available = Hydrazine(segment);

        foreach (Part part in _parts) {

            if (part.Segment == segment && part.Hydrazine > 0.0) {

                part.Hydrazine -= mass * part.Hydrazine / available;

            }

        }

    }

    // Hands the vessel across a sphere-of-influence boundary crossed under power, and notices the ground.
    private void Rehome() {

        Vector3d position = State.Position;

        if (Body.Parent != null && position.Length > Body.SoiRadius) {

            (Vector3d bodyPosition, Vector3d bodyVelocity) = Body.Orbit.StateAt(Time);

            State = new RigidState(position + bodyPosition, State.Velocity + bodyVelocity, State.Attitude, State.AngularVelocity);
            Body = Body.Parent;

            return;

        }

        foreach (CelestialBody child in Body.Children) {

            (Vector3d childPosition, Vector3d childVelocity) = child.Orbit.StateAt(Time);

            if (Vector3d.Distance(position, childPosition) < child.SoiRadius) {

                State = new RigidState(position - childPosition, State.Velocity - childVelocity, State.Attitude, State.AngularVelocity);
                Body = child;

                return;

            }

        }

        // ponytail: impact is against the mean radius, as the conics have it; terrain contact comes with landing.
        HasImpacted = position.Length < Body.Radius;

    }

}
