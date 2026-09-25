using MaxQ.Sim.Bodies;

namespace MaxQ.Sim.Orbits;

public enum PatchEnd {

    None,
    Escape,
    Encounter,
    Impact,
    Maneuver,

}

/// <summary>One conic segment of a patched-conic trajectory.</summary>
public sealed class Patch {

    public CelestialBody Body { get; init; }
    public Orbit Orbit { get; init; }

    public double StartTime { get; init; }
    public double EndTime { get; init; }

    public PatchEnd End { get; init; }
    public CelestialBody NextBody { get; init; }

    public bool Continues => End == PatchEnd.Escape || End == PatchEnd.Encounter;

}
