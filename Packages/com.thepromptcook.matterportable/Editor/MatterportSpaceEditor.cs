using System;
using System.Threading;
using Matterportable.Core;
using UnityEditor;
using UnityEngine;

namespace Matterportable.Editor
{
    [CustomEditor(typeof(MatterportSpace))]
    public sealed class MatterportSpaceEditor : UnityEditor.Editor
    {
        SerializedProperty _link, _cacheFolder, _settings, _startSweep, _downloadIfMissing, _preview;
        bool _showSweeps = true, _showAlignment, _showLabels = true;
        string _filter = "";
        Vector2 _scroll;
        string _diskSize;
        string _diskSizeFor;
        static bool _aligning;

        static readonly string[] AxisNames = { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };

        void OnEnable()
        {
            _link = serializedObject.FindProperty(nameof(MatterportSpace.link));
            _cacheFolder = serializedObject.FindProperty(nameof(MatterportSpace.cacheFolder));
            _settings = serializedObject.FindProperty(nameof(MatterportSpace.settings));
            _startSweep = serializedObject.FindProperty(nameof(MatterportSpace.startSweep));
            _downloadIfMissing = serializedObject.FindProperty(nameof(MatterportSpace.downloadIfMissing));
            _preview = serializedObject.FindProperty(nameof(MatterportSpace.previewInEditMode));
        }

        public override void OnInspectorGUI()
        {
            var space = (MatterportSpace)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(_link);
            bool valid = MatterportLink.TryParseModelId(_link.stringValue, out var modelId);
            if (!string.IsNullOrWhiteSpace(_link.stringValue) && !valid)
                EditorGUILayout.HelpBox("No Matterport model ID found in this link.", MessageType.Warning);
            EditorGUILayout.PropertyField(_settings);
            EditorGUILayout.PropertyField(_startSweep);
            EditorGUILayout.PropertyField(_downloadIfMissing);
            EditorGUILayout.PropertyField(_preview);
            EditorGUILayout.PropertyField(_cacheFolder);
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(4);
            if (Application.isPlaying) DrawPlayMode(space);
            else DrawEditMode(space, valid, modelId);

            if (space.Manifest != null)
            {
                DrawInfo(space);
                DrawSweeps(space);
                DrawAlignment(space);
            }
        }

        void DrawEditMode(MatterportSpace space, bool valid, string modelId)
        {
            string dir = valid ? MatterportPaths.SpaceDir(space.cacheFolder, modelId) : null;
            bool cached = dir != null && SpaceManifest.Exists(dir);
            if (!string.IsNullOrEmpty(space.Error)) EditorGUILayout.HelpBox(space.Error, MessageType.Error);
            else if (valid && !cached) EditorGUILayout.HelpBox("Not downloaded yet. Download now, or just press Play (it downloads automatically when 'Download If Missing' is on).", MessageType.Info);
            else if (!string.IsNullOrEmpty(space.Status)) EditorGUILayout.HelpBox(space.Status, MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!valid))
                    if (GUILayout.Button(cached ? "Update Download" : "Download")) MatterportImportWindow.OpenAndDownload(space.link, space.cacheFolder);
                using (new EditorGUI.DisabledScope(!cached))
                {
                    if (GUILayout.Button("Rebuild Preview")) space.BuildEditorPreview();
                    if (GUILayout.Button("Open Folder")) MatterportSceneBuilder.Reveal(dir);
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
                var cal = await MatterportSceneBuilder.AlignAsync(dir, UnityLog.Instance, CancellationToken.None, force: true);
                Debug.Log($"[Matterportable] Alignment: {cal.Method}, score {cal.Score:0.00}, rotation {cal.Rotation}.");
                if (space != null) space.Reload();
            }
            catch (Exception e)
            {
                Debug.LogError("[Matterportable] Alignment failed: " + e.Message);
            }
            finally
            {
                _aligning = false;
                Repaint();
            }
        }

        void DrawPlayMode(MatterportSpace space)
        {
            if (!string.IsNullOrEmpty(space.Error)) EditorGUILayout.HelpBox(space.Error, MessageType.Error);
            else EditorGUILayout.HelpBox(space.Status, MessageType.None);
            if (!space.IsLoaded) return;
            var nav = FindNavigator();
            if (nav != null)
            {
                EditorGUILayout.LabelField("Current sweep", nav.CurrentSweep + (nav.IsMoving ? "  (moving)" : ""));
                using (new EditorGUILayout.HorizontalScope())
                {
                    foreach (MatterportNavigator.ViewMode m in Enum.GetValues(typeof(MatterportNavigator.ViewMode)))
                        if (GUILayout.Toggle(nav.Mode == m, m.ToString(), EditorStyles.miniButton) && nav.Mode != m) nav.SetMode(m);
                }
                nav.AutoTour = EditorGUILayout.Toggle("Auto tour", nav.AutoTour);
            }
            space.DebugView = EditorGUILayout.Popup("Render view", space.DebugView, new[] { "Final", "Mesh texture", "Panorama only", "Visibility A/B", "Blend weights" });
            if (GUILayout.Button("Reload Space")) space.Reload();
            Repaint();
        }

        static MatterportNavigator FindNavigator()
        {
#if UNITY_2023_1_OR_NEWER
            return FindAnyObjectByType<MatterportNavigator>();
#else
            return FindObjectOfType<MatterportNavigator>();
#endif
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
            if (cal != null)
                EditorGUILayout.LabelField("Alignment", cal.Method + (double.IsNaN(cal.Score) ? "" : $" (score {cal.Score:0.00}, margin {cal.Margin:0.00})"));
            if (m.Warnings.Count > 0)
                EditorGUILayout.HelpBox(string.Join("\n", m.Warnings.GetRange(0, Math.Min(6, m.Warnings.Count))) + (m.Warnings.Count > 6 ? "\n…" : ""), MessageType.Warning);
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
                if (!string.IsNullOrEmpty(_filter) && label.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (s.Id ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(label + (i == space.startSweep ? "   (start)" : ""), s.Id), GUILayout.MinWidth(120));
                    if (GUILayout.Button(new GUIContent("Look", "Move the Scene view camera to this sweep"), EditorStyles.miniButtonLeft, GUILayout.Width(40)))
                        LookFrom(space, i);
                    if (GUILayout.Button(new GUIContent("▶", "Enter Play mode at this sweep"), EditorStyles.miniButtonMid, GUILayout.Width(26)))
                        PlayFrom(space, i);
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
                FindNavigator()?.GoTo(i);
                return;
            }
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
            var yaw = sv.rotation.eulerAngles.y;
            sv.orthographic = false;
            sv.LookAtDirect(space.SweepWorldPosition(i), Quaternion.Euler(0, yaw, 0), 0.01f);
            sv.Repaint();
        }

        static void PlayFrom(MatterportSpace space, int i)
        {
            if (Application.isPlaying)
            {
                FindNavigator()?.Teleport(i);
                return;
            }
            SessionState.SetInt(MatterportSpace.PlayFromSweepKey, i);
            EditorApplication.isPlaying = true;
        }

        void DrawAlignment(MatterportSpace space)
        {
            _showAlignment = EditorGUILayout.Foldout(_showAlignment, "Alignment (advanced)", true);
            if (!_showAlignment || space.Calibration == null) return;
            EditorGUILayout.HelpBox("Normally solved automatically. Change these only if panoramas look rotated or mirrored; " +
                                    "edits are saved to calibration.json as 'manual' and apply live in Play mode.", MessageType.None);
            var cal = space.Calibration.Clone();
            bool geometryChanged = false;
            EditorGUI.BeginChangeCheck();
            cal.Rotation = (RotationMode)EditorGUILayout.EnumPopup("Sweep rotation", cal.Rotation);
            var meshFrame = (CoordinateFrame)EditorGUILayout.EnumPopup("Mesh frame", cal.MeshFrame);
            var sweepFrame = (CoordinateFrame)EditorGUILayout.EnumPopup("Sweep frame", cal.SweepFrame);
            bool flipV = EditorGUILayout.Toggle("Flip texture V", cal.FlipTextureV);
            bool flipW = EditorGUILayout.Toggle("Flip winding", cal.FlipWinding);
            geometryChanged = meshFrame != cal.MeshFrame || sweepFrame != cal.SweepFrame || flipV != cal.FlipTextureV || flipW != cal.FlipWinding;
            cal.MeshFrame = meshFrame;
            cal.SweepFrame = sweepFrame;
            cal.FlipTextureV = flipV;
            cal.FlipWinding = flipW;
            EditorGUILayout.LabelField("Face images (forward / right / up in panorama space)", EditorStyles.miniBoldLabel);
            for (int f = 0; f < 6; f++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("face " + f, GUILayout.Width(50));
                    cal.Faces[f].Forward = (SignedAxis)EditorGUILayout.Popup((int)cal.Faces[f].Forward, AxisNames);
                    cal.Faces[f].Right = (SignedAxis)EditorGUILayout.Popup((int)cal.Faces[f].Right, AxisNames);
                    cal.Faces[f].Up = (SignedAxis)EditorGUILayout.Popup((int)cal.Faces[f].Up, AxisNames);
                }
            }
            bool layoutValid = Array.TrueForAll(cal.Faces, b => b.IsValid) && CubeFaces.TryBuildLookup(cal.Faces, out _, out _, out _);
            if (!layoutValid) EditorGUILayout.HelpBox("Each face needs three different axes and the six faces must look in six different directions.", MessageType.Warning);
            if (EditorGUI.EndChangeCheck() && layoutValid) Commit(space, cal, geometryChanged);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset to Matterport default"))
                {
                    var d = new SpaceCalibration { MeshFrame = space.Calibration.MeshFrame, SweepFrame = space.Calibration.SweepFrame };
                    Commit(space, d, false);
                }
                if (GUILayout.Button("Rotate faces 90° (yaw)"))
                {
                    var r = space.Calibration.Clone();
                    for (int f = 0; f < 6; f++) r.Faces[f] = RotateYaw(r.Faces[f]);
                    Commit(space, r, false);
                }
            }
        }

        static FaceBasis RotateYaw(FaceBasis b)
        {
            SignedAxis Rot(SignedAxis a)
            {
                switch (a)
                {
                    case SignedAxis.PosX: return SignedAxis.NegZ;
                    case SignedAxis.NegZ: return SignedAxis.NegX;
                    case SignedAxis.NegX: return SignedAxis.PosZ;
                    case SignedAxis.PosZ: return SignedAxis.PosX;
                    default: return a;
                }
            }
            return new FaceBasis(Rot(b.Forward), Rot(b.Right), Rot(b.Up));
        }

        static void Commit(MatterportSpace space, SpaceCalibration cal, bool geometryChanged)
        {
            cal.Method = "manual";
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
