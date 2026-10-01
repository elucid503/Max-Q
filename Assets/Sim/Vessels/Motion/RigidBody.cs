using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Vessels.Motion;

/// <summary>Where a rigid body's centre of mass is relative to its central body, and how it is turned and turning.</summary>
public readonly struct RigidState {

    public readonly Vector3d Position;
    public readonly Vector3d Velocity;

    /// <summary>Body frame to sim frame.</summary>
    public readonly QuaternionD Attitude;

    /// <summary>Angular velocity in the body frame, rad/s.</summary>
    public readonly Vector3d AngularVelocity;

    public RigidState(Vector3d position, Vector3d velocity, QuaternionD attitude, Vector3d angularVelocity) {

        Position = position;
        Velocity = velocity;
        Attitude = attitude;
        AngularVelocity = angularVelocity;

    }

}

/// <summary>Motion of an axisymmetric rigid body (body Z the symmetry axis) under point-mass gravity.</summary>
public static class RigidBody {

    /// <summary>One RK4 step with a force and torque held fixed in the body frame.</summary>
    public static RigidState Step(RigidState s, double mu, double mass, double axial, double transverse, Vector3d force, Vector3d torque, double dt) {

        (Vector3d dr1, Vector3d dv1, QuaternionD dq1, Vector3d dw1) = Rates(s);
        (Vector3d dr2, Vector3d dv2, QuaternionD dq2, Vector3d dw2) = Rates(Offset(s, dr1, dv1, dq1, dw1, 0.5 * dt));
        (Vector3d dr3, Vector3d dv3, QuaternionD dq3, Vector3d dw3) = Rates(Offset(s, dr2, dv2, dq2, dw2, 0.5 * dt));
        (Vector3d dr4, Vector3d dv4, QuaternionD dq4, Vector3d dw4) = Rates(Offset(s, dr3, dv3, dq3, dw3, dt));

        double h = dt / 6.0;

        return new RigidState(

            s.Position + h * (dr1 + 2.0 * dr2 + 2.0 * dr3 + dr4),
            s.Velocity + h * (dv1 + 2.0 * dv2 + 2.0 * dv3 + dv4),
            (s.Attitude + (dq1 + dq2 * 2.0 + dq3 * 2.0 + dq4) * h).Normalized,
            s.AngularVelocity + h * (dw1 + 2.0 * dw2 + 2.0 * dw3 + dw4)

        );

        (Vector3d, Vector3d, QuaternionD, Vector3d) Rates(RigidState x) {

            double r = x.Position.Length;
            Vector3d gravity = x.Position * (-mu / (r * r * r));
            Vector3d w = x.AngularVelocity;

            // Euler's equations for I = diag(transverse, transverse, axial).
            Vector3d spin = new Vector3d(

                (torque.X - (axial - transverse) * w.Y * w.Z) / transverse,
                (torque.Y + (axial - transverse) * w.Z * w.X) / transverse,
                torque.Z / axial

            );

            QuaternionD turn = x.Attitude * new QuaternionD(0.0, w.X, w.Y, w.Z) * 0.5;

            return (x.Velocity, gravity + x.Attitude.Rotate(force) / mass, turn, spin);

        }

    }

    /// <summary>Torque-free rotation over any interval, exactly: the body spins about its axis while that axis cones about the
    /// fixed angular momentum.</summary>
    public static (QuaternionD Attitude, Vector3d AngularVelocity) Coast(QuaternionD attitude, Vector3d angularVelocity, double axial, double transverse, double dt) {

        Vector3d w = angularVelocity;
        Vector3d momentum = attitude.Rotate(new Vector3d(transverse * w.X, transverse * w.Y, axial * w.Z));
        double magnitude = momentum.Length;

        if (magnitude < 1e-300) {

            return (attitude, angularVelocity);

        }

        Vector3d axis = momentum / magnitude;
        double precession = magnitude / transverse;
        double spin = w.Z * (transverse - axial) / transverse;

        QuaternionD turned = (QuaternionD.AxisAngle(axis, precession * dt) * attitude * QuaternionD.AxisAngle(Vector3d.UnitZ, spin * dt)).Normalized;

        return (turned, precession * turned.InverseRotate(axis) + spin * Vector3d.UnitZ);

    }

    private static RigidState Offset(RigidState s, Vector3d dr, Vector3d dv, QuaternionD dq, Vector3d dw, double h) =>
        new RigidState(s.Position + h * dr, s.Velocity + h * dv, (s.Attitude + dq * h).Normalized, s.AngularVelocity + h * dw);

}
