using UnityEditor;
using UnityEditor.Build.Reporting;

namespace MaxQ.Game.Editor;

public static class Build {

    [MenuItem("Max-Q/Build Windows Player")]
    public static void Windows() {

        BuildReport report = BuildPipeline.BuildPlayer(EditorBuildSettings.scenes, "Builds/Windows/Max-Q.exe", BuildTarget.StandaloneWindows64, BuildOptions.None);

        if (report.summary.result != BuildResult.Succeeded) {

            EditorApplication.Exit(1);

        }

    }

}
