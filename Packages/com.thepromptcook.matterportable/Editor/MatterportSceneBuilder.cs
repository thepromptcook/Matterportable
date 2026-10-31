using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Matterportable.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Matterportable.Editor
{
    /// <summary>Scene creation, the offline demo space, alignment runs and project setup helpers.</summary>
    public static class MatterportSceneBuilder
    {
        public const string DemoModelId = "DemoSpace01";
        public const string SceneFolder = "Assets/Matterport";

        /// <summary>Creates (and saves) a new scene containing a space and a navigable camera.</summary>
        public static MatterportSpace CreateScene(string link, string displayName, string cacheFolder)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return null;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var space = CreateRig(link, cacheFolder);
            Directory.CreateDirectory(SceneFolder);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{SceneFolder}/{SafeFileName(displayName)}.unity");
            EditorSceneManager.SaveScene(scene, path);
            AddToBuildSettings(path);
            Debug.Log($"[Matterportable] Created scene {path}. Press Play to explore.");
            return space;
        }

        /// <summary>Adds a space + camera rig to the open scene.</summary>
        public static MatterportSpace CreateRig(string link, string cacheFolder)
        {
            var spaceGo = new GameObject("Matterport Space");
            Undo.RegisterCreatedObjectUndo(spaceGo, "Create Matterport Space");
            var space = spaceGo.AddComponent<MatterportSpace>();
            space.link = link ?? "";
            space.cacheFolder = cacheFolder ?? "";
            space.BuildEditorPreview();

            var camGo = new GameObject("Matterport Camera");
            Undo.RegisterCreatedObjectUndo(camGo, "Create Matterport Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 1000f;
            cam.fieldOfView = 75f;
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<MatterportNavigator>().space = space;
            camGo.AddComponent<MatterportHud>();
            if (space.Sweeps.Count > 0)
            {
                int start = Mathf.Clamp(space.startSweep, 0, space.Sweeps.Count - 1);
                camGo.transform.position = space.SweepWorldPosition(start);
            }
            Selection.activeGameObject = spaceGo;
            return space;
        }

        static void AddToBuildSettings(string scenePath)
        {
            var scenes = EditorBuildSettings.scenes;
            foreach (var s in scenes) if (s.path == scenePath) return;
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(scenes) { new EditorBuildSettingsScene(scenePath, true) };
            EditorBuildSettings.scenes = list.ToArray();
        }

        public static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Matterport Space";
            var s = Regex.Replace(name, @"[^\w\-. ]", "_").Trim();
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        // ------------------------------------------------------------------ demo space

        /// <summary>
        /// Generates the procedural demo house in Matterport's cache format (no network needed) so the whole
        /// pipeline can be tried and iterated on offline.
        /// </summary>
        public static async Task<string> GenerateDemoSpaceAsync(string cacheFolder, int faceSize, CancellationToken ct)
        {
            var options = new SyntheticOptions
            {
                ModelId = DemoModelId,
                PanoSizes = new[] { Mathf.Max(64, faceSize / 4), faceSize },
                TextureSize = 512,
                ImageExtension = "jpg",
            };
            var space = await Task.Run(() => SyntheticSpace.Generate(options), ct);
            var dir = MatterportPaths.SpaceDir(cacheFolder, DemoModelId);
            space.WriteTo(dir, img => MpUtil.EncodeJpg(img, 92));
            return dir;
        }

        // ------------------------------------------------------------------ alignment

        /// <summary>Re-solves panorama alignment for a cached space (unless calibration.json is marked manual).</summary>
        public static async Task<SpaceCalibration> AlignAsync(string spaceDir, ILogSink log, CancellationToken ct, bool force = false)
        {
            var manifest = SpaceManifest.Load(spaceDir);
            var existing = SpaceCalibration.LoadOrDefault(spaceDir);
            if (manifest.Mesh == null) return existing;
            if (existing.Method == "manual" && !force)
            {
                log?.Info("calibration.json is marked manual; skipping automatic alignment.");
                return existing;
            }
            var images = CalibrationRunner.DecodeInputs(spaceDir, manifest);
            return await CalibrationRunner.SolveAsync(spaceDir, manifest, images, log, ct);
        }

        // ------------------------------------------------------------------ project setup

        [MenuItem("Tools/Matterportable/Enable Fast Enter Play Mode", priority = 20)]
        public static void EnableFastEnterPlayMode()
        {
#if !UNITY_6000_0_OR_NEWER
#pragma warning disable CS0618
            EditorSettings.enterPlayModeOptionsEnabled = true;
#pragma warning restore CS0618
#endif
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            Debug.Log("[Matterportable] Enter Play Mode Options: domain reload disabled - entering Play mode is now near-instant.");
        }

        [MenuItem("Tools/Matterportable/Open Cache Folder", priority = 21)]
        public static void OpenCacheFolder() => Reveal(MatterportPaths.DefaultCacheRoot);

        public static void Reveal(string dir)
        {
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        [MenuItem("GameObject/Matterportable/Matterport Space + Camera", priority = 10)]
        static void CreateRigMenu() => CreateRig("", "");

        [MenuItem("Tools/Matterportable/Generate Demo Space", priority = 1)]
        static void GenerateDemoMenu() => MatterportImportWindow.OpenAndGenerateDemo();
    }
}
