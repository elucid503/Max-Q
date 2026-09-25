using System;
using System.Collections.Generic;

using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;

namespace MaxQ.Sim.Orbits;

/// <summary>Patched-conic prediction: where a conic leaves its sphere of influence and what it becomes next.</summary>
public static class Trajectory {

    private const int EncounterSamples = 400;
    private const double TimeTolerance = 1e-3;

    public static List<Patch> Predict(CelestialBody body, Orbit orbit, double startTime, int maxPatches) {

        List<Patch> patches = new List<Patch>();

        while (patches.Count < maxPatches) {

            Patch patch = NextPatch(body, orbit, startTime);
            patches.Add(patch);

            if (!patch.Continues) {

                break;

            }

            (body, orbit) = Transition(patch);
            startTime = patch.EndTime;

        }

        return patches;

    }

    /// <summary>Predicts the trajectory with an impulsive maneuver applied at its time.</summary>
    public static List<Patch> PredictWithManeuver(CelestialBody body, Orbit orbit, double startTime, Maneuver maneuver, int maxPatches) {

        List<Patch> patches = new List<Patch>();

        while (patches.Count < maxPatches) {

            Patch patch = NextPatch(body, orbit, startTime);

            if (patch.EndTime >= maneuver.Time) {

                patches.Add(new Patch { Body = body, Orbit = orbit, StartTime = startTime, EndTime = maneuver.Time, End = PatchEnd.Maneuver });
                patches.AddRange(Predict(body, maneuver.ApplyTo(orbit), maneuver.Time, maxPatches));

                return patches;

            }

            patches.Add(patch);

            if (!patch.Continues) {

                return patches;

            }

            (body, orbit) = Transition(patch);
            startTime = patch.EndTime;

        }

        return patches;

    }

    public static Patch NextPatch(CelestialBody body, Orbit orbit, double startTime) {

        double endTime = double.PositiveInfinity;
        PatchEnd end = PatchEnd.None;
        CelestialBody next = null;

        double? impact = CrossingTime(orbit, body.Radius, startTime, outbound: false);

        if (impact.HasValue) {

            endTime = impact.Value;
            end = PatchEnd.Impact;

        }

        double? escape = body.Parent == null ? null : CrossingTime(orbit, body.SoiRadius, startTime, outbound: true);

        if (escape.HasValue && escape.Value < endTime) {

            endTime = escape.Value;
            end = PatchEnd.Escape;
            next = body.Parent;

        }

        double searchEnd = Math.Min(endTime, startTime + SearchHorizon(body, orbit));

        foreach (CelestialBody child in body.Children) {

            double? encounter = EncounterTime(orbit, child, startTime, searchEnd);

            if (encounter.HasValue && encounter.Value < endTime) {

                endTime = encounter.Value;
                end = PatchEnd.Encounter;
                next = child;

            }

        }

        return new Patch { Body = body, Orbit = orbit, StartTime = startTime, EndTime = endTime, End = end, NextBody = next };

    }

    /// <summary>The body and conic that take over where a continuing patch ends.</summary>
    public static (CelestialBody Body, Orbit Orbit) Transition(Patch patch) {

        (Vector3d r, Vector3d v) = patch.Orbit.StateAt(patch.EndTime);
        CelestialBody next = patch.NextBody;

        if (patch.End == PatchEnd.Escape) {

            (Vector3d bodyR, Vector3d bodyV) = patch.Body.Orbit.StateAt(patch.EndTime);

            return (next, Orbit.FromStateVectors(r + bodyR, v + bodyV, next.Mu, patch.EndTime));

        }

        (Vector3d childR, Vector3d childV) = next.Orbit.StateAt(patch.EndTime);

        return (next, Orbit.FromStateVectors(r - childR, v - childV, next.Mu, patch.EndTime));

    }

    private static double? CrossingTime(Orbit orbit, double radius, double after, bool outbound) {

        if (orbit.PeriapsisRadius >= radius || orbit.ApoapsisRadius <= radius) {

            return null;

        }

        double cos = (orbit.SemiLatusRectum / radius - 1.0) / orbit.Eccentricity;
        double trueAnomaly = Math.Acos(Math.Clamp(cos, -1.0, 1.0));

        return orbit.TimeAtTrueAnomaly(outbound ? trueAnomaly : -trueAnomaly, after);

    }

    private static double SearchHorizon(CelestialBody body, Orbit orbit) {

        double longestChildPeriod = 0.0;

        foreach (CelestialBody child in body.Children) {

            longestChildPeriod = Math.Max(longestChildPeriod, child.Orbit.Period);

        }

        // ponytail: one revolution ahead only; KSP-style multi-orbit encounter search if players want it.
        return orbit.IsClosed ? orbit.Period : longestChildPeriod;

    }

    private static double? EncounterTime(Orbit orbit, CelestialBody child, double from, double to) {

        if (to <= from) {

            return null;

        }

        double step = Math.Min((to - from) / EncounterSamples, child.Orbit.Period / EncounterSamples);
        double Gap(double t) => Vector3d.Distance(orbit.StateAt(t).Position, child.Orbit.StateAt(t).Position) - child.SoiRadius;

        double t0 = from, g0 = Gap(from);
        double t1 = Math.Min(from + step, to), g1 = Gap(t1);

        while (true) {

            if (g0 > 0.0 && g1 <= 0.0) {

                return Bisect(Gap, t0, t1);

            }

            if (t1 >= to) {

                return null;

            }

            double t2 = Math.Min(t1 + step, to), g2 = Gap(t2);

            // A grazing pass can dip inside and out again between samples.
            if (g0 > 0.0 && g1 < g0 && g1 < g2) {

                double tMin = GoldenMinimum(Gap, t0, t2);

                if (Gap(tMin) <= 0.0) {

                    return Bisect(Gap, t0, tMin);

                }

            }

            (t0, g0, t1, g1) = (t1, g1, t2, g2);

        }

    }

    private static double Bisect(Func<double, double> f, double outside, double inside) {

        while (inside - outside > TimeTolerance) {

            double mid = 0.5 * (outside + inside);

            if (f(mid) > 0.0) {

                outside = mid;

            } else {

                inside = mid;

            }

        }

        return inside;

    }

    private static double GoldenMinimum(Func<double, double> f, double a, double b) {

        const double ratio = 0.618_033_988_749_895;

        while (b - a > TimeTolerance) {

            double c = b - ratio * (b - a);
            double d = a + ratio * (b - a);

            if (f(c) < f(d)) {

                b = d;

            } else {

                a = c;

            }

        }

        return 0.5 * (a + b);

    }

}
