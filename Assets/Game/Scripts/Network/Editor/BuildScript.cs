using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// Build helpers for multiplayer testing. Produces a standalone Windows client
    /// that can be launched alongside the Editor host (or another client build) to
    /// test two-end networking without opening a second Editor instance.
    /// </summary>
    public static class BuildScript
    {
        private static readonly string[] Scenes = { "Assets/Game/Scenes/OutdoorsScene.scene" };

        [MenuItem("HagenDa/Build Windows Client")]
        public static void BuildWindowsClient()
        {
            PerformBuild();
        }

        public static void PerformBuild()
        {
            string path = "Build/Client/HagenDa.exe";

            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = Scenes,
                locationPathName = path,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[BuildScript] Build succeeded -> {path} ({summary.totalSize} bytes)");
            }
            else
            {
                Debug.LogError($"[BuildScript] Build failed: {summary.result}");
            }
        }
    }
}
