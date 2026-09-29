using System.IO;

using UnityEngine;

namespace MaxQ.Game.Planet;

/// <summary>Data/&lt;body&gt; baked by tools/, found by walking up from the Assets or player data folder.</summary>
public static class SurveyData {

    public static string Directory(string body) {

        for (DirectoryInfo folder = new DirectoryInfo(Application.dataPath); folder != null; folder = folder.Parent) {

            string candidate = Path.Combine(folder.FullName, "Data", body);

            if (System.IO.Directory.Exists(candidate)) {

                return candidate;

            }

        }

        return null;

    }

}
