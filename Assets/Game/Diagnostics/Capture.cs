using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

using MaxQ.Game.Map;

using UnityEngine;

namespace MaxQ.Game.Diagnostics;

/// <summary>Scripted screenshots for headless review: run the player with <c>-capture &lt;dir&gt;</c>, optionally
/// <c>-only &lt;text&gt;</c> to keep just the shots whose names contain it. Each shot waits for the
/// ground to finish streaming, then logs GPU time to timings.txt beside the images: the whole frame, and what the frame
/// saves without the ground, without the atmosphere and without the sun's shadows.</summary>
public sealed class Capture : MonoBehaviour {

    private const int SettleFrameLimit = 900;
    private const int TimedFrames = 60;
    private const int AdaptFrames = 240;

    private string _directory;
    private string _only;
    private float _settleSeconds;
    private readonly List<string> _timings = new List<string>();
    private readonly FrameTiming[] _frameTimings = new FrameTiming[1];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot() {

        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-capture");

        if (index < 0 || index + 1 >= args.Length) {

            return;

        }

        Capture capture = new GameObject("Capture").AddComponent<Capture>();
        capture._directory = args[index + 1];

        int only = Array.IndexOf(args, "-only");
        capture._only = only >= 0 && only + 1 < args.Length ? args[only + 1] : null;

    }

    private IEnumerator Start() {

        Directory.CreateDirectory(_directory);
        MapView map = FindAnyObjectByType<MapView>();

        yield return Shoot(map, "map-parking", 0, 20.0f, 30.0f, 4_000.0f);
        yield return Shoot(map, "map-system", 1, 0.0f, 70.0f, 190_000.0f);

        // Burn, then coast to the Selene SOI.
        map.Jump(map.TimeToNextEvent);
        yield return null;
        map.Jump(map.TimeToNextEvent + 600.0);

        yield return Shoot(map, "map-encounter", 2, 40.0f, 35.0f, 45_000.0f);
        yield return Shoot(map, "map-flyby", 0, 60.0f, 20.0f, 2_500.0f);

        // The ground shots fly the clock forward to each place's local time.
        yield return Fly(map, "terra-5000km", 5.0, 20.0, 5_000_000.0, 0.0, -90.0, 11.0);
        yield return Fly(map, "coasts-1500km", 57.0, 10.0, 1_500_000.0, 0.0, -80.0, 12.0);
        yield return Fly(map, "finnish-lakes-60km", 61.8, 27.5, 60_000.0, 0.0, -70.0, 12.0);
        yield return Fly(map, "lake-geneva-3km", 46.38, 6.35, 3_000.0, 80.0, -25.0, 11.0);
        yield return Fly(map, "mediterranean-400km", 36.0, 12.0, 400_000.0, 300.0, -38.0, 10.0);
        yield return Fly(map, "limb-sunset-400km", 0.0, -20.0, 400_000.0, 270.0, -30.0, 17.6);
        yield return Fly(map, "himalaya-30km", 28.2, 84.0, 30_000.0, 95.0, -10.0, 9.0);
        yield return Fly(map, "everest-2km", 27.75, 86.85, 2_000.0, 15.0, -6.0, 8.5);
        yield return Fly(map, "sahara-2km", 25.3, 8.9, 2_000.0, 210.0, -18.0, 15.0);
        yield return Fly(map, "big-sur-100m", 36.1, -121.62, 100.0, 140.0, -4.0, 16.0);
        yield return Fly(map, "alps-2m", 46.58, 7.91, 2.0, 170.0, 6.0, 10.0);
        yield return Fly(map, "grand-canyon-sunset-2m", 36.06, -112.11, 2.0, 280.0, 2.0, 17.7);
        yield return Fly(map, "himalaya-dusk-30km", 28.0, 86.5, 30_000.0, 60.0, -12.0, 17.3);
        yield return Fly(map, "alps-evening-2m", 46.58, 7.91, 2.0, 60.0, 4.0, 17.2);
        yield return Fly(map, "sea-glitter-300m", 36.0, -122.2, 300.0, 255.0, -10.0, 16.3);
        yield return Fly(map, "himalaya-shafts-3km", 27.9, 86.9, 3_000.0, 265.0, 1.0, 17.8);
        yield return Fly(map, "eiger-scree-2m", 46.565, 8.0, 2.0, 200.0, -12.0, 11.0);
        yield return Fly(map, "pad-ground-2m", 46.6, 7.93, 1.8, 120.0, -35.0, 15.0);
        yield return Fly(map, "coast-dusk-50m", 36.3, -121.9, 50.0, 250.0, 2.0, 18.35);

        File.WriteAllLines(Path.Combine(_directory, "timings.txt"), _timings);
        Application.Quit();

    }

    private IEnumerator Fly(MapView map, string name, double latitude, double longitude, double altitude, double heading, double pitch, double solarHour) {

        if (_only != null && !name.Contains(_only)) {

            yield break;

        }

        map.Look(latitude, longitude, altitude, heading, pitch, solarHour);

        yield return Settle(map);
        yield return Save(map, name);

    }

    private IEnumerator Shoot(MapView map, string name, int focus, float yaw, float pitch, float distance) {

        if (_only != null && !name.Contains(_only)) {

            yield break;

        }

        map.Frame(focus, yaw, pitch, distance);

        yield return Settle(map);
        yield return Save(map, name);

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

    private IEnumerator Settle(MapView map) {

        float start = Time.realtimeSinceStartup;

        for (int i = 0; i < 4; i++) {

            yield return null;

        }

        for (int i = 0; i < SettleFrameLimit && map.Ground.Pending > 0; i++) {

            yield return null;

        }

        _settleSeconds = Time.realtimeSinceStartup - start;

    }

    private IEnumerator Save(MapView map, string name) {

        double[] gpu = new double[1];

        yield return MeasureGpu(gpu);
        double frame = gpu[0];

        map.Ground.Hidden = true;
        yield return MeasureGpu(gpu);
        double withoutGround = gpu[0];
        map.Ground.Hidden = false;

        map.Atmosphere.Enabled = false;
        yield return MeasureGpu(gpu);
        double withoutAir = gpu[0];
        map.Atmosphere.Enabled = true;

        map.Sun.Shadows = false;
        yield return MeasureGpu(gpu);
        double withoutShadows = gpu[0];
        map.Sun.Shadows = true;

        yield return MeasureGpu(gpu);

        // The eye readapts to the whole view after the timings hid parts of it.
        for (int i = 0; i < AdaptFrames; i++) {

            yield return null;

        }

        _timings.Add($"{name}: frame {frame:F2} ms GPU, ground {frame - withoutGround:F2} ms, atmosphere {frame - withoutAir:F2} ms, shadows {frame - withoutShadows:F2} ms; " +
            $"{map.Ground.ShownCount} patches drawn, {map.Ground.PatchCount} built, {map.Ground.Pending} pending, settled in {_settleSeconds:F1} s; exposure {Math.Log(map.Atmosphere.Exposure, 2.0):+0.0;-0.0} EV");

        ScreenCapture.CaptureScreenshot(Path.Combine(_directory, name + ".png"));

        yield return null;
        yield return null;

    }

}
