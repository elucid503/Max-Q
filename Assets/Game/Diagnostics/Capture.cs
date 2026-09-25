using System;
using System.Collections;
using System.IO;

using MaxQ.Game.Map;

using UnityEngine;

namespace MaxQ.Game.Diagnostics;

/// <summary>Scripted screenshots for headless review: run the player with <c>-capture &lt;dir&gt;</c>.</summary>
public sealed class Capture : MonoBehaviour {

    private string _directory;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot() {

        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-capture");

        if (index < 0 || index + 1 >= args.Length) {

            return;

        }

        Capture capture = new GameObject("Capture").AddComponent<Capture>();
        capture._directory = args[index + 1];

    }

    private IEnumerator Start() {

        Directory.CreateDirectory(_directory);
        MapView map = FindAnyObjectByType<MapView>();

        yield return Shoot(map, "01-parking", 0, 20.0f, 30.0f, 4_000.0f);
        yield return Shoot(map, "02-system", 1, 0.0f, 70.0f, 190_000.0f);

        // Burn, then coast to the Selene SOI.
        map.Jump(map.TimeToNextEvent);
        yield return null;
        map.Jump(map.TimeToNextEvent + 600.0);

        yield return Shoot(map, "03-encounter", 2, 40.0f, 35.0f, 45_000.0f);
        yield return Shoot(map, "04-flyby", 0, 60.0f, 20.0f, 2_500.0f);

        Application.Quit();

    }

    private IEnumerator Shoot(MapView map, string name, int focus, float yaw, float pitch, float distance) {

        map.Frame(focus, yaw, pitch, distance);

        for (int i = 0; i < 4; i++) {

            yield return null;

        }

        ScreenCapture.CaptureScreenshot(Path.Combine(_directory, name + ".png"));

        yield return null;
        yield return null;

    }

}
