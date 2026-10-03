using System;
using System.Collections.Generic;

using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Orbits;
using MaxQ.Sim.Surface;
using MaxQ.Sim.Vessels.Motion;
using MaxQ.Sim.Vessels.Parts;
using MaxQ.Sim.Vessels.Propulsion;

namespace MaxQ.Sim.Vessels;

/// <summary>A rigid stack of parts. Held on its pad it turns with the ground until its engines lift it off. Coasting above
/// the air, it rides its conic and turns torque-free, exactly over any interval; in the air, or while anything fires, it
/// integrates in short steps, handed between spheres of influence as it goes.</summary>
public sealed class Vessel {

    /// <summary>Longest integration step under power or in the air.</summary>
    public const double StepSeconds = 0.02;

    // Coasting on rails starts this far above the air's top, so a conic always meets the air strictly later.
    private const double AirMargin = 10.0;

    // Clamps let go once every engine has reached what the throttle asks to within this share.
    private const double ReleaseSpool = 0.02;

    // Stability assist: the error and turn within which it rests, how fast it lets the stack turn, how quickly it closes
    // on that rate, and how quickly it closes small errors.
    private const double RestAngle = 0.25 * Math.PI / 180.0;
    private const double RestRate = 0.05 * Math.PI / 180.0;
    private const double MaxTurnRate = 5.0 * Math.PI / 180.0;
    private const double RateSeconds = 0.3;
    private const double AngleSeconds = 1.5;

    // Below this speed through the air (m/s) a prograde hold follows the orbit instead.
    private const double ProgradeAirspeed = 1.0;

    private readonly List<Part> _parts = new List<Part>();
    private readonly List<Engine> _engines = new List<Engine>();
    private readonly List<Tank> _tanks = new List<Tank>();
    private readonly List<Thruster> _thrusters = new List<Thruster>();
    private readonly List<Thruster> _active = new List<Thruster>();
    private double[] _duties = Array.Empty<double>();

    // The stack's extent along its axis and its widest radius, for the air.
    private double _bottom;
    private double _top;
    private double _radius;

    // The conic being coasted and where the torque-free turn is reckoned from; no patch while under power.
    private Patch _patch;
    private double _coastTime;
    private QuaternionD _coastAttitude;
    private Vector3d _coastSpin;

    // On the pad: station zero and the attitude, fixed to the ground.
    private Vector3d _padDatum;
    private QuaternionD _padAttitude;

    // The attitude being held; taken afresh whenever the pilot lets go.
    private QuaternionD? _holdAttitude;

    public string Name { get; }
    public double Time { get; private set; }
    public CelestialBody Body { get; private set; }

    /// <summary>The centre of mass relative to <see cref="Body"/>, and the attitude and spin about it.</summary>
    public RigidState State { get; private set; }

    public MassProperties MassProperties { get; private set; }
    public bool HasImpacted { get; private set; }

    /// <summary>Clamped to its pad until its engines can lift it.</summary>
    public bool IsHeld { get; private set; }

    /// <summary>Half the air's density times the airspeed squared over the last step, Pa.</summary>
    public double DynamicPressure { get; private set; }

    /// <summary>Bottom to top.</summary>
    public IReadOnlyList<Part> Parts => _parts;
    public IReadOnlyList<Engine> Engines => _engines;
    public IReadOnlyList<Tank> Tanks => _tanks;
    public IReadOnlyList<Thruster> Thrusters => _thrusters;

    /// <summary>The conic being coasted, or null while under power, in the air or held.</summary>
    public Patch Current => _patch;

    public Orbit Orbit => _patch?.Orbit ?? Orbit.FromStateVectors(State.Position, State.Velocity, Body.Mu, Time);

    /// <summary>Stack station zero relative to <see cref="Body"/>: the origin the parts are drawn from.</summary>
    public Vector3d Datum => State.Position - State.Attitude.Rotate(MassProperties.CentreOfMass * Vector3d.UnitZ);

    /// <summary>Centre of mass relative to the root body.</summary>
    public Vector3d RootPosition => Body.PositionAt(Time) + State.Position;

    /// <summary>Height of the centre of mass above the body's reference radius, m.</summary>
    public double Altitude => State.Position.Length - Body.Radius;

    /// <summary>Velocity through the air, which turns with the body.</summary>
    public Vector3d Airspeed => State.Velocity - Body.AirVelocityAt(State.Position);

    /// <summary>Whether the stack is low enough to be flown through the air rather than coasted on its conic.</summary>
    public bool InAir => Body.Air is { } air && Altitude < air.Top + AirMargin;

    /// <summary>Lowest and highest stations and the widest radius of the stack.</summary>
    public double Bottom => _bottom;
    public double Top => _top;
    public double Radius => _radius;

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

    /// <summary>Stacks the parts and clamps them to a pad: station zero at <paramref name="datum"/> and turned to
    /// <paramref name="attitude"/>, both fixed to the body.</summary>
    public Vessel(string name, IReadOnlyList<Part> parts, CelestialBody body, double time, Vector3d datum, QuaternionD attitude) {

        if (parts.Count == 0) {

            throw new ArgumentException("A vessel needs at least one part.", nameof(parts));

        }

        Name = name;
        Body = body;
        Time = time;

        Stack(parts);
        Collect(parts);

        _padDatum = datum;
        _padAttitude = attitude;
        IsHeld = true;

        Place();

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

            double dt = Math.Min(StepSeconds, time - Time);

            if (IsHeld) {

                if (controls.Throttle <= 0.0 && EnginesOff()) {

                    Rest(time);

                    return;

                }

                Clamp(dt, controls);

                continue;

            }

            Vector3d rotation = Steering(controls);

            if (controls.Throttle <= 0.0 && controls.Translation.LengthSquared == 0.0 && rotation.LengthSquared == 0.0 && EnginesOff() && !InAir) {

                Coast(time);

                continue;

            }

            Fly(dt, controls, rotation);

        }

    }

    /// <summary>Fires the lowest decoupler. Everything up to and including it leaves as a new vessel, which is returned;
    /// null when there is nothing to separate or the stack is still clamped.</summary>
    public Vessel Stage() {

        int cut = _parts.FindIndex(part => part is Decoupler);

        if (IsHeld || cut < 0 || cut == _parts.Count - 1) {

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

            // A nested engine hangs its length down into the part below.
            part.Station = part is Engine { Nested: true } nested ? station - nested.Length : station;
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
        _bottom = double.PositiveInfinity;
        _top = double.NegativeInfinity;
        _radius = 0.0;

        foreach (Part part in parts) {

            _parts.Add(part);
            _thrusters.AddRange(part.Thrusters);
            _bottom = Math.Min(_bottom, part.Station);
            _top = Math.Max(_top, part.Station + part.Height);
            _radius = Math.Max(_radius, part.OuterRadius);

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

    private double PressureAt(double altitude) => Body.Air?.PressureAt(altitude) ?? 0.0;

    // Held with everything off, the stack only turns with the ground while its propellant lies settled under gravity.
    private void Rest(double time) {

        foreach (Tank tank in _tanks) {

            tank.Settle(time - Time, Body.SurfaceGravity);

        }

        foreach (Engine engine in _engines) {

            engine.Cool(time - Time);

        }

        Time = time;
        Place();

    }

    // Engines run against the clamps, which let go once they are all up to what the throttle asks and can lift the stack.
    private void Clamp(double dt, Controls controls) {

        double ambient = PressureAt(Altitude);
        double lift = 0.0;
        bool spooled = true;

        foreach (Engine engine in _engines) {

            (double settled, bool fed) = Feed(engine.Segment);

            engine.Update(dt, Throttle(engine, controls.Throttle), Vector3d.Zero, 0.0, fed, settled, Time, ambient);
            DrawPropellant(engine.Segment, engine.MassFlow * dt, engine.MixtureRatio);

            if (engine.Segment != _engines[0].Segment) {

                continue;

            }

            double demand = Math.Max(engine.MinimumThrottle, Math.Min(controls.Throttle, 1.0));

            lift += engine.Output;
            spooled &= engine.Phase == EnginePhase.Running && engine.Chamber >= demand - ReleaseSpool;

        }

        foreach (Thruster thruster in _thrusters) {

            thruster.Firing = false;

        }

        foreach (Tank tank in _tanks) {

            tank.Settle(dt, Body.SurfaceGravity);

        }

        Time += dt;
        MassProperties = Measure(_parts);
        _holdAttitude = null;

        Place();

        if (spooled && _engines.Count > 0 && lift > MassProperties.Mass * Body.SurfaceGravity) {

            IsHeld = false;

        }

    }

    // Where the pad has the stack now: carried round with the ground.
    private void Place() {

        QuaternionD attitude = Body.FromBodyFixed(_padAttitude, Time);
        Vector3d position = Body.FromBodyFixed(_padDatum, Time) + attitude.Rotate(MassProperties.CentreOfMass * Vector3d.UnitZ);
        Vector3d spin = attitude.InverseRotate(Body.SpinRate * Vector3d.UnitZ);

        State = new RigidState(position, Body.AirVelocityAt(position), attitude, spin);

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

        // A conic that meets the air hands back there, to be flown through it.
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

        foreach (Engine engine in _engines) {

            engine.Cool(time - Time);

        }

        State = new RigidState(position, velocity, attitude, spin);
        Time = time;
        DynamicPressure = 0.0;

        if (_patch.End == PatchEnd.Atmosphere && time >= _patch.EndTime) {

            _patch = null;

        }

    }

    private void Fly(double dt, Controls controls, Vector3d rotation) {

        MassProperties mass = MassProperties;
        double centre = mass.CentreOfMass;
        double altitude = Altitude;
        Vector3d force = Vector3d.Zero;
        Vector3d torque = Vector3d.Zero;

        foreach (Engine engine in _engines) {

            (double settled, bool fed) = Feed(engine.Segment);

            engine.Update(dt, Throttle(engine, controls.Throttle), Steer(engine, centre, rotation), rotation.Z, fed, settled, Time, PressureAt(altitude));

            double each = engine.Output / engine.Count;

            for (int i = 0; i < engine.Count; i++) {

                Vector3d thrust = engine.Direction(i) * each;

                force += thrust;
                torque += Vector3d.Cross(engine.Mount(i) - centre * Vector3d.UnitZ, thrust);

            }

            DrawPropellant(engine.Segment, engine.MassFlow * dt, engine.MixtureRatio);

        }

        RcsMixer.Allocate(_active, centre, rotation, controls.Translation, _duties);

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

        DynamicPressure = 0.0;

        if (Body.Air is { } air && altitude < air.Top) {

            Vector3d airspeed = State.Attitude.InverseRotate(Airspeed);
            double density = air.DensityAt(altitude);
            (Vector3d drag, Vector3d twist) = Aerodynamics.Forces(airspeed, State.AngularVelocity, density, air.SpeedOfSound, centre, _bottom, _top, _radius);

            force += drag;
            torque += twist;
            DynamicPressure = 0.5 * density * airspeed.LengthSquared;

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

    // The rotation asked of the stack: the pilot's, or with a hold set and the keys let go, what brings it back to the hold.
    private Vector3d Steering(Controls controls) {

        SelectThrusters();

        if (controls.Hold == Hold.Off || controls.Rotation.LengthSquared > 0.0) {

            _holdAttitude = null;

            return controls.Rotation;

        }

        QuaternionD attitude = State.Attitude;
        Vector3d spin = State.AngularVelocity;
        Vector3d error;

        if (controls.Hold == Hold.Prograde) {

            Vector3d motion = Airspeed.Length > ProgradeAirspeed && InAir ? Airspeed : State.Velocity;
            Vector3d along = attitude.InverseRotate(motion.Normalized);
            Vector3d axis = Vector3d.Cross(Vector3d.UnitZ, along);
            double sine = axis.Length;

            // Roll is free about the motion: only its turning is stopped.
            error = sine > 1e-9 ? axis / sine * Math.Atan2(sine, along.Z) : Vector3d.Zero;

        } else {

            _holdAttitude ??= attitude;

            QuaternionD off = attitude.Conjugate * _holdAttitude.Value;

            error = Rotation(off.W < 0.0 ? off * -1.0 : off);

        }

        if (error.Length < RestAngle && spin.Length < RestRate) {

            return Vector3d.Zero;

        }

        Vector3d authority = Authority(MassProperties.CentreOfMass);

        return new Vector3d(Stabilise(error.X, spin.X, authority.X), Stabilise(error.Y, spin.Y, authority.Y), Stabilise(error.Z, spin.Z, authority.Z));

    }

    // The turn rate that would close the error braking at half the authority, and the command that reaches it.
    private static double Stabilise(double error, double spin, double authority) {

        if (authority < 1e-9) {

            return 0.0;

        }

        double magnitude = Math.Abs(error);
        double want = Math.Sign(error) * Math.Min(Math.Min(Math.Sqrt(authority * magnitude), magnitude / AngleSeconds), MaxTurnRate);

        return Math.Clamp((want - spin) / (authority * RateSeconds), -1.0, 1.0);

    }

    // A unit quaternion as the rotation vector it turns by.
    private static Vector3d Rotation(QuaternionD q) {

        Vector3d axis = new Vector3d(q.X, q.Y, q.Z);
        double sine = axis.Length;

        return sine < 1e-12 ? Vector3d.Zero : axis / sine * (2.0 * Math.Atan2(sine, q.W));

    }

    // Angular acceleration a full command gives about each body axis: the gimbals swung through their range at the thrust
    // they carry, and half of every active RCS nozzle that turns that way, as the mixer pairs them.
    private Vector3d Authority(double centre) {

        MassProperties mass = MassProperties;
        double pitch = 0.0;
        double roll = 0.0;

        foreach (Engine engine in _engines) {

            double sine = Math.Sin(engine.GimbalRange);
            double each = engine.Output / engine.Count;

            pitch += engine.Output * sine * Math.Abs(centre - engine.Mount(0).Z);

            foreach (Vector3d nozzle in engine.Nozzles) {

                roll += each * sine * nozzle.Length;

            }

        }

        Vector3d rcs = Vector3d.Zero;

        foreach (Thruster thruster in _active) {

            Vector3d twist = Vector3d.Cross(thruster.Position - centre * Vector3d.UnitZ, thruster.Direction * -thruster.Thrust);

            rcs += new Vector3d(Math.Abs(twist.X), Math.Abs(twist.Y), Math.Abs(twist.Z));

        }

        rcs *= 0.25;

        return new Vector3d((pitch + rcs.X) / mass.Transverse, (pitch + rcs.Y) / mass.Transverse, (roll + rcs.Z) / mass.Axial);

    }

    // Only the lowest stage's engines answer the throttle; those above wait for staging to bring them to the bottom.
    private double Throttle(Engine engine, double throttle) => engine.Segment == _engines[0].Segment ? throttle : 0.0;

    // The gimbal deflection, as a fraction of range, that turns the stack the way the pilot asks.
    private static Vector3d Steer(Engine engine, double centre, Vector3d rotation) {

        double lever = centre - engine.Mount(0).Z;

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

        HasImpacted = Touches(Datum + State.Attitude.Rotate(_bottom * Vector3d.UnitZ)) || Touches(Datum + State.Attitude.Rotate(_top * Vector3d.UnitZ));

    }

    // Whether a point relative to the body lies under its ground or its water.
    private bool Touches(Vector3d point) {

        double height = point.Length - Body.Radius;

        if (Body.Terrain is not { } terrain) {

            return height < 0.0;

        }

        if (height > terrain.Highest) {

            return false;

        }

        Vector3d direction = Body.ToBodyFixed(point, Time) / point.Length;
        double level = terrain.WaterLevelAt(direction, 0.0);
        double ground = terrain.HeightAt(direction, 0.0);

        return height < (double.IsNaN(level) ? ground : Math.Max(ground, level));

    }

}
