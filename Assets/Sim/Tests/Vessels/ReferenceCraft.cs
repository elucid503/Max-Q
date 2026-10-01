using System;
using System.Collections.Generic;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Vessels.Parts;
using MaxQ.Sim.Vessels.Propulsion;

namespace MaxQ.Sim.Tests.Vessels;

/// <summary>The v1 stack in round numbers: a Dragon-sized capsule on a 3.7 m kerolox stage with a vacuum Merlin-class engine.</summary>
public static class ReferenceCraft {

    private const double Radius = 1.85;

    public static List<Part> Build() => new List<Part> {

        Engine(),
        Tank(),
        new Skirt { Name = "Forward skirt", BottomRadius = Radius, TopRadius = Radius, Length = 1.6, ArealDensity = 12.0 },
        StageRcs(),
        new Skirt { Name = "Adapter", BottomRadius = Radius, TopRadius = Radius, Length = 0.5, ArealDensity = 12.0 },
        new Decoupler { Name = "Separation ring", Radius = Radius, Length = 0.15, Mass = 120.0, Impulse = 2_000.0 },
        Capsule(),

    };

    public static Engine Engine() => new Engine {

        Name = "Vacuum engine",
        Thrust = 981_000.0,
        SpecificImpulse = 348.0,
        MixtureRatio = 2.36,
        MinimumThrottle = 0.39,
        IgnitionDelaySeconds = 0.5,
        SpoolUpSeconds = 0.35,
        SpoolDownSeconds = 0.2,
        GimbalRange = 5.0 * Math.PI / 180.0,
        GimbalRate = 10.0 * Math.PI / 180.0,
        Mass = 550.0,
        Length = 4.2,
        ExitRadius = 1.6,
        CentreOfMassHeight = 3.4,

    };

    public static Tank Tank() => new Tank {

        Name = "Tank",
        Radius = Radius,
        BarrelLength = 4.0,
        DomeRatio = 0.7,
        MixtureRatio = 2.36,
        OxidiserDensity = 1_141.0,
        FuelDensity = 820.0,
        WallArealDensity = 14.0,

    };

    public static RcsBlock StageRcs() => new RcsBlock {

        Name = "Stage RCS",
        Offset = -0.6,
        RingRadius = Radius + 0.1,
        Count = 4,
        Phase = 0.25 * Math.PI,
        PodMass = 25.0,
        HydrazineCapacity = 120.0,
        Thrust = 445.0,
        SpecificImpulse = 225.0,
        Ports = Quad(),

    };

    public static Capsule Capsule() {

        List<Port> ports = new List<Port>();

        for (int pod = 0; pod < 4; pod++) {

            double angle = 0.25 * Math.PI + 0.5 * Math.PI * pod;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);

            foreach (Port port in Quad()) {

                Vector3d Turn(Vector3d v) => new Vector3d(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, v.Z);

                ports.Add(new Port(Turn(port.Position) + new Vector3d(1.55 * cos, 1.55 * sin, 2.6), Turn(port.Direction)));

            }

        }

        return new Capsule {

            Name = "Capsule",
            Mass = 7_700.0,
            Length = 4.0,
            Radius = Radius,
            CentreOfMassHeight = 1.3,
            HydrazineCapacity = 400.0,
            Thrust = 400.0,
            SpecificImpulse = 230.0,
            Ports = ports,

        };

    }

    // Four nozzles on a pod: two firing round the ring, two along the stack.
    private static Port[] Quad() => new[] {

        new Port(new Vector3d(0.1, 0.12, 0.0), Vector3d.UnitY),
        new Port(new Vector3d(0.1, -0.12, 0.0), -Vector3d.UnitY),
        new Port(new Vector3d(0.1, 0.0, 0.12), Vector3d.UnitZ),
        new Port(new Vector3d(0.1, 0.0, -0.12), -Vector3d.UnitZ),

    };

}
