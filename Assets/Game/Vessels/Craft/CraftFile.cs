using System;
using System.Collections.Generic;

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

    public static CraftFile Parse(string json) => JsonUtility.FromJson<CraftFile>(json);

    /// <summary>Fresh sim parts for one vessel.</summary>
    public List<Part> Build(Catalogue catalogue) {

        List<Part> built = new List<Part>();

        foreach (PartLine line in parts ?? Array.Empty<PartLine>()) {

            built.Add(line.part switch {

                "engine" => Engine(catalogue.Engine(line.entry)),
                "tank" => Tank(line, catalogue.Propellant(line.propellant)),
                "skirt" => new Skirt { Name = line.name, BottomRadius = line.bottomRadius, TopRadius = line.topRadius, Length = line.length, ArealDensity = line.arealDensity },
                "decoupler" => new Decoupler { Name = line.name, Radius = line.radius, Length = line.length, Mass = line.mass, Impulse = line.impulse },
                "rcs" => Rcs(line, catalogue.Pod(line.entry)),
                "capsule" => Capsule(catalogue.Capsule(line.entry)),
                _ => throw new InvalidOperationException($"Craft '{name}' has a part of unknown kind '{line.part}'."),

            });

        }

        return built;

    }

    private static Engine Engine(EngineEntry entry) => new Engine {

        Name = entry.name,
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

    };

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
