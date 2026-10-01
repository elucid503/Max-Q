using System;
using System.Collections.Generic;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Vessels.Parts;
using MaxQ.Sim.Vessels.Propulsion;

using UnityEngine;

namespace MaxQ.Game.Vessels.Craft;

// The JSON shapes, as JsonUtility reads them: public fields, arrays for lists, SI units, radians.

[Serializable]
public sealed class PortEntry {

    public double[] position;
    public double[] direction;

    public Port ToPort() => new Port(Vector(position), Vector(direction));

    internal static Vector3d Vector(double[] v) => v is { Length: 3 } ? new Vector3d(v[0], v[1], v[2]) : Vector3d.Zero;

}

/// <summary>A model's material redone in one of the vessel's finishes: paint, metal, dark, shield or glass.</summary>
[Serializable]
public sealed class FinishEntry {

    public string material;
    public string finish;

}

/// <summary>How an imported model sits in its part: scale to metres, then the offset of its origin from the part's bottom
/// node, along the stack's own axes (Z up the stack), and a turn about the stack axis. Named nodes can be hidden (a part
/// of the model the stack replaces) and named materials redone.</summary>
[Serializable]
public sealed class ModelFit {

    public string model;
    public double scale = 1.0;
    public double[] offset;
    public double turn;
    public string[] hide;
    public FinishEntry[] finishes;

}

/// <summary>One layer of an exhaust plume, after KSP's Waterfall: an open tube along the nozzle's axis downstream, flared by
/// a linear spread and a bounded one that fills out within its length, shining additively. It fades along its length as
/// a power of what is left of it, brighter where it faces the eye (fresnel) or along its axis (inverted fresnel), tinted
/// from start to end, and broken by noise that streams downstream. Metres, seconds; colours linear.</summary>
[Serializable]
public sealed class PlumeLayer {

    public string name;

    // Shape: where it starts downstream of the exit (negative inside the nozzle), radius there, length, spread per metre
    // downstream, and the extra radius it fills out to.
    public double offset;
    public double radius;
    public double length;
    public double spread;
    public double bounded;

    // Light: the power it fades by along its length, the share of it that fades in from the exit, and its brightness.
    public double falloff = 1.0;
    public double fadeIn;
    public double brightness = 1.0;
    public double[] startTint;
    public double[] endTint;
    public double tintFalloff = 1.0;
    public double fresnel;
    public double fresnelInvert;

    // Flow: noise contrast, tiles round and along, and how fast it streams (tiles a second).
    public double noise;
    public double tilesAround = 1.0;
    public double tilesAlong = 1.0;
    public double speed;

}

/// <summary>One lobe of an engine's exhaust: gas spread about the axis by a Gaussian of the given radius (m) at the exit, widening steadily downstream at the given angle (rad), as bright seen across it at the exit,
/// dimming and cooling by e every reach (m) downstream. Colours linear.</summary>
[Serializable]
public sealed class ExhaustLobe {

    public double radius;
    public double width;
    public double brightness = 1.0;
    public double reach;
    public double[] startTint;
    public double[] endTint;

}

/// <summary>An engine's exhaust in vacuum, one glowing volume of a narrow core and a wide expansion, out to a length (m)
/// past the exit. Upstream of the exit it fills the bell, whose wall run straight meets the axis at its depth (m);
/// downstream it stays within a fan from the lip, opening at the given angle (rad). Noise of
/// the given contrast streams out at a speed (m/s), in tiles across the expansion's width and of a length (m).</summary>
[Serializable]
public sealed class ExhaustPlume {

    public double bellDepth;
    public double length;
    public double fan;
    public ExhaustLobe core;
    public ExhaustLobe expansion;

    public double noise;
    public double tilesAcross = 1.0;
    public double tileLength = 1.0;
    public double speed;

}

/// <summary>The light the exhaust throws inside its own bell: where on the axis (m above the exit), how far it reaches (m),
/// its strength (radiance at a metre, so URP's inverse square lands on the wall right) and colour, all at full chamber.</summary>
[Serializable]
public sealed class ExhaustLight {

    public double height;
    public double range;
    public double intensity;
    public double[] colour;

}

[Serializable]
public sealed class PropellantEntry {

    public string name;
    public double mixtureRatio;
    public double oxidiserDensity;
    public double fuelDensity;

}

[Serializable]
public sealed class EngineEntry {

    public string name;
    public ModelFit fit;
    public double thrust;
    public double specificImpulse;
    public double mixtureRatio;
    public double minimumThrottle;
    public double ignitionDelaySeconds;
    public double spoolUpSeconds;
    public double spoolDownSeconds;
    public double gimbalRange;
    public double gimbalRate;
    public double mass;
    public double length;
    public double exitRadius;
    public double centreOfMassHeight;

    public ExhaustPlume exhaust;
    public ExhaustLight light;

    /// <summary>Where the thrust structure's shroud ends round the engine: a radius that clears its body through the gimbal's
    /// swing, and how far below the gimbal pivot.</summary>
    public double mountRadius;
    public double mountDrop;

}

[Serializable]
public sealed class CapsuleEntry {

    public string name;
    public ModelFit fit;
    public double mass;
    public double length;
    public double radius;
    public double centreOfMassHeight;
    public double hydrazine;
    public double thrust;
    public double specificImpulse;
    public PortEntry[] ports;
    public PlumeLayer[] plume;

}

/// <summary>An RCS pod: a housing on the skin with a bell per port, all drawn from these numbers.</summary>
[Serializable]
public sealed class PodEntry {

    public string name;
    public double podMass;
    public double thrust;
    public double specificImpulse;
    public double housingWidth;
    public double housingHeight;
    public double housingDepth;
    public double throatRadius;
    public double exitRadius;
    public double bellLength;
    public PortEntry[] ports;
    public PlumeLayer[] plume;

}

[Serializable]
internal sealed class CatalogueFile {

    public PropellantEntry[] propellants;
    public EngineEntry[] engines;
    public CapsuleEntry[] capsules;
    public PodEntry[] rcsPods;

}

/// <summary>Every part a craft may name: physical numbers for the sim and how each is drawn.</summary>
public sealed class Catalogue {

    private readonly Dictionary<string, PropellantEntry> _propellants = new Dictionary<string, PropellantEntry>();
    private readonly Dictionary<string, EngineEntry> _engines = new Dictionary<string, EngineEntry>();
    private readonly Dictionary<string, CapsuleEntry> _capsules = new Dictionary<string, CapsuleEntry>();
    private readonly Dictionary<string, PodEntry> _pods = new Dictionary<string, PodEntry>();

    public Catalogue(string json) {

        CatalogueFile file = JsonUtility.FromJson<CatalogueFile>(json);

        Index(file.propellants, _propellants, entry => entry.name);
        Index(file.engines, _engines, entry => entry.name);
        Index(file.capsules, _capsules, entry => entry.name);
        Index(file.rcsPods, _pods, entry => entry.name);

    }

    public PropellantEntry Propellant(string name) => Find(_propellants, name, "propellant");
    public EngineEntry Engine(string name) => Find(_engines, name, "engine");
    public CapsuleEntry Capsule(string name) => Find(_capsules, name, "capsule");
    public PodEntry Pod(string name) => Find(_pods, name, "RCS pod");

    private static void Index<T>(T[] entries, Dictionary<string, T> into, Func<T, string> name) {

        foreach (T entry in entries ?? Array.Empty<T>()) {

            into[name(entry)] = entry;

        }

    }

    private static T Find<T>(Dictionary<string, T> entries, string name, string kind) =>
        entries.TryGetValue(name ?? "", out T entry) ? entry : throw new InvalidOperationException($"The catalogue has no {kind} named '{name}'.");

    internal static Port[] Ports(PortEntry[] entries) => Array.ConvertAll(entries ?? Array.Empty<PortEntry>(), entry => entry.ToPort());

}
