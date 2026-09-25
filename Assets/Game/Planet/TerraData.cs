using System.IO;

using UnityEngine;

namespace MaxQ.Game.Planet;

/// <summary>Where tools/terra.sh baked Terra's survey and colour: Data/Terra at the project root, found by walking up
/// from the editor's Assets folder or the player's data folder.</summary>
public static class TerraData {

    public static string Directory {

        get {

            for (DirectoryInfo folder = new DirectoryInfo(Application.dataPath); folder != null; folder = folder.Parent) {

                string candidate = Path.Combine(folder.FullName, "Data", "Terra");

                if (System.IO.Directory.Exists(candidate)) {

                    return candidate;

                }

            }

            return null;

        }

    }

}
