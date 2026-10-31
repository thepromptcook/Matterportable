using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One-time setup for this project on Unity 6, so it opens without deprecation warnings:
/// installs URP and the Input System (versions matching your editor), creates and assigns a URP asset,
/// switches Active Input Handling to the Input System, and turns off deprecated Dynamic Batching.
/// Re-runs safely; does nothing once everything is configured. Tools ▸ Matterportable ▸ Configure Project.
/// </summary>
[InitializeOnLoad]
static class MatterportProjectSetup
{
    const string SkipKey = "Matterportable.ProjectSetup.Skip";
    const string OpenedImporterKey = "Matterportable.ProjectSetup.OpenedImporter";
    const string UrpAssetType = "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime";
    const string UrpRendererType = "UnityEngine.Rendering.Universal.UniversalRendererData, Unity.RenderPipelines.Universal.Runtime";
    const string PostProcessDataType = "UnityEngine.Rendering.Universal.PostProcessData, Unity.RenderPipelines.Universal.Runtime";
    const string InputSystemType = "UnityEngine.InputSystem.InputSystem, Unity.InputSystem";
    const string SettingsFolder = "Assets/Settings";

    static Request _request;

    static MatterportProjectSetup()
    {
        EditorApplication.delayCall += () => Run(false);
    }

    [MenuItem("Tools/Matterportable/Configure Project (URP + Input System)", priority = 0)]
    static void RunFromMenu()
    {
        EditorPrefs.DeleteKey(SkipKey + ProjectKey);
        Run(true);
    }

    static string ProjectKey => Application.dataPath;

    static void Run(bool interactive)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || _request != null) return;
        if (!interactive && EditorPrefs.GetBool(SkipKey + ProjectKey, false)) { OpenImporterOnce(); return; }

        bool needUrp = Type.GetType(UrpAssetType) == null;
        bool needInput = Type.GetType(InputSystemType) == null;
        bool needPipeline = GraphicsSettings.defaultRenderPipeline == null;
        bool needHandler = GetPlayerSetting("activeInputHandler", -1) == 0;
        bool needBatching = DynamicBatchingEnabled();

        if (!(needUrp || needInput || needPipeline || needHandler || needBatching))
        {
            OpenImporterOnce();
            if (interactive) Debug.Log("[Matterportable] Project already configured.");
            return;
        }

        if (needUrp || needInput)
        {
            var packages = new System.Collections.Generic.List<string>();
            if (needUrp) packages.Add("com.unity.render-pipelines.universal");
            if (needInput) packages.Add("com.unity.inputsystem");
            Debug.Log("[Matterportable] Installing " + string.Join(", ", packages) + "…");
#if UNITY_2021_2_OR_NEWER
            _request = Client.AddAndRemove(packages.ToArray());
#else
            _request = Client.Add(packages[0]); // older editors: one package per run
#endif
            EditorApplication.update += WaitForPackages;
            return; // scripts reload after install, and this runs again
        }

        if (needBatching) DisableDynamicBatching();
        if (needPipeline) CreateUrpAsset();
        if (needHandler)
        {
            // 1 = Input System Package (New). Takes effect after a restart.
            SetPlayerSetting("activeInputHandler", 1);
            AssetDatabase.SaveAssets();
            if (EditorUtility.DisplayDialog("Matterportable project setup",
                    "Input handling was switched to the Input System. Unity needs to restart to apply it.", "Restart now", "Later"))
            {
                EditorApplication.OpenProject(Directory.GetCurrentDirectory());
                return;
            }
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[Matterportable] Project configured for Unity 6 (URP + Input System).");
        OpenImporterOnce();
    }

    static void WaitForPackages()
    {
        if (_request == null || !_request.IsCompleted) return;
        EditorApplication.update -= WaitForPackages;
        if (_request.Status == StatusCode.Failure)
            Debug.LogError("[Matterportable] Package install failed: " + _request.Error?.message +
                           ". Install 'Universal RP' and 'Input System' from Window ▸ Package Manager, then run Tools ▸ Matterportable ▸ Configure Project.");
        _request = null;
    }

    const string LauncherScene = "Assets/Matterport/Launcher.unity";

    /// <summary>Creates the start scene (in-game link menu) once, makes it the first build scene and opens it.</summary>
    static void OpenImporterOnce()
    {
        if (EditorPrefs.GetBool(OpenedImporterKey + ProjectKey, false)) return;
        EditorPrefs.SetBool(OpenedImporterKey + ProjectKey, true);
        if (!File.Exists(LauncherScene))
        {
            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            new GameObject("Matterport Launcher").AddComponent<Matterportable.MatterportLauncher>();
            Directory.CreateDirectory(Path.GetDirectoryName(LauncherScene));
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, LauncherScene);
        }
        else if (UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path != LauncherScene)
        {
            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(LauncherScene);
        }
        var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        scenes.RemoveAll(sc => sc.path == LauncherScene);
        scenes.Insert(0, new EditorBuildSettingsScene(LauncherScene, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log("[Matterportable] Opened the start scene. Press Play and paste a Matterport link into the menu.");
    }

    static void CreateUrpAsset()
    {
        try
        {
            var assetType = Type.GetType(UrpAssetType);
            var rendererType = Type.GetType(UrpRendererType);
            if (assetType == null || rendererType == null) throw new Exception("URP types not found");
            Directory.CreateDirectory(SettingsFolder);

            var renderer = ScriptableObject.CreateInstance(rendererType);
            var ppType = Type.GetType(PostProcessDataType);
            var getDefault = ppType?.GetMethod("GetDefaultPostProcessData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var ppField = rendererType.GetField("postProcessData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getDefault != null && ppField != null) ppField.SetValue(renderer, getDefault.Invoke(null, null));
            AssetDatabase.CreateAsset(renderer, SettingsFolder + "/URP-Renderer.asset");

            var create = assetType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
            var asset = (RenderPipelineAsset)create.Invoke(null, new object[] { renderer });
            AssetDatabase.CreateAsset(asset, SettingsFolder + "/URP.asset");
            AssetDatabase.SaveAssets();

            GraphicsSettings.defaultRenderPipeline = asset;
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = asset;
            }
            Debug.Log("[Matterportable] Created and assigned " + SettingsFolder + "/URP.asset.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Matterportable] Could not create a URP asset automatically (" + e.Message + "). Create one with " +
                             "Assets ▸ Create ▸ Rendering ▸ URP Asset (with Universal Renderer) and assign it in Project Settings ▸ Graphics.");
        }
    }

    // ---- Player settings that have no public API, edited through the serialized PlayerSettings asset.

    static SerializedObject PlayerSettingsObject()
    {
        var obj = Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings");
        return obj != null ? new SerializedObject(obj) : null;
    }

    static int GetPlayerSetting(string name, int fallback)
    {
        var p = PlayerSettingsObject()?.FindProperty(name);
        return p != null ? p.intValue : fallback;
    }

    static void SetPlayerSetting(string name, int value)
    {
        var so = PlayerSettingsObject();
        var p = so?.FindProperty(name);
        if (p == null) return;
        p.intValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static bool DynamicBatchingEnabled()
    {
        var so = PlayerSettingsObject();
        if (so == null) return false;
        var it = so.GetIterator();
        while (it.Next(true))
            if (it.propertyType == SerializedPropertyType.Boolean && it.name.IndexOf("DynamicBatching", StringComparison.OrdinalIgnoreCase) >= 0 && it.boolValue)
                return true;
        return false;
    }

    static void DisableDynamicBatching()
    {
        // 1) Serialized player settings (per-platform batching entries).
        var so = PlayerSettingsObject();
        if (so != null)
        {
            var it = so.GetIterator();
            while (it.Next(true))
                if (it.propertyType == SerializedPropertyType.Boolean && it.name.IndexOf("DynamicBatching", StringComparison.OrdinalIgnoreCase) >= 0)
                    it.boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        // 2) Editor API (internal in some versions) for every build target.
        foreach (var m in typeof(PlayerSettings).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var ps = m.GetParameters();
            if (m.Name.IndexOf("DynamicBatching", StringComparison.OrdinalIgnoreCase) < 0 || !m.Name.StartsWith("Set") || ps.Length != 2 || ps[1].ParameterType != typeof(bool)) continue;
            foreach (BuildTarget t in Enum.GetValues(typeof(BuildTarget)))
            {
                try { if (ps[0].ParameterType == typeof(BuildTarget)) m.Invoke(null, new object[] { t, false }); }
                catch (Exception) { /* unsupported target */ }
            }
        }
        AssetDatabase.SaveAssets();
    }
}
