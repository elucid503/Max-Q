using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Vessels;

/// <summary>What stability assist holds while the pilot leaves the rotation alone.</summary>
public enum Hold {

    Off,

    /// <summary>The attitude the stack had when the pilot last let go.</summary>
    Attitude,

    /// <summary>Nose along the motion: through the air while in it, along the orbit above it.</summary>
    Prograde,

}

/// <summary>What the pilot asks of the vessel this instant.</summary>
public readonly struct Controls {

    /// <summary>0 shuts the engines down; above 0 lights them and sets thrust, never below their floor.</summary>
    public readonly double Throttle;

    /// <summary>Rotation about body X (pitch), Y (yaw) and Z (roll), each -1 to 1.</summary>
    public readonly Vector3d Rotation;

    /// <summary>Translation along body X, Y and Z (forward, up the stack), each -1 to 1.</summary>
    public readonly Vector3d Translation;

    public readonly Hold Hold;

    public Controls(double throttle, Vector3d rotation, Vector3d translation, Hold hold = Hold.Off) {

        Throttle = throttle;
        Rotation = rotation;
        Translation = translation;
        Hold = hold;

    }

    /// <summary>Nothing asked of the engines or the thrusters; a hold acts only once the stack strays from it.</summary>
    public bool IsIdle => Throttle <= 0.0 && Rotation.LengthSquared == 0.0 && Translation.LengthSquared == 0.0;

}
