using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace Physics.Editor
{
    /// <summary>
    /// Automates building the Unity WebGL multiplayer game and formatting it
    /// for immediate deployment to GitHub Pages.
    /// </summary>
    public static class WebGLBuildExporter
    {
        private const string SCENE_PATH = "Assets/Scenes/DodgeballCourt.unity";
        private const string OUTPUT_FOLDER = "docs";

        [MenuItem("Dodgeball/Configure WebGL Player Settings for GitHub Pages", false, 10)]
        public static void ConfigureWebGLPlayerSettings()
        {
            Debug.Log("[WebGLBuildExporter] Configuring WebGL Player Settings for GitHub Pages deployment...");

            // Switch to WebGL build target group
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);

            // Set compression to Disabled for universal GitHub Pages hosting (avoids gzip/brotli MIME type errors)
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.dataCaching = true;

            // Set webgl canvas sizing
            PlayerSettings.defaultWebScreenWidth = 1280;
            PlayerSettings.defaultWebScreenHeight = 720;
            PlayerSettings.runInBackground = true;

            Debug.Log("[WebGLBuildExporter] Player Settings configured: WebGL Target, Disabled Compression (GitHub Pages compatible), 1280x720 canvas, Decompression Fallback Enabled.");
        }

        [MenuItem("Dodgeball/Build WebGL for GitHub Pages (docs folder)", false, 11)]
        public static void BuildWebGLForGitHubPages()
        {
            ConfigureWebGLPlayerSettings();

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string buildPath = Path.Combine(projectRoot, OUTPUT_FOLDER);

            Debug.Log($"[WebGLBuildExporter] Starting automated WebGL build to '{buildPath}'...");

            if (!File.Exists(SCENE_PATH))
            {
                Debug.LogError($"[WebGLBuildExporter] Scene not found at '{SCENE_PATH}'! Building scene first...");
                DodgeballCourtSceneBuilder.BuildDodgeballCourtScene();
            }

            BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions
            {
                scenes = new[] { SCENE_PATH },
                locationPathName = buildPath,
                target = BuildTarget.WebGL,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                // Create .nojekyll file so GitHub Pages doesn't ignore directories
                string noJekyllPath = Path.Combine(buildPath, ".nojekyll");
                File.WriteAllText(noJekyllPath, "");

                Debug.Log($"[WebGLBuildExporter] Build SUCCEEDED: {summary.totalSize} bytes in {summary.totalTime.TotalSeconds:F1} seconds.");
                Debug.Log($"[WebGLBuildExporter] Ready to push to GitHub! In repo settings: Enable GitHub Pages from main branch /docs folder.");
            }
            else if (summary.result == BuildResult.Failed)
            {
                Debug.LogError($"[WebGLBuildExporter] Build FAILED with {summary.totalErrors} errors.");
            }
        }
    }
}

