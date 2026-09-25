using System;

using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Orbits;

/// <summary>An immutable two-body conic, elliptic or hyperbolic, relative to its central body.</summary>
public sealed class Orbit {

    private const double CircularEccentricity = 1e-10;

    public double Mu { get; }
    public double SemiMajorAxis { get; }
    public double Eccentricity { get; }

    /// <summary>Unit vector towards periapsis (towards the epoch position when circular).</summary>
    public Vector3d P { get; }
    public Vector3d Q { get; }
    public Vector3d Normal { get; }

    public double MeanAnomalyAtEpoch { get; }
    public double Epoch { get; }

    private Orbit(double mu, double semiMajorAxis, double eccentricity, Vector3d p, Vector3d q, double meanAnomalyAtEpoch, double epoch) {

        Mu = mu;
        SemiMajorAxis = semiMajorAxis;
        Eccentricity = eccentricity;

        P = p;
        Q = q;
        Normal = Vector3d.Cross(p, q);

        MeanAnomalyAtEpoch = meanAnomalyAtEpoch;
        Epoch = epoch;

    }

    public bool IsClosed => Eccentricity < 1.0;
    public double MeanMotion => Math.Sqrt(Mu / Math.Abs(SemiMajorAxis * SemiMajorAxis * SemiMajorAxis));
    public double Period => IsClosed ? 2.0 * Math.PI / MeanMotion : double.PositiveInfinity;
    public double SemiLatusRectum => SemiMajorAxis * (1.0 - Eccentricity * Eccentricity);
    public double PeriapsisRadius => SemiMajorAxis * (1.0 - Eccentricity);
    public double ApoapsisRadius => IsClosed ? SemiMajorAxis * (1.0 + Eccentricity) : double.PositiveInfinity;
    public double Inclination => Math.Acos(Math.Clamp(Normal.Z, -1.0, 1.0));

    /// <summary>Largest reachable |true anomaly|: pi when closed, the asymptote when not.</summary>
    public double TrueAnomalyLimit => IsClosed ? Math.PI : Math.Acos(-1.0 / Eccentricity);

    public static Orbit FromStateVectors(Vector3d position, Vector3d velocity, double mu, double epoch) {

        Vector3d h = Vector3d.Cross(position, velocity);
        double r = position.Length;

        // ponytail: radial (h = 0) and parabolic (e = 1) trajectories are not handled; universal variables if they show up.
        Vector3d eccentricityVector = Vector3d.Cross(velocity, h) / mu - position / r;
        double e = eccentricityVector.Length;
        double energy = velocity.LengthSquared / 2.0 - mu / r;
        double a = -mu / (2.0 * energy);

        Vector3d w = h.Normalized;
        Vector3d p = e > CircularEccentricity ? eccentricityVector / e : position / r;
        Vector3d q = Vector3d.Cross(w, p);

        double trueAnomaly = Math.Atan2(Vector3d.Dot(position, q), Vector3d.Dot(position, p));

        return new Orbit(mu, a, e, p, q, MeanAnomalyFromTrue(trueAnomaly, e), epoch);

    }

    /// <summary>Builds an orbit from classical elements; angles in radians, periapsis at epoch plus meanAnomaly.</summary>
    public static Orbit FromElements(double mu, double semiMajorAxis, double eccentricity, double inclination, double ascendingNode, double argumentOfPeriapsis, double meanAnomalyAtEpoch, double epoch) {

        double cO = Math.Cos(ascendingNode), sO = Math.Sin(ascendingNode);
        double ci = Math.Cos(inclination), si = Math.Sin(inclination);
        double cw = Math.Cos(argumentOfPeriapsis), sw = Math.Sin(argumentOfPeriapsis);

        Vector3d p = new Vector3d(cO * cw - sO * sw * ci, sO * cw + cO * sw * ci, sw * si);
        Vector3d q = new Vector3d(-cO * sw - sO * cw * ci, -sO * sw + cO * cw * ci, cw * si);

        return new Orbit(mu, semiMajorAxis, eccentricity, p, q, meanAnomalyAtEpoch, epoch);

    }

    public double MeanAnomalyAt(double time) => MeanAnomalyAtEpoch + MeanMotion * (time - Epoch);

    public double TrueAnomalyAt(double time) {

        double m = MeanAnomalyAt(time);
        double e = Eccentricity;

        if (IsClosed) {

            double ea = SolveElliptic(WrapPi(m), e);

            return Math.Atan2(Math.Sqrt(1.0 - e * e) * Math.Sin(ea), Math.Cos(ea) - e);

        }

        double ha = SolveHyperbolic(m, e);

        return Math.Atan2(Math.Sqrt(e * e - 1.0) * Math.Sinh(ha), e - Math.Cosh(ha));

    }

    public double RadiusAtTrueAnomaly(double trueAnomaly) => SemiLatusRectum / (1.0 + Eccentricity * Math.Cos(trueAnomaly));

    public Vector3d PositionAtTrueAnomaly(double trueAnomaly) {

        double r = RadiusAtTrueAnomaly(trueAnomaly);

        return r * Math.Cos(trueAnomaly) * P + r * Math.Sin(trueAnomaly) * Q;

    }

    public (Vector3d Position, Vector3d Velocity) StateAt(double time) {

        double m = MeanAnomalyAt(time);
        double e = Eccentricity;
        double a = Math.Abs(SemiMajorAxis);
        double root = Math.Sqrt(Mu * a);

        if (IsClosed) {

            double ea = SolveElliptic(WrapPi(m), e);
            double cosE = Math.Cos(ea), sinE = Math.Sin(ea);
            double b = Math.Sqrt(1.0 - e * e);
            double r = a * (1.0 - e * cosE);

            Vector3d position = a * (cosE - e) * P + a * b * sinE * Q;
            Vector3d velocity = (-sinE * P + b * cosE * Q) * (root / r);

            return (position, velocity);

        }

        double ha = SolveHyperbolic(m, e);
        double coshH = Math.Cosh(ha), sinhH = Math.Sinh(ha);
        double bh = Math.Sqrt(e * e - 1.0);
        double rh = a * (e * coshH - 1.0);

        Vector3d positionH = a * (e - coshH) * P + a * bh * sinhH * Q;
        Vector3d velocityH = (-sinhH * P + bh * coshH * Q) * (root / rh);

        return (positionH, velocityH);

    }

    /// <summary>First time at or after <paramref name="after"/> the body passes the given true anomaly, or null if it never will.</summary>
    public double? TimeAtTrueAnomaly(double trueAnomaly, double after) {

        if (Math.Abs(trueAnomaly) >= TrueAnomalyLimit && !IsClosed) {

            return null;

        }

        double dm = MeanAnomalyFromTrue(trueAnomaly, Eccentricity) - MeanAnomalyAt(after);

        if (IsClosed) {

            dm %= 2.0 * Math.PI;

            if (dm < 0.0) {

                dm += 2.0 * Math.PI;

            }

        }

        return dm < 0.0 ? null : after + dm / MeanMotion;

    }

    public static double MeanAnomalyFromTrue(double trueAnomaly, double e) {

        if (e < 1.0) {

            double ea = Math.Atan2(Math.Sqrt(1.0 - e * e) * Math.Sin(trueAnomaly), e + Math.Cos(trueAnomaly));

            return ea - e * Math.Sin(ea);

        }

        double ha = Math.Asinh(Math.Sqrt(e * e - 1.0) * Math.Sin(trueAnomaly) / (1.0 + e * Math.Cos(trueAnomaly)));

        return e * Math.Sinh(ha) - ha;

    }

    private static double SolveElliptic(double m, double e) {

        // Starting at pi keeps Newton monotone for high eccentricity.
        double ea = e < 0.8 ? m : Math.PI * Math.Sign(m == 0.0 ? 1.0 : m);

        for (int i = 0; i < 50; i++) {

            double step = (ea - e * Math.Sin(ea) - m) / (1.0 - e * Math.Cos(ea));
            ea -= step;

            if (Math.Abs(step) < 1e-14) {

                break;

            }

        }

        return ea;

    }

    private static double SolveHyperbolic(double m, double e) {

        double ha = Math.Asinh(m / e);

        for (int i = 0; i < 50; i++) {

            double step = (e * Math.Sinh(ha) - ha - m) / (e * Math.Cosh(ha) - 1.0);
            ha -= step;

            if (Math.Abs(step) < 1e-14 * Math.Max(1.0, Math.Abs(ha))) {

                break;

            }

        }

        return ha;

    }

    private static double WrapPi(double angle) {

        angle %= 2.0 * Math.PI;

        if (angle > Math.PI) {

            return angle - 2.0 * Math.PI;

        }

        return angle < -Math.PI ? angle + 2.0 * Math.PI : angle;

    }

}
