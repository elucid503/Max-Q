using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

using MaxQ.Game.Map;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Vessels;

using UnityEngine;

namespace MaxQ.Game.Diagnostics;

/// <summary>Scripted screenshots for headless review: run the player with <c>-capture &lt;dir&gt;</c>, optionally
/// <c>-only &lt;text&gt;[,&lt;text&gt;...]</c> to keep just the shots whose names contain one of them. Each shot waits
/// for the ground to finish streaming, then logs GPU time to timings.txt beside the images: the whole frame, and what
/// each part in <see cref="Parts"/> costs. Add a shot or a part with one line in its table.</summary>
public sealed class Capture : MonoBehaviour {

    private const int SettleFrameLimit = 900;
    private const int TimedFrames = 60;
    private const int AdaptFrames = 240;

    // Where the free camera hovers (metres above the ground), where it looks (degrees), and the local solar time (hours);
    // over Selene where the name starts with "selene-".
    private static readonly (string Name, double Latitude, double Longitude, double Altitude, double Heading, double Pitch, double SolarHour)[] Shots = {

        ("terra-5000km", 5.0, 20.0, 5_000_000.0, 0.0, -90.0, 11.0),
        ("coasts-1500km", 57.0, 10.0, 1_500_000.0, 0.0, -80.0, 12.0),
        ("finnish-lakes-60km", 61.8, 27.5, 60_000.0, 0.0, -70.0, 12.0),
        ("lake-geneva-3km", 46.38, 6.35, 3_000.0, 80.0, -25.0, 11.0),
        ("mediterranean-400km", 36.0, 12.0, 400_000.0, 300.0, -38.0, 10.0),
        ("limb-sunset-400km", 0.0, -20.0, 400_000.0, 270.0, -30.0, 17.6),
        ("dusk-sunward-150km", 0.0, 60.0, 150_000.0, 270.0, -33.0, 19.1),
        ("himalaya-30km", 28.2, 84.0, 30_000.0, 95.0, -10.0, 9.0),
        ("everest-2km", 27.75, 86.85, 2_000.0, 15.0, -6.0, 8.5),
        ("sahara-2km", 25.3, 8.9, 2_000.0, 210.0, -18.0, 15.0),
        ("big-sur-100m", 36.1, -121.62, 100.0, 140.0, -4.0, 16.0),
        ("alps-2m", 46.58, 7.91, 2.0, 170.0, 6.0, 10.0),
        ("grand-canyon-sunset-2m", 36.06, -112.11, 2.0, 280.0, 2.0, 17.7),
        ("himalaya-dusk-30km", 28.0, 86.5, 30_000.0, 60.0, -12.0, 17.3),
        ("alps-evening-2m", 46.58, 7.91, 2.0, 60.0, 4.0, 17.2),
        ("sea-glitter-300m", 36.0, -122.2, 300.0, 255.0, -10.0, 16.3),
        ("himalaya-shafts-3km", 27.9, 86.9, 3_000.0, 265.0, 1.0, 17.8),
        ("eiger-scree-2m", 46.565, 8.0, 2.0, 200.0, -12.0, 11.0),
        ("pad-ground-2m", 46.6, 7.93, 1.8, 120.0, -35.0, 15.0),
        ("coast-dusk-50m", 36.3, -121.9, 50.0, 250.0, 2.0, 18.35),
        ("black-forest-2m", 48.3, 8.2, 2.0, 100.0, -2.0, 10.5),
        ("meadow-2m", 46.93, 7.72, 2.0, 200.0, -5.0, 14.0),
        ("boreal-300m", 62.5, 26.5, 300.0, 40.0, -12.0, 13.0),
        ("forest-canopy-60m", 48.3, 8.2, 60.0, 100.0, -15.0, 10.5),
        ("forest-reach-400m", 48.3, 8.2, 400.0, 280.0, -6.0, 10.5),
        ("amazon-1km", -3.0, -60.0, 1_000.0, 70.0, -15.0, 9.0),
        ("surf-beach-30m", 21.66, -158.06, 30.0, 330.0, -6.0, 15.0),
        ("southern-storm-40m", -52.0, 100.0, 40.0, 60.0, -4.0, 12.0),
        ("lagoon-reef-200m", -18.93, -159.78, 200.0, 350.0, -20.0, 12.0),
        ("lake-dawn-20m", 46.43, 6.62, 20.0, 115.0, -2.0, 6.4),
        ("glint-orbit-400km", 5.0, -35.0, 400_000.0, 90.0, -60.0, 12.0),
        ("ice-edge-2km", -63.0, -45.0, 2_000.0, 180.0, -12.0, 13.0),
        ("fjord-100m", 61.13, 6.4, 100.0, 90.0, -6.0, 13.0),
        ("lake-shore-30m", 46.508, 6.487, 30.0, 170.0, -12.0, 11.0),
        ("dune-beach-40m", 52.37, 4.535, 40.0, 250.0, -12.0, 15.0),
        ("canyon-walls-400m", 36.08, -112.12, 400.0, 20.0, -12.0, 10.0),
        ("meadow-tiling-20m", 46.93, 7.72, 20.0, 200.0, -25.0, 14.0),
        ("cumulus-nigeria-1km", 10.9, 7.6, 1_000.0, 0.0, 8.0, 13.0),
        ("cumulonimbus-maine-3km", 43.0, -71.0, 3_000.0, 60.0, 6.0, 15.0),
        ("stratocumulus-above-6km", 49.6, -131.6, 6_000.0, 90.0, -15.0, 10.0),
        ("cyclone-orbit-400km", -50.0, 110.0, 400_000.0, 0.0, -60.0, 12.0),
        ("terra-far-70000km", 20.0, 0.0, 70_000_000.0, 0.0, -90.0, 15.0),
        ("selene-terra-night-2m", 25.0, 0.0, 2.0, 180.0, 65.0, 2.0),
        ("selene-terra-day-2m", 25.0, 0.0, 2.0, 180.0, 65.0, 9.0),

    };

    // What the pilot does before a vessel shot: nothing, settle the propellant on the RCS and light the engine, pulse the
    // RCS, or stage and back the capsule away.
    private enum Act {

        Coast,
        Burn,
        Pulse,
        Separate,

    }

    // Vessels 150 km up: where over Terra and which way they travel (degrees), the local solar time (hours), where the chase
    // camera sits (degrees round from the motion and down, metres off), and what the pilot does first.
    private static readonly (string Name, double Latitude, double Longitude, double Azimuth, double SolarHour, double Heading, double Pitch, double Distance, Act Act)[]
        VesselShots = {

        ("vessel-coast-45m", 31.0, 24.0, 55.0, 10.5, 200.0, 12.0, 45.0, Act.Coast),
        ("vessel-above-70m", 31.0, 24.0, 55.0, 10.5, 120.0, 55.0, 70.0, Act.Coast),
        ("vessel-burn-60m", 31.0, 24.0, 55.0, 10.5, 95.0, 8.0, 60.0, Act.Burn),
        ("vessel-burn-bell-40m", 31.0, 24.0, 55.0, 10.5, 20.0, 12.0, 40.0, Act.Burn),
        ("vessel-burn-sunward-80m", 10.0, -30.0, 90.0, 16.5, 180.0, 5.0, 80.0, Act.Burn),
        ("vessel-night-burn-60m", 0.0, 60.0, 90.0, 0.5, 160.0, 10.0, 60.0, Act.Burn),
        ("vessel-night-side-40m", 0.0, 60.0, 90.0, 0.5, 90.0, 5.0, 40.0, Act.Burn),
        ("vessel-rcs-25m", 31.0, 24.0, 55.0, 10.5, 230.0, 10.0, 25.0, Act.Pulse),
        ("vessel-separation-40m", 31.0, 24.0, 55.0, 10.5, 110.0, 10.0, 40.0, Act.Separate),
        ("vessel-terminator-50m", 20.0, 100.0, 90.0, 18.4, 200.0, 10.0, 50.0, Act.Coast),
        ("vessel-dusk-sunward-50m", 0.0, 60.0, 90.0, 19.1, 180.0, 15.0, 50.0, Act.Coast),

    };

    // A part's cost is the GPU time the frame saves while it is hidden.
    private static readonly (string Name, Action<MapView, bool> Show)[] Parts = {

        ("ground", (view, shown) => view.Ground.Hidden = !shown),
        ("plants and rocks", (view, shown) => view.Ground.StrewHidden = !shown),
        ("atmosphere", (view, shown) => view.Atmosphere.Enabled = shown),
        ("clouds", (view, shown) => view.Clouds.Enabled = shown),
        ("shadows", (view, shown) => view.Sun.Shadows = shown),
        ("water", (view, shown) => view.Water.Hidden = !shown),

    };

    private string _directory;
    private string[] _only;
    private float _settleSeconds;
    private readonly List<string> _timings = new List<string>();
    private readonly FrameTiming[] _frameTimings = new FrameTiming[1];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot() {

        string[] args = Environment.GetCommandLineArgs();
        string directory = Argument(args, "-capture");

        if (directory == null) {

            return;

        }

        // Frames run unthrottled, so the GPU holds its clocks and the timings compare run to run.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        Capture capture = new GameObject("Capture").AddComponent<Capture>();
        capture._directory = directory;
        capture._only = Argument(args, "-only")?.Split(',', StringSplitOptions.RemoveEmptyEntries);

    }

    private static string Argument(string[] args, string name) {

        int index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;

    }

    private IEnumerator Start() {

        Directory.CreateDirectory(_directory);
        MapView view = FindAnyObjectByType<MapView>();

        foreach ((string name, double latitude, double longitude, double altitude, double heading, double pitch, double solarHour) in Shots) {

            if (_only != null && !Array.Exists(_only, name.Contains)) {

                continue;

            }

            Action look = name.StartsWith("selene-") ? () => view.LookFromSelene(latitude, longitude, altitude, heading, pitch, solarHour) :
                () => view.Look(latitude, longitude, altitude, heading, pitch, solarHour);

            look();

            yield return Settle(view);

            yield return Save(view, name, look);

        }

        foreach ((string name, double latitude, double longitude, double azimuth, double solarHour, double heading, double pitch, double distance, Act act) in VesselShots) {

            if (_only != null && !Array.Exists(_only, name.Contains)) {

                continue;

            }

            void Fly() => view.Fly(latitude, longitude, azimuth, solarHour, heading, pitch, distance);

            Fly();

            yield return Settle(view);

            double[] gpu = new double[1];

            yield return MeasureGpu(gpu);
            _timings.Add($"{name}: frame {gpu[0]:F2} ms GPU; settled in {_settleSeconds:F1} s");

            // From the start again, so the act plays out the same however long the timing took.
            Fly();

            yield return Perform(view, act);

            ScreenCapture.CaptureScreenshot(Path.Combine(_directory, name + ".png"));

            yield return null;
            yield return null;

            view.ScriptedControls = null;

        }

        File.WriteAllLines(Path.Combine(_directory, "timings.txt"), _timings);
        Application.Quit();

    }

    private static IEnumerator Perform(MapView view, Act act) {

        switch (act) {

            case Act.Burn:

                view.ScriptedControls = new Controls(0.0, Vector3d.Zero, Vector3d.UnitZ);

                yield return Wait(4.0f);

                view.ScriptedControls = new Controls(1.0, Vector3d.Zero, Vector3d.Zero);

                // Long enough for a radiatively cooled extension to reach its glow.
                yield return Wait(25.0f);

                break;

            case Act.Pulse:

                view.ScriptedControls = new Controls(0.0, new Vector3d(1.0, 0.0, 0.6), new Vector3d(0.4, 0.0, 0.0));

                yield return Wait(1.0f);

                break;

            case Act.Separate:

                yield return Wait(0.5f);

                view.Stage();
                view.ScriptedControls = new Controls(0.0, Vector3d.Zero, Vector3d.UnitZ);

                yield return Wait(2.5f);

                break;

            default:

                yield return Wait(0.5f);

                break;

        }

    }

    private static IEnumerator Wait(float seconds) {

        float end = Time.realtimeSinceStartup + seconds;

        while (Time.realtimeSinceStartup < end) {

            yield return null;

        }

    }

    // Mean GPU time over TimedFrames frames, after two frames for a change to take effect.
    private IEnumerator MeasureGpu(double[] result) {

        yield return null;
        yield return null;

        double sum = 0.0;
        int timed = 0;

        for (int i = 0; i < TimedFrames; i++) {

            yield return null;

            FrameTimingManager.CaptureFrameTimings();

            if (FrameTimingManager.GetLatestTimings(1, _frameTimings) == 1 && _frameTimings[0].gpuFrameTime > 0.0) {

                sum += _frameTimings[0].gpuFrameTime;
                timed++;

            }

        }

        result[0] = timed > 0 ? sum / timed : double.NaN;

    }

    private IEnumerator Settle(MapView view) {

        float start = Time.realtimeSinceStartup;

        for (int i = 0; i < 4; i++) {

            yield return null;

        }

        for (int i = 0; i < SettleFrameLimit && view.Ground.Pending > 0; i++) {

            yield return null;

        }

        _settleSeconds = Time.realtimeSinceStartup - start;

    }

    // Retakes the shot's place and time just before the screenshot, so the sun, sky and sea are the same run to run.
    private IEnumerator Save(MapView view, string name, Action retake) {

        double[] gpu = new double[1];

        yield return MeasureGpu(gpu);
        double frame = gpu[0];
        List<string> costs = new List<string> { $"frame {frame:F2} ms GPU" };

        foreach ((string part, Action<MapView, bool> show) in Parts) {

            show(view, false);
            yield return MeasureGpu(gpu);
            show(view, true);

            costs.Add($"{part} {frame - gpu[0]:F2} ms");

        }

        // The eye readapts to the whole view after the timings hid parts of it.
        for (int i = 0; i < AdaptFrames; i++) {

            yield return null;

        }

        _timings.Add($"{name}: {string.Join(", ", costs)}; " +
            $"{view.Ground.ShownCount} patches drawn, {view.Ground.PatchCount} built, {view.Ground.Pending} pending, settled in {_settleSeconds:F1} s; exposure {Math.Log(view.Atmosphere.Exposure, 2.0):+0.0;-0.0} EV; " +
            $"sea {view.Water.SeaHeight:F2} m, drawn {view.Water.DrawnHeight:F2} m");

        retake();

        yield return null;
        yield return null;

        ScreenCapture.CaptureScreenshot(Path.Combine(_directory, name + ".png"));

        yield return null;
        yield return null;

    }

}
