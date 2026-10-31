using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Matterportable.Core;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Matterportable.Editor
{
    /// <summary>
    /// Bundles the cached spaces used by the build's scenes into StreamingAssets/Matterport for the duration of
    /// the build (removed afterwards), so players work offline. Toggle via Tools ▸ Matterportable.
    /// </summary>
    sealed class MatterportBuildHook : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        const string TargetRoot = "Assets/StreamingAssets/Matterport";
        const string MenuPath = "Tools/Matterportable/Bundle Cached Spaces Into Builds";
        static string PrefKey => "Matterportable.BundleSpaces." + Application.dataPath;
        static bool _created;

        public int callbackOrder => 0;

        static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, true);
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        [MenuItem(MenuPath, priority = 30)]
        static void Toggle() => Enabled = !Enabled;

        [MenuItem(MenuPath, true)]
        static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            _created = false;
            if (!Enabled) return;
            var spaces = FindSpacesInBuildScenes();
            if (spaces.Count == 0) return;
            _created = !Directory.Exists(TargetRoot);
            foreach (var kv in spaces)
            {
                var src = MatterportPaths.SpaceDir(kv.Value, kv.Key);
                if (!SpaceManifest.Exists(src))
                {
                    Debug.LogWarning($"[Matterportable] Space {kv.Key} used in a build scene is not downloaded; the player will try to download it at runtime.");
                    continue;
                }
                var dst = Path.Combine(TargetRoot, CacheLayout.SafeName(kv.Key));
                CopyDirectory(src, dst);
                Debug.Log($"[Matterportable] Bundled space {kv.Key} ({IoUtil.FormatBytes(IoUtil.DirectorySize(dst))}) into the build.");
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (!_created) return;
            _created = false;
            if (Directory.Exists(TargetRoot)) Directory.Delete(TargetRoot, true);
            var meta = TargetRoot + ".meta";
            if (File.Exists(meta)) File.Delete(meta);
            AssetDatabase.Refresh();
        }

        /// <summary>Model IDs (and cache folders) referenced by MatterportSpace components in enabled build scenes.</summary>
        static Dictionary<string, string> FindSpacesInBuildScenes()
        {
            var result = new Dictionary<string, string>();
            var linkRx = new Regex(@"^\s*link:\s*(.*)$");
            var folderRx = new Regex(@"^\s*cacheFolder:\s*(.*)$");
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (!scene.enabled || !File.Exists(scene.path)) continue;
                string pendingId = null;
                foreach (var line in File.ReadLines(scene.path))
                {
                    var m = linkRx.Match(line);
                    if (m.Success)
                    {
                        if (pendingId != null && !result.ContainsKey(pendingId)) result[pendingId] = "";
                        pendingId = MatterportLink.TryParseModelId(m.Groups[1].Value.Trim().Trim('\'', '"'), out var id) ? id : null;
                        continue;
                    }
                    var f = folderRx.Match(line);
                    if (f.Success && pendingId != null)
                    {
                        result[pendingId] = f.Groups[1].Value.Trim().Trim('\'', '"');
                        pendingId = null;
                    }
                }
                if (pendingId != null && !result.ContainsKey(pendingId)) result[pendingId] = "";
            }
            return result;
        }

        static void CopyDirectory(string src, string dst)
        {
            foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                if (rel.StartsWith(CacheLayout.DebugDir) || rel.EndsWith(".part") || rel.EndsWith(".tmp")) continue;
                var target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }
    }
}
