using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Matterportable.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Matterportable.Editor
{
    /// <summary>Editor helpers: scenes per space, rigs with undo, diagnostics, project shortcuts and menu items.</summary>
    public static class MatterportSceneBuilder
    {
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

        /// <summary>Adds a space + camera rig to the open scene (undoable).</summary>
        public static MatterportSpace CreateRig(string link, string cacheFolder)
        {
            var rig = MatterportRig.Create(link, cacheFolder);
            Undo.RegisterCreatedObjectUndo(rig.Space.gameObject, "Create Matterport Space");
            Undo.RegisterCreatedObjectUndo(rig.Navigator.gameObject, "Create Matterport Camera");
            Selection.activeGameObject = rig.Space.gameObject;
            return rig.Space;
        }

        static void AddToBuildSettings(string scenePath)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Exists(s => s.path == scenePath)) return;
            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        public static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Matterport Space";
            var s = Regex.Replace(name, @"[^\w\-. ]", "_").Trim();
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        /// <summary>
        /// Writes debug/diagnostics.txt for a space and copies it to the clipboard - the one thing to send when
        /// asking for help with a space that downloads or renders wrongly.
        /// </summary>
        public static void CopyDiagnostics(string spaceDir)
        {
            var report = Diagnostics.Report(spaceDir, MpUtil.EnvironmentSummary());
            var path = Path.Combine(spaceDir, CacheLayout.DebugDir, Diagnostics.FileName);
            IoUtil.WriteAllTextAtomic(path, report);
            EditorGUIUtility.systemCopyBuffer = report;
            Debug.Log($"[Matterportable] Diagnostics copied to the clipboard and saved to {path}.");
        }

        public static void Reveal(string dir)
        {
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

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
        static void OpenCacheFolder() => Reveal(MatterportPaths.DefaultCacheRoot);

        [MenuItem("GameObject/Matterportable/Matterport Space + Camera", priority = 10)]
        static void CreateRigMenu() => CreateRig("", "");

        [MenuItem("Tools/Matterportable/Generate Demo Space", priority = 1)]
        static void GenerateDemoMenu() => MatterportImportWindow.OpenAndGenerateDemo();
    }
}
