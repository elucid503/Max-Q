using System;
using System.Collections.Generic;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Vessels.Motion;
using MaxQ.Sim.Vessels.Parts;

namespace MaxQ.Sim.Vessels.Propulsion;

public enum EnginePhase {

    Off,
    Starting,
    Running,
    Stopping,

}

/// <summary>A gimballed bipropellant engine, or a cluster of identical ones lit together, hung from the tank above it: the
/// bottom node is the nozzle exits, the top node the gimbal pivots. It throttles between a floor and full thrust, takes time
/// to light and to spool, and will only light on propellant settled over the outlets. The air pushes back on each exit, and
/// a cluster rolls the stack by swinging its outer engines round the axis.</summary>
public sealed class Engine : Part {

    public const double StandardGravity = 9.80665;

    /// <summary>Settledness below which ignition fails; between it and fully settled the chamber chugs on ingested gas.</summary>
    public const double IgnitionSettled = 0.6;

    // Chamber fraction below which a stopping engine counts as out.
    private const double Extinguished = 0.005;

    /// <summary>After a failed start, the engine tries again this long later while the throttle stays open.</summary>
    public const double RetrySeconds = 1.0;

    // W/m^2/K^4.
    private const double StefanBoltzmann = 5.670_374e-8;

    private static readonly Vector3d[] Single = { Vector3d.Zero };

    private double _phaseSeconds;
    private Vector3d[] _deflections;

    /// <summary>Where each engine of the cluster stands off the stack axis, body X and Y, m; one on the axis by default.</summary>
    public IReadOnlyList<Vector3d> Nozzles { get; init; } = Single;

    /// <summary>Hung inside an interstage below: the engine takes no height in the stack, its pivots at the part below's top.</summary>
    public bool Nested { get; init; }

    /// <summary>Vacuum thrust of each engine at full throttle, N.</summary>
    public double Thrust { get; init; }

    /// <summary>Vacuum specific impulse, s.</summary>
    public double SpecificImpulse { get; init; }

    /// <summary>Oxidiser over fuel by mass.</summary>
    public double MixtureRatio { get; init; }

    /// <summary>Lowest stable chamber fraction while lit.</summary>
    public double MinimumThrottle { get; init; }

    public double IgnitionDelaySeconds { get; init; }

    /// <summary>Time constants of the chamber following its demand up and down.</summary>
    public double SpoolUpSeconds { get; init; }
    public double SpoolDownSeconds { get; init; }

    /// <summary>Largest deflection off the stack axis, radians.</summary>
    public double GimbalRange { get; init; }
    public double GimbalRate { get; init; }

    /// <summary>Of each engine.</summary>
    public double Mass { get; init; }
    public double Length { get; init; }
    public double ExitRadius { get; init; }
    public double CentreOfMassHeight { get; init; }

    /// <summary>Hottest temperature of the radiatively cooled nozzle extension at full chamber, K; no extension without a
    /// heat capacity.</summary>
    public double ExtensionTemperature { get; init; }

    /// <summary>The extension's emissivity, and its heat capacity per area, J/m^2/K.</summary>
    public double ExtensionEmissivity { get; init; }
    public double ExtensionHeatCapacity { get; init; }

    public override double Height => Nested ? 0.0 : Length;

    public int Count => Nozzles.Count;

    internal override double OuterRadius {

        get {

            double reach = 0.0;

            foreach (Vector3d nozzle in Nozzles) {

                reach = Math.Max(reach, nozzle.Length + ExitRadius);

            }

            return reach;

        }

    }

    public EnginePhase Phase { get; private set; }

    /// <summary>Chamber pressure as a fraction of full: what the plume and the sound follow.</summary>
    public double Chamber { get; private set; }

    /// <summary>The extension's hottest temperature now, K: heated by the gas in step with the chamber, radiating to the
    /// dark (the sun's warmth is left out; it glows at neither).</summary>
    public double NozzleTemperature { get; private set; }

    /// <summary>Thrust the whole cluster delivered over the last step, N.</summary>
    public double Output { get; private set; }

    /// <summary>Propellant the cluster burns, kg/s: the flow its chambers pass, whatever the air does to its thrust.</summary>
    public double MassFlow { get; private set; }

    /// <summary>Pressure of the air round the exits over the last step, Pa.</summary>
    public double AmbientPressure { get; private set; }

    /// <summary>Each engine's deflection as tangents of its nozzle's lean towards body X and Y.</summary>
    public IReadOnlyList<Vector3d> Deflections => _deflections ??= new Vector3d[Count];

    /// <summary>The last start died for want of settled propellant; cleared once one lights.</summary>
    public bool StartFailed { get; private set; }

    /// <summary>The engine ran its tanks dry.</summary>
    public bool Flameout { get; private set; }

    public double ExitArea => Math.PI * ExitRadius * ExitRadius;

    /// <summary>Engine <paramref name="i"/>'s gimbal pivot, body frame.</summary>
    public Vector3d Mount(int i) => Nozzles[i] + (Station + Length) * Vector3d.UnitZ;

    /// <summary>Unit vector engine <paramref name="i"/>'s thrust pushes along, body frame; its plume leaves the opposite way.</summary>
    public Vector3d Direction(int i) => new Vector3d(Deflections[i].X, Deflections[i].Y, 1.0).Normalized;

    /// <summary>Runs the engine for one step: <paramref name="throttle"/> 0 to 1 (0 shuts it down, anything above lights
    /// it), <paramref name="steer"/> the wanted deflection as a fraction of full range, <paramref name="roll"/> -1 to 1
    /// the outer engines' swing round the axis that rolls the stack positively about body Z.</summary>
    internal void Update(double dt, double throttle, Vector3d steer, double roll, bool fed, double settled, double time, double ambientPressure) {

        _phaseSeconds += dt;

        switch (Phase) {

            case EnginePhase.Off:

                if (throttle > 0.0 && fed && (!StartFailed || _phaseSeconds >= RetrySeconds)) {

                    Enter(EnginePhase.Starting);
                    Flameout = false;

                }

                break;

            case EnginePhase.Starting:

                if (throttle <= 0.0) {

                    Enter(EnginePhase.Off);

                } else if (_phaseSeconds >= IgnitionDelaySeconds) {

                    if (settled < IgnitionSettled) {

                        StartFailed = true;
                        Enter(EnginePhase.Off);

                    } else {

                        StartFailed = false;
                        Enter(EnginePhase.Running);

                    }

                }

                break;

            case EnginePhase.Running:

                if (!fed) {

                    Flameout = true;
                    Enter(EnginePhase.Stopping);

                } else if (throttle <= 0.0) {

                    Enter(EnginePhase.Stopping);

                }

                break;

            case EnginePhase.Stopping:

                if (Chamber < Extinguished) {

                    Chamber = 0.0;
                    Enter(EnginePhase.Off);

                }

                break;

        }

        double demand = Phase == EnginePhase.Running ? Math.Max(MinimumThrottle, Math.Min(throttle, 1.0)) : 0.0;
        double seconds = demand > Chamber ? SpoolUpSeconds : SpoolDownSeconds;

        Chamber += (demand - Chamber) * (1.0 - Math.Exp(-dt / Math.Max(seconds, 1e-6)));

        // Gas reaching the turbopumps makes the chamber chug until the engine's own thrust settles the tanks.
        double vacuum = fed ? Chamber * Thrust * (1.0 - (1.0 - Math.Min(settled, 1.0)) * Chug(time)) : 0.0;

        // The air pushes back on each exit: what a nozzle loses at sea level against its vacuum thrust.
        AmbientPressure = ambientPressure;
        MassFlow = Count * vacuum / (SpecificImpulse * StandardGravity);
        Output = vacuum > 0.0 ? Count * Math.Max(vacuum - ambientPressure * ExitArea, 0.0) : 0.0;

        bool running = Phase == EnginePhase.Running;

        for (int i = 0; i < Count; i++) {

            Vector3d nozzle = Nozzles[i];
            double across = nozzle.Length;
            Vector3d round = across > 1e-6 ? new Vector3d(-nozzle.Y, nozzle.X, 0.0) / across : Vector3d.Zero;

            Steer(i, dt, running ? steer + round * roll : Vector3d.Zero);

        }

        Radiate(dt);

    }

    /// <summary>Lets the extension cool over a coast, radiating to the dark: 1 / T^3 grows by 3 times the rate a second.</summary>
    internal void Cool(double seconds) {

        if (ExtensionHeatCapacity <= 0.0 || NozzleTemperature <= 0.0) {

            return;

        }

        NozzleTemperature = Math.Pow(Math.Pow(NozzleTemperature, -3.0) + 3.0 * RadiationRate * seconds, -1.0 / 3.0);

    }

    internal override MassProperties AddTo(MassProperties sum) {

        double radius = 0.5 * ExitRadius;
        double axial = 0.5 * Mass * radius * radius;
        double transverse = Mass * (3.0 * radius * radius + Length * Length) / 12.0;

        // An engine off the axis counts as its share of a ring round it, as a symmetric cluster sums to.
        foreach (Vector3d nozzle in Nozzles) {

            double across = nozzle.LengthSquared;

            sum = sum.Add(Mass, Station + CentreOfMassHeight, axial + Mass * across, transverse + 0.5 * Mass * across);

        }

        return sum;

    }

    private double RadiationRate => ExtensionEmissivity * StefanBoltzmann / ExtensionHeatCapacity;

    // The gas heats the extension in step with the chamber, to its full temperature at full chamber, and it radiates as a
    // grey body.
    private void Radiate(double dt) {

        if (ExtensionHeatCapacity <= 0.0) {

            return;

        }

        double full = ExtensionTemperature * ExtensionTemperature;
        double now = NozzleTemperature * NozzleTemperature;

        NozzleTemperature = Math.Max(NozzleTemperature + dt * RadiationRate * (Chamber * full * full - now * now), 0.0);

    }

    private void Enter(EnginePhase phase) {

        Phase = phase;
        _phaseSeconds = 0.0;

    }

    private void Steer(int i, double dt, Vector3d steer) {

        Vector3d[] deflections = _deflections ??= new Vector3d[Count];
        double range = Math.Tan(GimbalRange);
        Vector3d target = new Vector3d(steer.X, steer.Y, 0.0) * range;

        if (target.Length > range) {

            target = target.Normalized * range;

        }

        Vector3d move = target - deflections[i];
        double reach = Math.Tan(GimbalRate * dt);

        deflections[i] = move.Length <= reach ? target : deflections[i] + move.Normalized * reach;

    }

    // White noise in [0, 1) that changes every step.
    private static double Chug(double time) {

        double n = Math.Sin(time * 12_345.678) * 43_758.5453;

        return n - Math.Floor(n);

    }

}
