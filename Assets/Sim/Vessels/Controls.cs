using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Vessels;

/// <summary>What the pilot asks of the vessel this instant.</summary>
public readonly struct Controls {

    /// <summary>0 shuts the engines down; above 0 lights them and sets thrust, never below their floor.</summary>
    public readonly double Throttle;

    /// <summary>Rotation about body X (pitch), Y (yaw) and Z (roll), each -1 to 1.</summary>
    public readonly Vector3d Rotation;

    /// <summary>Translation along body X, Y and Z (forward, up the stack), each -1 to 1.</summary>
    public readonly Vector3d Translation;

    public Controls(double throttle, Vector3d rotation, Vector3d translation) {

        Throttle = throttle;
        Rotation = rotation;
        Translation = translation;

    }

    public bool IsIdle => Throttle <= 0.0 && Rotation.LengthSquared == 0.0 && Translation.LengthSquared == 0.0;

}
