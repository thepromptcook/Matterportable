using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Matterportable.Core;
using UnityEditor;
using UnityEngine;

namespace Matterportable.Editor
{
    /// <summary>
    /// Window ▸ Matterportable ▸ Import Space: paste a Matterport link, download + align it, get a playable scene.
    /// </summary>
    public sealed class MatterportImportWindow : EditorWindow
    {
        static readonly int[] PanoSizes = { 512, 1024, 2048, 4096 };
        static readonly string[] PanoLabels = { "Low (512 px faces)", "Medium (1024 px)", "High (2048 px) - recommended", "Ultra (4096 px, large download)" };

        [SerializeField] string _link = "";
        [SerializeField] int _panoQuality = 2;
        [SerializeField] bool _highTextures = true;
        [SerializeField] int _maxSweeps;
        [SerializeField] string _cacheFolder = "";
        [SerializeField] bool _createScene = true;
        [SerializeField] bool _enterPlayMode = true;
        [SerializeField] int _demoFaceSize = 1024;

        string _busy;
        CancellationTokenSource _cts;
        SpaceDownloader _downloader;
        BufferLog _log = new BufferLog { Forward = UnityLog.Instance };
        Vector2 _logScroll;
        string _lastResult;
        bool _showAdvanced;

        [MenuItem("Window/Matterportable/Import Space", priority = 1)]
        public static MatterportImportWindow Open()
        {
            var w = GetWindow<MatterportImportWindow>("Matterportable");
            w.minSize = new Vector2(420, 420);
            return w;
        }

        /// <summary>Opens the window with a link filled in and starts downloading.</summary>
        public static void OpenAndDownload(string link, string cacheFolder)
        {
            var w = Open();
            w._link = link;
            w._cacheFolder = cacheFolder ?? "";
            w._createScene = false;
            w.StartDownload();
        }

        public static void OpenAndGenerateDemo() => Open().StartDemo();

        void OnDisable() => _cts?.Cancel();

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Matterport → Unity", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Paste a Matterport link (e.g. https://discover.matterport.com/space/rGMMnZDh3Tf). The space is downloaded once into " +
                "<project>/MatterportCache, the panoramas are aligned automatically, and a scene is created - press Play to explore.\n" +
                "Only import spaces you own or have permission to use; Matterport's terms restrict copying content.",
                MessageType.None);

            using (new EditorGUI.DisabledScope(_busy != null))
            {
                _link = EditorGUILayout.TextField("Matterport link", _link);
                bool valid = MatterportLink.TryParseModelId(_link, out var modelId);
                if (!string.IsNullOrWhiteSpace(_link))
                {
                    if (valid)
                    {
                        var dir = MatterportPaths.SpaceDir(_cacheFolder, modelId);
                        EditorGUILayout.LabelField("Model ID", modelId + (SpaceManifest.Exists(dir) ? "   (already cached)" : ""));
                    }
                    else EditorGUILayout.HelpBox("No model ID found in this link.", MessageType.Warning);
                }

                _panoQuality = EditorGUILayout.Popup("Panorama quality", _panoQuality, PanoLabels);
                _highTextures = EditorGUILayout.Toggle("High-res mesh textures", _highTextures);
                _createScene = EditorGUILayout.Toggle("Create scene", _createScene);
                using (new EditorGUI.DisabledScope(!_createScene)) _enterPlayMode = EditorGUILayout.Toggle("Enter Play mode after", _enterPlayMode);

                _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced", true);
                if (_showAdvanced)
                {
                    EditorGUI.indentLevel++;
                    _maxSweeps = EditorGUILayout.IntField(new GUIContent("Max sweeps (0 = all)", "Download only the first N panoramas for a quick first look."), Mathf.Max(0, _maxSweeps));
                    _cacheFolder = EditorGUILayout.TextField(new GUIContent("Cache folder", "Empty = <project>/MatterportCache"), _cacheFolder);
                    _demoFaceSize = EditorGUILayout.IntPopup("Demo panorama size", _demoFaceSize, new[] { "512", "1024", "2048" }, new[] { 512, 1024, 2048 });
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space(6);
                using (new EditorGUI.DisabledScope(!valid))
                {
                    if (GUILayout.Button(_createScene ? "Download && Create Scene" : "Download", GUILayout.Height(32))) StartDownload();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!valid || !SpaceManifest.Exists(MatterportPaths.SpaceDir(_cacheFolder, modelId ?? ""))))
                    {
                        if (GUILayout.Button("Create Scene from Cache")) CreateSceneFor(modelId, _link, null);
                        if (GUILayout.Button("Re-run Alignment")) StartAlign(MatterportPaths.SpaceDir(_cacheFolder, modelId));
                    }
                }
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Offline / fallback", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Generate Demo Space", "A procedural house in Matterport's format - tests the whole pipeline without network access."))) StartDemo();
                    if (GUILayout.Button(new GUIContent("Import Browser Export…", "Import the JSON saved by the browser export script."))) StartImportExport();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Copy Browser Export Script", "For networks where Matterport blocks the importer: run this in the browser console on my.matterport.com/show/?m=<id>.")))
                    {
                        EditorGUIUtility.systemCopyBuffer = BrowserExport.BuildScript(valid ? modelId : null);
                        ShowNotification(new GUIContent("Script copied - paste it into the browser console"));
                    }
                    if (GUILayout.Button("Open Cache Folder")) MatterportSceneBuilder.Reveal(MatterportPaths.ResolveCacheRoot(_cacheFolder));
                }
                if (GUILayout.Button(new GUIContent("Enable Fast Enter Play Mode", "Disables domain reload when entering Play mode (project setting)."))) MatterportSceneBuilder.EnableFastEnterPlayMode();
            }

            if (_busy != null)
            {
                EditorGUILayout.Space(6);
                var status = _downloader != null ? _downloader.Status.ToString() : _busy;
                float fraction = _downloader != null ? _downloader.Status.Fraction : (float)(EditorApplication.timeSinceStartup % 1.0);
                var r = GUILayoutUtility.GetRect(18, 20, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(r, fraction, status);
                if (GUILayout.Button("Cancel")) _cts?.Cancel();
            }
            else if (!string.IsNullOrEmpty(_lastResult))
            {
                EditorGUILayout.HelpBox(_lastResult, _lastResult.StartsWith("Error") ? MessageType.Error : MessageType.Info);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.ExpandHeight(true));
            var lines = _log.Snapshot();
            for (int i = Math.Max(0, lines.Count - 200); i < lines.Count; i++)
            {
                var style = lines[i].Key == LogLevel.Info ? EditorStyles.wordWrappedMiniLabel : EditorStyles.wordWrappedLabel;
                var prefix = lines[i].Key == LogLevel.Error ? "✖ " : lines[i].Key == LogLevel.Warning ? "⚠ " : "";
                EditorGUILayout.LabelField(prefix + lines[i].Value, style);
            }
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ jobs

        async void RunJob(string title, Func<CancellationToken, Task<string>> job)
        {
            if (_busy != null) return;
            _busy = title;
            _lastResult = null;
            _cts = new CancellationTokenSource();
            EditorApplication.update += Repaint;
            try
            {
                _lastResult = await job(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                _lastResult = "Cancelled. Already downloaded files are kept; run again to resume.";
            }
            catch (MatterportBlockedException e)
            {
                _lastResult = "Error: " + e.Message;
                _log.Error(e.Message);
            }
            catch (Exception e)
            {
                var inner = e is AggregateException ae && ae.InnerException != null ? ae.InnerException : e;
                _lastResult = "Error: " + inner.Message;
                _log.Error(inner.ToString());
            }
            finally
            {
                EditorApplication.update -= Repaint;
                _busy = null;
                _downloader = null;
                Repaint();
            }
        }

        void StartDownload()
        {
            if (!MatterportLink.TryParseModelId(_link, out var modelId)) return;
            var link = _link.Trim();
            RunJob("Downloading", async ct =>
            {
                var dir = MatterportPaths.SpaceDir(_cacheFolder, modelId);
                SpaceManifest manifest;
                using (var http = new HttpClientTransport())
                {
                    _downloader = new SpaceDownloader(http, _log);
                    var options = new DownloadOptions
                    {
                        PanoSizes = new[] { 512, PanoSizes[Mathf.Clamp(_panoQuality, 0, PanoSizes.Length - 1)] },
                        TextureQuality = _highTextures ? "high" : "low",
                        MaxSweeps = _maxSweeps,
                    };
                    manifest = await _downloader.DownloadAsync(modelId, dir, options, ct);
                }
                _downloader = null;
                _busy = "Aligning panoramas";
                await MatterportSceneBuilder.AlignAsync(dir, _log, ct);
                RefreshSpacesInScene(modelId);
                if (_createScene) CreateSceneFor(modelId, link, manifest.Name);
                return $"Downloaded \"{manifest.Name}\": {manifest.Sweeps.Count} sweeps → {dir}";
            });
        }

        void StartAlign(string spaceDir)
        {
            RunJob("Aligning panoramas", async ct =>
            {
                var cal = await MatterportSceneBuilder.AlignAsync(spaceDir, _log, ct, force: true);
                RefreshSpacesInScene(null);
                return $"Alignment: {cal.Method}" + (double.IsNaN(cal.Score) ? "" : $" (score {cal.Score:0.00})");
            });
        }

        void StartDemo()
        {
            int size = _demoFaceSize;
            RunJob("Generating demo space", async ct =>
            {
                var dir = await MatterportSceneBuilder.GenerateDemoSpaceAsync(_cacheFolder, size, ct);
                _log.Info("Demo space written to " + dir);
                RefreshSpacesInScene(MatterportSceneBuilder.DemoModelId);
                CreateSceneFor(MatterportSceneBuilder.DemoModelId, MatterportSceneBuilder.DemoModelId, "Matterportable Demo");
                return "Demo space ready. Press Play to explore it.";
            });
        }

        void StartImportExport()
        {
            var file = EditorUtility.OpenFilePanel("Matterportable browser export", "", "json");
            if (string.IsNullOrEmpty(file)) return;
            RunJob("Importing browser export", async ct =>
            {
                SpaceManifest manifest;
                using (var http = new HttpClientTransport())
                {
                    _downloader = new SpaceDownloader(http, _log);
                    var options = new DownloadOptions
                    {
                        PanoSizes = new[] { 512, PanoSizes[Mathf.Clamp(_panoQuality, 0, PanoSizes.Length - 1)] },
                        TextureQuality = _highTextures ? "high" : "low",
                        MaxSweeps = _maxSweeps,
                    };
                    manifest = await _downloader.ImportExportAsync(File.ReadAllText(file), MatterportPaths.ResolveCacheRoot(_cacheFolder), options, ct);
                }
                _downloader = null;
                var dir = MatterportPaths.SpaceDir(_cacheFolder, manifest.ModelId);
                _busy = "Aligning panoramas";
                await MatterportSceneBuilder.AlignAsync(dir, _log, ct);
                _link = manifest.ModelId;
                if (_createScene) CreateSceneFor(manifest.ModelId, manifest.ModelId, manifest.Name);
                return $"Imported \"{manifest.Name}\": {manifest.Sweeps.Count} sweeps.";
            });
        }

        void CreateSceneFor(string modelId, string link, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                try { name = SpaceManifest.Load(MatterportPaths.SpaceDir(_cacheFolder, modelId)).Name; }
                catch (Exception) { name = modelId; }
            }
            var space = MatterportSceneBuilder.CreateScene(link, name ?? modelId, _cacheFolder);
            if (space != null && _enterPlayMode) EditorApplication.isPlaying = true;
        }

        static void RefreshSpacesInScene(string modelId)
        {
#if UNITY_2023_1_OR_NEWER
            var spaces = FindObjectsByType<MatterportSpace>(FindObjectsSortMode.None);
#else
            var spaces = FindObjectsOfType<MatterportSpace>();
#endif
            foreach (var s in spaces)
                if (modelId == null || s.ModelId == modelId || (MatterportLink.TryParseModelId(s.link, out var id) && id == modelId))
                    s.Reload();
        }
    }
}
