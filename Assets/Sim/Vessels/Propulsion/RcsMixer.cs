using System;
using System.Collections.Generic;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Vessels.Propulsion;

/// <summary>Chooses how hard each RCS nozzle works to give a commanded rotation and translation: a bounded least-squares
/// fit of the six-axis demand, so nozzles that would only cross-couple or cancel stay quiet.</summary>
public static class RcsMixer {

    private const int Iterations = 150;

    // Cost of every unit of duty, in units of the demand: it trims nozzles that fire against each other.
    private const double Economy = 0.01;

    private const double CouplingWeight = 5.0;

    /// <summary>Duty 0 to 1 per thruster for a command whose axes each run -1 to 1: rotation about body X, Y, Z, then
    /// translation along them. Full command on one axis asks for every nozzle that can push that way.</summary>
    public static void Allocate(IReadOnlyList<Thruster> thrusters, double centreOfMass, Vector3d rotation, Vector3d translation, double[] duties) {

        int n = thrusters.Count;

        Array.Clear(duties, 0, n);

        if (n == 0 || (rotation.LengthSquared == 0.0 && translation.LengthSquared == 0.0)) {

            return;

        }

        double[,] a = new double[6, n];
        double[] reach = new double[6];

        for (int j = 0; j < n; j++) {

            Thruster thruster = thrusters[j];
            Vector3d force = thruster.Direction * -thruster.Thrust;
            Vector3d torque = Vector3d.Cross(thruster.Position - centreOfMass * Vector3d.UnitZ, force);

            a[0, j] = torque.X;
            a[1, j] = torque.Y;
            a[2, j] = torque.Z;
            a[3, j] = force.X;
            a[4, j] = force.Y;
            a[5, j] = force.Z;

            for (int k = 0; k < 6; k++) {

                reach[k] += 0.5 * Math.Abs(a[k, j]);

            }

        }

        double[] command = { rotation.X, rotation.Y, rotation.Z, translation.X, translation.Y, translation.Z };
        double lipschitz = 0.0;

        // Each axis is scaled by what the nozzles can do about it, so torques and forces weigh alike; an axis the pilot
        // left alone weighs more, so falling short on a command beats disturbing the rest.
        for (int k = 0; k < 6; k++) {

            double weight = command[k] == 0.0 ? CouplingWeight : 1.0;
            double scale = reach[k] > 1e-9 ? weight / reach[k] : 0.0;

            for (int j = 0; j < n; j++) {

                a[k, j] *= scale;
                lipschitz += a[k, j] * a[k, j];

            }

            command[k] = scale > 0.0 ? Math.Clamp(command[k], -1.0, 1.0) : 0.0;

        }

        if (lipschitz <= 0.0) {

            return;

        }

        // Accelerated projected gradient on 1/2 |A x - b|^2 + economy * sum(x), 0 <= x <= 1.
        double[] y = new double[n];
        double[] previous = new double[n];
        double[] residual = new double[6];
        double momentum = 1.0;

        for (int iteration = 0; iteration < Iterations; iteration++) {

            for (int k = 0; k < 6; k++) {

                double sum = -command[k];

                for (int j = 0; j < n; j++) {

                    sum += a[k, j] * y[j];

                }

                residual[k] = sum;

            }

            double nextMomentum = 0.5 * (1.0 + Math.Sqrt(1.0 + 4.0 * momentum * momentum));
            double carry = (momentum - 1.0) / nextMomentum;

            for (int j = 0; j < n; j++) {

                double gradient = Economy;

                for (int k = 0; k < 6; k++) {

                    gradient += a[k, j] * residual[k];

                }

                double x = Math.Clamp(y[j] - gradient / lipschitz, 0.0, 1.0);

                y[j] = Math.Clamp(x + carry * (x - previous[j]), 0.0, 1.0);
                previous[j] = x;

            }

            momentum = nextMomentum;

        }

        Array.Copy(previous, duties, n);

    }

}
