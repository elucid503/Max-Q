using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Vessels.Motion;

/// <summary>The air's push on a slender stack of one radius, in the body frame: drag along the axis by a coefficient that
/// climbs through the sound barrier, and across it slender-body lift at the leading end plus cylinder crossflow drag along
/// the length (Jorgensen's split). Each piece of the crossflow sees its own velocity, so a turning stack is damped.</summary>
public static class Aerodynamics {

    // Crossflow drag coefficient of a long cylinder, and the slices its length is summed in.
    private const double Crossflow = 1.2;
    private const int Slices = 8;

    // Axial drag coefficient by Mach number for a blunt-nosed launcher, base drag included: flat subsonic, a peak just past
    // the sound barrier, falling away as the flow goes hypersonic.
    private static readonly double[] Machs = { 0.0, 0.8, 1.0, 1.2, 2.0, 3.0, 5.0, 10.0 };
    private static readonly double[] Coefficients = { 0.30, 0.32, 0.50, 0.58, 0.45, 0.35, 0.28, 0.25 };

    /// <summary>The axial drag coefficient at a Mach number.</summary>
    public static double AxialCoefficient(double mach) {

        if (mach >= Machs[^1]) {

            return Coefficients[^1];

        }

        int i = 1;

        while (Machs[i] < mach) {

            i++;

        }

        double t = (mach - Machs[i - 1]) / (Machs[i] - Machs[i - 1]);

        return Coefficients[i - 1] + (Coefficients[i] - Coefficients[i - 1]) * Math.Max(t, 0.0);

    }

    /// <summary>Force and torque about the centre of mass (station <paramref name="centre"/>) on a stack from station
    /// <paramref name="bottom"/> to <paramref name="top"/> moving at <paramref name="velocity"/> through still air
    /// (body frame) while turning at <paramref name="spin"/>.</summary>
    public static (Vector3d Force, Vector3d Torque) Forces(Vector3d velocity, Vector3d spin, double density, double speedOfSound, double centre,
        double bottom, double top, double radius) {

        double speed = velocity.Length;

        if (density <= 0.0 || speed < 1e-3) {

            return (Vector3d.Zero, Vector3d.Zero);

        }

        double area = Math.PI * radius * radius;
        double length = top - bottom;
        double axial = -0.5 * density * area * AxialCoefficient(speed / speedOfSound) * velocity.Z * Math.Abs(velocity.Z);
        Vector3d force = new Vector3d(0.0, 0.0, axial);
        Vector3d torque = Vector3d.Zero;

        // Slender-body lift, 2 sin a cos a of the dynamic pressure, where the body's section grows: a radius in from
        // whichever end leads.
        double lead = velocity.Z >= 0.0 ? top - radius : bottom + radius;

        Add(-density * area * Math.Abs(Local(lead).Z) * Across(Local(lead)), lead);

        double slice = length / Slices;

        for (int i = 0; i < Slices; i++) {

            double station = bottom + (i + 0.5) * slice;
            Vector3d across = Across(Local(station));

            Add(-0.5 * density * Crossflow * 2.0 * radius * slice * across.Length * across, station);

        }

        return (force, torque);

        Vector3d Local(double station) => velocity + Vector3d.Cross(spin, (station - centre) * Vector3d.UnitZ);

        static Vector3d Across(Vector3d v) => new Vector3d(v.X, v.Y, 0.0);

        void Add(Vector3d push, double station) {

            force += push;
            torque += Vector3d.Cross((station - centre) * Vector3d.UnitZ, push);

        }

    }

}
