using System;
using System.Threading;
using Matterportable.Core;
using UnityEditor;
using UnityEngine;

namespace Matterportable.Editor
{
    /// <summary>MatterportSpace inspector: download/preview actions, live Play-mode controls, sweep list.</summary>
    [CustomEditor(typeof(MatterportSpace))]
    public sealed class MatterportSpaceEditor : UnityEditor.Editor
    {
        bool _showSweeps = true, _showLabels = true;
        string _filter = "";
        Vector2 _scroll;
        string _diskSize, _diskSizeFor;
        static bool _aligning;

        public override void OnInspectorGUI()
        {
            var space = (MatterportSpace)target;
            DrawDefaultInspector();
            bool valid = MatterportLink.TryParseModelId(space.link, out var modelId);
            if (!string.IsNullOrWhiteSpace(space.link) && !valid)
                EditorGUILayout.HelpBox("No Matterport model ID found in this link.", MessageType.Warning);

            EditorGUILayout.Space(4);
            if (Application.isPlaying) DrawPlayMode(space);
            else DrawEditMode(space, valid ? MatterportPaths.SpaceDir(space.cacheFolder, modelId) : null);

            if (space.Model == null) return;
            DrawInfo(space.Model, space.SpaceDir);
            DrawSweeps(space);
        }

        void DrawEditMode(MatterportSpace space, string dir)
        {
            bool cached = dir != null && SpaceManifest.Exists(dir);
            if (!string.IsNullOrEmpty(space.Error)) EditorGUILayout.HelpBox(space.Error, MessageType.Error);
            else if (dir != null && !cached) EditorGUILayout.HelpBox("Not downloaded yet. Download now, or press Play (it downloads automatically).", MessageType.Info);
            else if (!string.IsNullOrEmpty(space.Status)) EditorGUILayout.HelpBox(space.Status, MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(dir == null || MatterportEditorTools.Busy))
                    if (GUILayout.Button(cached ? "Update Download" : "Download"))
                        MatterportEditorTools.Download(space.link, space.cacheFolder, () => { if (space != null) space.BuildEditorPreview(); });
                using (new EditorGUI.DisabledScope(!cached))
                {
                    if (GUILayout.Button("Rebuild Preview")) space.BuildEditorPreview();
                    if (GUILayout.Button("Open Folder")) MatterportEditorTools.Reveal(dir);
                    if (GUILayout.Button(new GUIContent("Copy Diagnostics", "Copies a report to paste when asking for help with this space.")))
                        MatterportEditorTools.CopyDiagnostics(dir);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!cached || _aligning || space.Model?.Manifest.Mesh == null))
                    if (GUILayout.Button(_aligning ? "Aligning…" : "Re-run Panorama Alignment")) RunAlignment(space, dir);
                if (GUILayout.Button("▶ Play")) EditorApplication.isPlaying = true;
            }
        }

        async void RunAlignment(MatterportSpace space, string dir)
        {
            _aligning = true;
            try
            {
                var cal = await CalibrationRunner.RunAsync(dir, UnityLog.All, CancellationToken.None, force: true);
                Debug.Log($"[Matterportable] Alignment: {cal.Method}, score {cal.Score:0.00}, rotation {cal.Rotation}.");
                if (space != null) space.Reload();
            }
            catch (Exception e) { Debug.LogError("[Matterportable] Alignment failed: " + e.GetBaseException().Message); }
            finally
            {
                _aligning = false;
                Repaint();
            }
        }

        void DrawPlayMode(MatterportSpace space)
        {
            EditorGUILayout.HelpBox(space.Error ?? space.Status, space.Error != null ? MessageType.Error : MessageType.None);
            var nav = Navigator();
            if (nav != null)
            {
                EditorGUILayout.LabelField("Current sweep", nav.CurrentSweep + (nav.IsMoving ? "  (moving)" : ""));
                using (new EditorGUILayout.HorizontalScope())
                    foreach (ViewMode m in Enum.GetValues(typeof(ViewMode)))
                        if (GUILayout.Toggle(nav.Mode == m, m.ToString(), EditorStyles.miniButton) && nav.Mode != m) nav.SetMode(m);
                nav.AutoTour = EditorGUILayout.Toggle("Auto tour", nav.AutoTour);
                nav.DebugView = EditorGUILayout.Popup("Render view", nav.DebugView, ViewerText.DebugViews);
            }
            if (space.IsLoaded && GUILayout.Button("Reload Space")) space.Reload();
            if (space.SpaceDir != null && GUILayout.Button("Copy Diagnostics")) MatterportEditorTools.CopyDiagnostics(space.SpaceDir);
            Repaint();
        }

        static Navigator Navigator() => MpUtil.FindFirst<MatterportNavigator>()?.Core;

        void DrawInfo(SpaceModel model, string dir)
        {
            var m = model.Manifest;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(m.Name ?? m.ModelId, EditorStyles.boldLabel);
            if (_diskSizeFor != dir)
            {
                _diskSizeFor = dir;
                _diskSize = IoUtil.FormatBytes(IoUtil.DirectorySize(dir));
            }
            EditorGUILayout.LabelField("Sweeps", $"{m.Sweeps.Count}   floors {Math.Max(1, model.FloorCount)}   on disk {_diskSize}");
            EditorGUILayout.LabelField("Mesh", model.Geometry != null ? $"{model.Geometry.TriangleCount:N0} triangles, {model.Geometry.Parts.Count} textures" : "none (panorama-only)");
            var cal = model.Calibration;
            EditorGUILayout.LabelField("Alignment", cal.Method + (double.IsNaN(cal.Score) ? "" : $" (score {cal.Score:0.00}, margin {cal.Margin:0.00})"));
        }

        void DrawSweeps(MatterportSpace space)
        {
            var sweeps = space.Model.Sweeps;
            _showSweeps = EditorGUILayout.Foldout(_showSweeps, $"Sweeps ({sweeps.Count})", true);
            if (!_showSweeps) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                _filter = EditorGUILayout.TextField("Filter", _filter);
                _showLabels = GUILayout.Toggle(_showLabels, "Scene labels", EditorStyles.miniButton, GUILayout.Width(90));
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(260));
            for (int i = 0; i < sweeps.Count; i++)
            {
                var s = sweeps[i];
                var label = $"#{i}  {s.Label}  floor {s.Floor}";
                if (_filter.Length > 0 && (label + s.Id).IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(label + (i == space.startSweep ? "   (start)" : ""), s.Id), GUILayout.MinWidth(120));
                    if (GUILayout.Button(new GUIContent("Look", "Move the Scene view camera here (in Play mode: go there)"), EditorStyles.miniButtonLeft, GUILayout.Width(40))) LookFrom(space, i);
                    if (GUILayout.Button(new GUIContent("▶", "Enter Play mode at this sweep"), EditorStyles.miniButtonMid, GUILayout.Width(26))) PlayFrom(space, i);
                    if (GUILayout.Button(new GUIContent("Start", "Make this the start sweep"), EditorStyles.miniButtonRight, GUILayout.Width(40)))
                    {
                        Undo.RecordObject(space, "Set start sweep");
                        space.startSweep = i;
                        EditorUtility.SetDirty(space);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        static void LookFrom(MatterportSpace space, int i)
        {
            if (Application.isPlaying)
            {
                Navigator()?.GoTo(i);
                return;
            }
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
            sv.orthographic = false;
            sv.LookAtDirect(space.SweepWorldPosition(i), Quaternion.Euler(0, sv.rotation.eulerAngles.y, 0), 0.01f);
            sv.Repaint();
        }

        static void PlayFrom(MatterportSpace space, int i)
        {
            if (Application.isPlaying)
            {
                LookFrom(space, i);
                return;
            }
            SessionState.SetInt(MatterportSpace.PlayFromSweepKey, i);
            EditorApplication.isPlaying = true;
        }

        void OnSceneGUI()
        {
            var space = (MatterportSpace)target;
            if (!_showLabels || space.Model == null || Application.isPlaying) return;
            var style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.white }, alignment = TextAnchor.MiddleCenter };
            for (int i = 0; i < space.Model.Sweeps.Count; i++)
            {
                var p = space.SweepWorldPosition(i);
                Handles.color = i == space.startSweep ? new Color(1f, 0.8f, 0.2f) : new Color(0.2f, 0.8f, 1f);
                float size = HandleUtility.GetHandleSize(p) * 0.08f;
                if (Handles.Button(p, Quaternion.identity, size, size * 1.2f, Handles.SphereHandleCap)) LookFrom(space, i);
                Handles.Label(p + Vector3.up * 0.25f, i.ToString(), style);
            }
        }
    }
}
