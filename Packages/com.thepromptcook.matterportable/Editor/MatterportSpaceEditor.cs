using System;
using System.Threading;
using Matterportable.Core;
using UnityEditor;
using UnityEngine;

namespace Matterportable.Editor
{
    /// <summary>MatterportSpace inspector: download/preview actions, live Play-mode controls, sweep list, alignment fixes.</summary>
    [CustomEditor(typeof(MatterportSpace))]
    public sealed class MatterportSpaceEditor : UnityEditor.Editor
    {
        bool _showSweeps = true, _showAlignment, _showLabels = true;
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

            if (space.Manifest == null) return;
            DrawInfo(space);
            DrawSweeps(space);
            DrawAlignment(space);
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
                using (new EditorGUI.DisabledScope(!cached || _aligning || space.Manifest?.Mesh == null))
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
            if (!space.IsLoaded) return;
            var nav = MpUtil.FindFirst<MatterportNavigator>();
            if (nav != null)
            {
                EditorGUILayout.LabelField("Current sweep", nav.CurrentSweep + (nav.IsMoving ? "  (moving)" : ""));
                using (new EditorGUILayout.HorizontalScope())
                    foreach (MatterportNavigator.ViewMode m in Enum.GetValues(typeof(MatterportNavigator.ViewMode)))
                        if (GUILayout.Toggle(nav.Mode == m, m.ToString(), EditorStyles.miniButton) && nav.Mode != m) nav.SetMode(m);
                nav.AutoTour = EditorGUILayout.Toggle("Auto tour", nav.AutoTour);
            }
            space.DebugView = EditorGUILayout.Popup("Render view", space.DebugView, MatterportSpace.DebugViewNames);
            if (GUILayout.Button("Reload Space")) space.Reload();
            if (space.SpaceDir != null && GUILayout.Button("Copy Diagnostics")) MatterportEditorTools.CopyDiagnostics(space.SpaceDir);
            Repaint();
        }

        void DrawInfo(MatterportSpace space)
        {
            var m = space.Manifest;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(m.Name ?? m.ModelId, EditorStyles.boldLabel);
            if (_diskSizeFor != space.SpaceDir)
            {
                _diskSizeFor = space.SpaceDir;
                _diskSize = IoUtil.FormatBytes(IoUtil.DirectorySize(space.SpaceDir));
            }
            EditorGUILayout.LabelField("Sweeps", $"{m.Sweeps.Count}   floors {Math.Max(1, space.FloorCount)}   on disk {_diskSize}");
            EditorGUILayout.LabelField("Mesh", space.Geometry != null ? $"{space.Geometry.TriangleCount:N0} triangles, {space.Geometry.Parts.Count} textures" : "none (panorama-only)");
            var cal = space.Calibration;
            if (cal != null) EditorGUILayout.LabelField("Alignment", cal.Method + (double.IsNaN(cal.Score) ? "" : $" (score {cal.Score:0.00}, margin {cal.Margin:0.00})"));
        }

        void DrawSweeps(MatterportSpace space)
        {
            _showSweeps = EditorGUILayout.Foldout(_showSweeps, $"Sweeps ({space.Sweeps.Count})", true);
            if (!_showSweeps) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                _filter = EditorGUILayout.TextField("Filter", _filter);
                _showLabels = GUILayout.Toggle(_showLabels, "Scene labels", EditorStyles.miniButton, GUILayout.Width(90));
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(260));
            for (int i = 0; i < space.Sweeps.Count; i++)
            {
                var s = space.Sweeps[i];
                var label = $"#{i}  {s.Label}  floor {s.Floor}";
                if (_filter.Length > 0 && (label + s.Id).IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(label + (i == space.startSweep ? "   (start)" : ""), s.Id), GUILayout.MinWidth(120));
                    if (GUILayout.Button(new GUIContent("Look", "Move the Scene view camera here (in Play mode: go there)"), EditorStyles.miniButtonLeft, GUILayout.Width(40))) LookFrom(space, i);
                    if (GUILayout.Button(new GUIContent("▶", "Enter Play mode at this sweep"), EditorStyles.miniButtonMid, GUILayout.Width(26))) PlayFrom(i);
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
                MpUtil.FindFirst<MatterportNavigator>()?.GoTo(i);
                return;
            }
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
            sv.orthographic = false;
            sv.LookAtDirect(space.SweepWorldPosition(i), Quaternion.Euler(0, sv.rotation.eulerAngles.y, 0), 0.01f);
            sv.Repaint();
        }

        static void PlayFrom(int i)
        {
            if (Application.isPlaying)
            {
                MpUtil.FindFirst<MatterportNavigator>()?.Teleport(i);
                return;
            }
            SessionState.SetInt(MatterportSpace.PlayFromSweepKey, i);
            EditorApplication.isPlaying = true;
        }

        /// <summary>Escape hatch when automatic alignment is wrong; full per-face control lives in calibration.json.</summary>
        void DrawAlignment(MatterportSpace space)
        {
            _showAlignment = EditorGUILayout.Foldout(_showAlignment, "Alignment fixes", true);
            if (!_showAlignment || space.Calibration == null) return;
            EditorGUILayout.HelpBox("Only needed if panoramas look rotated or mirrored. Changes are saved to calibration.json as 'manual' " +
                                    "and apply live in Play mode; per-face settings can be edited in that file.", MessageType.None);
            var cal = space.Calibration.Clone();
            EditorGUI.BeginChangeCheck();
            cal.Rotation = (RotationMode)EditorGUILayout.EnumPopup("Sweep rotation", cal.Rotation);
            cal.FlipWinding = EditorGUILayout.Toggle("Flip winding", cal.FlipWinding);
            cal.FlipTextureV = EditorGUILayout.Toggle("Flip texture V", cal.FlipTextureV);
            bool geometryChanged = cal.FlipWinding != space.Calibration.FlipWinding || cal.FlipTextureV != space.Calibration.FlipTextureV;
            if (EditorGUI.EndChangeCheck()) Commit(space, cal, geometryChanged);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rotate panoramas 90°"))
                {
                    var r = space.Calibration.Clone();
                    for (int f = 0; f < 6; f++) r.Faces[f] = RotateYaw(r.Faces[f]);
                    Commit(space, r, false);
                }
                if (GUILayout.Button("Reset to default"))
                    Commit(space, new SpaceCalibration { MeshFrame = space.Calibration.MeshFrame, SweepFrame = space.Calibration.SweepFrame, FlipWinding = space.Calibration.FlipWinding }, false);
            }
        }

        static FaceBasis RotateYaw(FaceBasis b)
        {
            SignedAxis Rot(SignedAxis a) =>
                a == SignedAxis.PosX ? SignedAxis.NegZ : a == SignedAxis.NegZ ? SignedAxis.NegX : a == SignedAxis.NegX ? SignedAxis.PosZ : a == SignedAxis.PosZ ? SignedAxis.PosX : a;
            return new FaceBasis(Rot(b.Forward), Rot(b.Right), Rot(b.Up));
        }

        static void Commit(MatterportSpace space, SpaceCalibration cal, bool geometryChanged)
        {
            cal.Method = SpaceCalibration.Manual;
            if (!string.IsNullOrEmpty(space.SpaceDir)) cal.Save(space.SpaceDir);
            if (Application.isPlaying && !geometryChanged) space.SetCalibration(cal, false);
            else space.Reload();
        }

        void OnSceneGUI()
        {
            var space = (MatterportSpace)target;
            if (!_showLabels || space.Sweeps.Count == 0 || Application.isPlaying) return;
            var style = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.white }, alignment = TextAnchor.MiddleCenter };
            for (int i = 0; i < space.Sweeps.Count; i++)
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
