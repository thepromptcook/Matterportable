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
    /// Editor-side actions (the in-game launcher is the main way to open spaces): downloads with a cancelable progress
    /// bar, browser-export import, diagnostics, and the Tools / GameObject menu items.
    /// </summary>
    public static class MatterportEditorTools
    {
        static bool _busy;

        public static bool Busy => _busy;

        /// <summary>Downloads (or completes) a space, aligns its panoramas, then calls <paramref name="onDone"/>.</summary>
        public static async void Download(string link, string cacheFolder, Action onDone = null)
        {
            if (_busy || !MatterportLink.TryParseModelId(link, out var id)) return;
            var dir = MatterportPaths.SpaceDir(cacheFolder, id);
            await RunJob("Downloading " + id, async (downloader, ct) =>
            {
                await downloader.DownloadAsync(id, dir, new DownloadOptions(), ct);
                return dir;
            }, onDone);
        }

        [MenuItem("Tools/Matterportable/Import Browser Export…", priority = 10)]
        static async void ImportBrowserExport()
        {
            var file = EditorUtility.OpenFilePanel("Matterportable browser export", "", "json");
            if (string.IsNullOrEmpty(file) || _busy) return;
            await RunJob("Importing browser export", async (downloader, ct) =>
            {
                var manifest = await downloader.ImportExportAsync(File.ReadAllText(file), MatterportPaths.DefaultCacheRoot, new DownloadOptions(), ct);
                return MatterportPaths.SpaceDir("", manifest.ModelId);
            }, null);
        }

        [MenuItem("Tools/Matterportable/Copy Browser Export Script", priority = 11)]
        static void CopyBrowserScript()
        {
            EditorGUIUtility.systemCopyBuffer = BrowserExport.BuildScript();
            Debug.Log("[Matterportable] Browser export script copied. Paste it into the developer console on https://my.matterport.com/show/?m=<id>, " +
                      "then use Tools ▸ Matterportable ▸ Import Browser Export….");
        }

        static async Task RunJob(string title, Func<SpaceDownloader, CancellationToken, Task<string>> work, Action onDone)
        {
            _busy = true;
            var cts = new CancellationTokenSource();
            var log = new BufferLog { Forward = UnityLog.WarningsOnly };
            var http = new HttpClientTransport();
            var downloader = new SpaceDownloader(http, log);
            void Progress()
            {
                if (EditorUtility.DisplayCancelableProgressBar(title, downloader.Status.ToString(), downloader.Status.Fraction)) cts.Cancel();
            }
            EditorApplication.update += Progress;
            try
            {
                var dir = await work(downloader, cts.Token);
                EditorApplication.update -= Progress;
                EditorUtility.DisplayProgressBar(title, "Aligning panoramas…", 1f);
                var cal = await CalibrationRunner.RunAsync(dir, log, cts.Token);
                Debug.Log($"[Matterportable] {title}: done ({downloader.Status}, alignment {cal.Method}).");
                onDone?.Invoke();
            }
            catch (OperationCanceledException) { Debug.Log("[Matterportable] Cancelled; files already downloaded are kept."); }
            catch (Exception e) { Debug.LogError("[Matterportable] " + title + " failed: " + e.GetBaseException().Message); }
            finally
            {
                EditorApplication.update -= Progress;
                EditorUtility.ClearProgressBar();
                http.Dispose();
                _busy = false;
            }
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

        [MenuItem("Tools/Matterportable/Open Cache Folder", priority = 20)]
        static void OpenCacheFolder() => Reveal(MatterportPaths.DefaultCacheRoot);

        [MenuItem("Tools/Matterportable/Enable Fast Enter Play Mode", priority = 21)]
        static void EnableFastEnterPlayMode()
        {
#if !UNITY_6000_0_OR_NEWER
#pragma warning disable CS0618
            EditorSettings.enterPlayModeOptionsEnabled = true;
#pragma warning restore CS0618
#endif
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            Debug.Log("[Matterportable] Enter Play Mode: domain reload disabled.");
        }

        [MenuItem("GameObject/Matterportable/Matterport Space + Camera", priority = 10)]
        static void CreateRig()
        {
            var rig = MatterportRig.Create("", "");
            Undo.RegisterCreatedObjectUndo(rig.Space.gameObject, "Create Matterport Space");
            Undo.RegisterCreatedObjectUndo(rig.Navigator.gameObject, "Create Matterport Camera");
            Selection.activeGameObject = rig.Space.gameObject;
        }
    }
}
