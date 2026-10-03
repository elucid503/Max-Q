using System;
using System.Collections.Generic;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Vessels.Parts;
using MaxQ.Sim.Vessels.Propulsion;

using UnityEngine;

namespace MaxQ.Game.Vessels.Craft;

/// <summary>One line of a craft file. <c>part</c> says what it is; catalogue parts name their entry, procedural parts carry
/// their own dimensions. Unused fields are left out of the file.</summary>
[Serializable]
public sealed class PartLine {

    public string part;
    public string name;
    public string entry;

    // Engine: a cluster of count round ringRadius from phase, with one more on the axis when centred; nested hangs it
    // inside the interstage below.
    public bool centred;
    public bool nested;

    // Skirt: its finish (paint by default). Separation ring: open leaves out the deck, for an engine to pass through.
    public string finish;
    public bool open;

    // Tank.
    public string propellant;
    public double barrelLength;
    public double domeRatio;
    public bool flatBottom;
    public double wallArealDensity;

    // Shells and rings.
    public double radius;
    public double bottomRadius;
    public double topRadius;
    public double length;
    public double arealDensity;
    public double mass;
    public double impulse;

    // RCS ring.
    public double offset;
    public double ringRadius;
    public int count;
    public double phase;
    public double hydrazine;

}

/// <summary>A craft as saved: its name and its parts, bottom to top.</summary>
[Serializable]
public sealed class CraftFile {

    public string name;
    public PartLine[] parts;

    // The line each built part came from, for how it is drawn.
    [NonSerialized]
    private Dictionary<Part, PartLine> _lines = new Dictionary<Part, PartLine>();

    public static CraftFile Parse(string json) {

        CraftFile craft = JsonUtility.FromJson<CraftFile>(json);

        craft._lines = new Dictionary<Part, PartLine>();

        return craft;

    }

    /// <summary>The line a part built from this craft came from.</summary>
    public PartLine Line(Part part) => _lines.TryGetValue(part, out PartLine line) ? line : throw new InvalidOperationException($"Part '{part.Name}' is not from craft '{name}'.");

    /// <summary>Fresh sim parts for one vessel.</summary>
    public List<Part> Build(Catalogue catalogue) {

        List<Part> built = new List<Part>();

        _lines.Clear();

        foreach (PartLine line in parts ?? Array.Empty<PartLine>()) {

            Part part = line.part switch {

                "engine" => Engine(line, catalogue.Engine(line.entry)),
                "tank" => Tank(line, catalogue.Propellant(line.propellant)),
                "skirt" => new Skirt { Name = line.name, BottomRadius = line.bottomRadius, TopRadius = line.topRadius, Length = line.length, ArealDensity = line.arealDensity },
                "decoupler" => new Decoupler { Name = line.name, Radius = line.radius, Length = line.length, Mass = line.mass, Impulse = line.impulse },
                "rcs" => Rcs(line, catalogue.Pod(line.entry)),
                "capsule" => Capsule(catalogue.Capsule(line.entry)),
                _ => throw new InvalidOperationException($"Craft '{name}' has a part of unknown kind '{line.part}'."),

            };

            _lines[part] = line;
            built.Add(part);

        }

        return built;

    }

    private static Engine Engine(PartLine line, EngineEntry entry) => new Engine {

        Name = entry.name,
        Nozzles = Nozzles(line),
        Nested = line.nested,
        Thrust = entry.thrust,
        SpecificImpulse = entry.specificImpulse,
        MixtureRatio = entry.mixtureRatio,
        MinimumThrottle = entry.minimumThrottle,
        IgnitionDelaySeconds = entry.ignitionDelaySeconds,
        SpoolUpSeconds = entry.spoolUpSeconds,
        SpoolDownSeconds = entry.spoolDownSeconds,
        GimbalRange = entry.gimbalRange,
        GimbalRate = entry.gimbalRate,
        Mass = entry.mass,
        Length = entry.length,
        ExitRadius = entry.exitRadius,
        CentreOfMassHeight = entry.centreOfMassHeight,
        ExtensionTemperature = entry.extension?.temperature ?? 0.0,
        ExtensionEmissivity = entry.extension?.emissivity ?? 0.0,
        ExtensionHeatCapacity = entry.extension?.heatCapacity ?? 0.0,

    };

    // A single engine on the axis, or a ring of them round it.
    private static Vector3d[] Nozzles(PartLine line) {

        if (line.count <= 0) {

            return new[] { Vector3d.Zero };

        }

        List<Vector3d> nozzles = new List<Vector3d>();

        if (line.centred) {

            nozzles.Add(Vector3d.Zero);

        }

        for (int i = 0; i < line.count; i++) {

            double angle = line.phase + 2.0 * Math.PI * i / line.count;

            nozzles.Add(new Vector3d(line.ringRadius * Math.Cos(angle), line.ringRadius * Math.Sin(angle), 0.0));

        }

        return nozzles.ToArray();

    }

    private static Tank Tank(PartLine line, PropellantEntry propellant) => new Tank {

        Name = line.name,
        Radius = line.radius,
        BarrelLength = line.barrelLength,
        DomeRatio = line.domeRatio,
        FlatBottom = line.flatBottom,
        MixtureRatio = propellant.mixtureRatio,
        OxidiserDensity = propellant.oxidiserDensity,
        FuelDensity = propellant.fuelDensity,
        WallArealDensity = line.wallArealDensity,

    };

    private static RcsBlock Rcs(PartLine line, PodEntry pod) => new RcsBlock {

        Name = pod.name,
        Offset = line.offset,
        RingRadius = line.ringRadius,
        Count = line.count,
        Phase = line.phase,
        PodMass = pod.podMass,
        HydrazineCapacity = line.hydrazine,
        Thrust = pod.thrust,
        SpecificImpulse = pod.specificImpulse,
        Ports = Catalogue.Ports(pod.ports),

    };

    private static Capsule Capsule(CapsuleEntry entry) => new Capsule {

        Name = entry.name,
        Mass = entry.mass,
        Length = entry.length,
        Radius = entry.radius,
        CentreOfMassHeight = entry.centreOfMassHeight,
        HydrazineCapacity = entry.hydrazine,
        Thrust = entry.thrust,
        SpecificImpulse = entry.specificImpulse,
        Ports = Catalogue.Ports(entry.ports),

    };

}
