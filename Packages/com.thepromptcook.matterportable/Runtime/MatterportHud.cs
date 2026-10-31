using System.Text;
using Matterportable.Core;
using UnityEngine;

namespace Matterportable
{
    /// <summary>
    /// Zero-setup on-screen UI (IMGUI): loading status, view-mode buttons, floor selector, key hints,
    /// F1 statistics for profiling/iteration and F3 sweep labels. Put it next to the navigator.
    /// </summary>
    [RequireComponent(typeof(MatterportNavigator))]
    [AddComponentMenu("Matterportable/Matterport HUD")]
    public sealed class MatterportHud : MonoBehaviour
    {
        public bool showModeButtons = true;
        public bool showHints = true;
        public bool showStats;
        public bool showSweepLabels;
        [Tooltip("Seconds the key hint stays visible after loading (0 = always).")]
        public float hintSeconds = 12f;

        MatterportNavigator _nav;
        Camera _cam;
        float _fps, _frameMs;
        float _shownAt;
        GUIStyle _panel, _label, _small, _button, _buttonOn, _center;
        Texture2D _panelTex, _buttonTex, _buttonOnTex;

        static readonly string[] DebugViews = { "Final", "Mesh texture", "Panorama only", "Visibility A/B", "Blend weights" };

        void Awake()
        {
            _nav = GetComponent<MatterportNavigator>();
            _cam = GetComponent<Camera>();
            _shownAt = Time.unscaledTime;
        }

        void OnDestroy()
        {
            MpUtil.SafeDestroy(_panelTex);
            MpUtil.SafeDestroy(_buttonTex);
            MpUtil.SafeDestroy(_buttonOnTex);
        }

        void Update()
        {
            NavInput.BeginFrame();
            if (NavInput.KeyDown(KeyCode.F1)) showStats = !showStats;
            if (NavInput.KeyDown(KeyCode.F3)) showSweepLabels = !showSweepLabels;
            if (NavInput.KeyDown(KeyCode.H))
            {
                showHints = !showHints;
                _shownAt = Time.unscaledTime;
            }
            float dt = Mathf.Max(1e-5f, Time.unscaledDeltaTime);
            _fps = Mathf.Lerp(_fps, 1f / dt, 0.05f);
            _frameMs = Mathf.Lerp(_frameMs, dt * 1000f, 0.05f);
        }

        void EnsureStyles()
        {
            if (_panel != null) return;
            _panelTex = Solid(new Color(0.08f, 0.08f, 0.09f, 0.78f));
            _buttonTex = Solid(new Color(0.12f, 0.12f, 0.13f, 0.85f));
            _buttonOnTex = Solid(new Color(0.95f, 0.95f, 0.95f, 0.95f));
            _panel = new GUIStyle(GUI.skin.box) { normal = { background = _panelTex }, padding = new RectOffset(10, 10, 8, 8) };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = Color.white }, richText = true, wordWrap = true };
            _small = new GUIStyle(_label) { fontSize = 11 };
            _center = new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter };
            _button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                normal = { background = _buttonTex, textColor = Color.white },
                hover = { background = _buttonTex, textColor = new Color(0.8f, 0.9f, 1f) },
                active = { background = _buttonOnTex, textColor = Color.black },
                padding = new RectOffset(10, 10, 6, 6),
            };
            _buttonOn = new GUIStyle(_button)
            {
                normal = { background = _buttonOnTex, textColor = Color.black },
                hover = { background = _buttonOnTex, textColor = Color.black },
            };
        }

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        void OnGUI()
        {
            EnsureStyles();
            if (Event.current.type == EventType.Layout) _nav.BlockedGuiRects.Clear();
            var space = _nav.space;
            if (space == null)
            {
                Panel(new Rect(12, 12, 420, 46), "No MatterportSpace in the scene. Add one via Window ▸ Matterportable ▸ Import.");
                return;
            }

            if (!space.IsLoaded)
            {
                var text = space.Error != null ? "<b>Matterportable</b>\n" + space.Error : "<b>Loading Matterport space…</b>\n" + space.Status;
                var r = new Rect(12, 12, 460, space.Error != null ? 90 : 64);
                Panel(r, text);
                if (space.Error == null) DrawBar(new Rect(r.x + 10, r.yMax - 12, r.width - 20, 4), space.Progress);
                return;
            }

            if (showModeButtons) DrawModeButtons(space);
            if (showHints && (hintSeconds <= 0 || Time.unscaledTime - _shownAt < hintSeconds)) DrawHints();
            if (showStats) DrawStats(space);
            if (showSweepLabels) DrawSweepLabels(space);
            if (_nav.IsWaitingForPanorama)
                GUI.Label(new Rect(Screen.width / 2f - 60, Screen.height / 2f + 30, 120, 22), "Loading…", _center);
        }

        void DrawModeButtons(MatterportSpace space)
        {
            float x = 12, y = Screen.height - 46, h = 32;
            var modes = new[] { (MatterportNavigator.ViewMode.Inside, "Inside"), (MatterportNavigator.ViewMode.Dollhouse, "Dollhouse"), (MatterportNavigator.ViewMode.Floorplan, "Floor plan") };
            foreach (var (mode, label) in modes)
            {
                var r = new Rect(x, y, label.Length * 8 + 26, h);
                Block(r);
                if (GUI.Button(r, label, _nav.Mode == mode ? _buttonOn : _button)) _nav.SetMode(mode);
                x = r.xMax + 4;
            }
            if (_nav.Mode != MatterportNavigator.ViewMode.Inside && space.FloorCount > 1)
            {
                x += 16;
                var all = new Rect(x, y, 44, h);
                Block(all);
                if (GUI.Button(all, "All", _nav.SelectedFloor < 0 ? _buttonOn : _button)) _nav.SetFloor(-1);
                x = all.xMax + 4;
                for (int f = 0; f < space.FloorCount; f++)
                {
                    var label = FloorLabel(space, f);
                    var r = new Rect(x, y, Mathf.Max(32, label.Length * 8 + 16), h);
                    Block(r);
                    if (GUI.Button(r, label, _nav.SelectedFloor == f ? _buttonOn : _button)) _nav.SetFloor(f);
                    x = r.xMax + 4;
                }
            }
            if (_nav.AutoTour)
            {
                var r = new Rect(x + 12, y, 90, h);
                Block(r);
                if (GUI.Button(r, "■ Tour", _buttonOn)) _nav.AutoTour = false;
            }
        }

        static string FloorLabel(MatterportSpace space, int f)
        {
            if (space.Manifest != null && f < space.Manifest.Floors.Count && !string.IsNullOrEmpty(space.Manifest.Floors[f].Label))
            {
                var l = space.Manifest.Floors[f].Label;
                return l.Length > 14 ? l.Substring(0, 14) : l;
            }
            return (f + 1).ToString();
        }

        void DrawHints()
        {
            const string hint = "Drag to look · Click to move · W A S D step · ←/→ turn · Wheel zoom · 1/2/3 Inside/Dollhouse/Floor plan · " +
                                "[ ] prev/next · P tour · F1 stats · F2 render view · F3 labels · H hide";
            var size = _small.CalcSize(new GUIContent(hint));
            float w = Mathf.Min(size.x + 24, Screen.width - 24);
            var r = new Rect((Screen.width - w) / 2f, 12, w, 30);
            GUI.Box(r, GUIContent.none, _panel);
            GUI.Label(new Rect(r.x + 12, r.y + 7, r.width - 24, 20), hint, _small);
        }

        void DrawStats(MatterportSpace space)
        {
            var sb = new StringBuilder();
            sb.Append("<b>").Append(space.Manifest?.Name).Append("</b>  (").Append(space.ModelId).Append(")\n");
            sb.AppendFormat("{0:0} fps  {1:0.0} ms\n", _fps, _frameMs);
            int cur = _nav.CurrentSweep;
            if (cur >= 0 && cur < space.Sweeps.Count)
            {
                var sw = space.Sweeps[cur];
                sb.AppendFormat("Sweep {0}/{1}  id {2}  floor {3}{4}\n", cur, space.Sweeps.Count - 1, sw.Id, sw.Floor,
                    string.IsNullOrEmpty(sw.Label) ? "" : "  \"" + sw.Label + "\"");
            }
            sb.AppendFormat("Mode {0}{1}{2}  FOV {3:0}°\n", _nav.Mode, _nav.IsMoving ? "  (moving)" : "", _nav.IsChangingMode ? "  (changing view)" : "", _cam.fieldOfView);
            var cache = space.Panoramas;
            if (cache != null)
            {
                int loaded = 0, depth = 0;
                foreach (var e in cache.Entries)
                {
                    if (e.Faces != null) loaded++;
                    if (e.Depth != null) depth++;
                }
                var curEntry = cur >= 0 ? cache.Get(cur) : null;
                sb.AppendFormat("Panoramas: {0} cached ({1}), {2} depth maps, loading {3}, queued {4}\n", loaded,
                    IoUtil.FormatBytes(cache.BytesUsed), depth, cache.ActiveLoads, cache.Queued);
                if (curEntry != null) sb.AppendFormat("Current panorama: {0}px, loaded in {1:0.00}s\n", curEntry.Size, curEntry.LoadSeconds);
            }
            if (space.Geometry != null)
                sb.AppendFormat("Mesh: {0:N0} triangles, {1} materials, textures {2}/{3}\n", space.Geometry.TriangleCount,
                    space.Geometry.Parts.Count, space.TexturesLoaded, space.Manifest?.Textures.Count ?? 0);
            else sb.Append("Mesh: none (panorama-only)\n");
            var cal = space.Calibration;
            if (cal != null)
                sb.AppendFormat("Alignment: {0}{1}  rotation {2}\n", cal.Method,
                    double.IsNaN(cal.Score) ? "" : $" (score {cal.Score:0.00})", cal.Rotation);
            sb.AppendFormat("Render view (F2): {0}\n", DebugViews[space.DebugView]);
            sb.AppendFormat("Loaded in {0:0.0}s", space.LastLoadSeconds);
            var content = new GUIContent(sb.ToString());
            float w = 430;
            float h = _small.CalcHeight(content, w - 20) + 16;
            var r = new Rect(12, 50, w, h);
            Block(r);
            GUI.Box(r, GUIContent.none, _panel);
            GUI.Label(new Rect(r.x + 10, r.y + 8, w - 20, h - 16), content, _small);
        }

        void DrawSweepLabels(MatterportSpace space)
        {
            for (int i = 0; i < space.Sweeps.Count; i++)
            {
                var sp = _cam.WorldToScreenPoint(space.MarkerWorldPosition(i) + Vector3.up * 0.25f);
                if (sp.z <= 0 || sp.x < 0 || sp.x > Screen.width || sp.y < 0 || sp.y > Screen.height) continue;
                var r = new Rect(sp.x - 18, Screen.height - sp.y - 10, 36, 20);
                GUI.Box(r, GUIContent.none, _panel);
                GUI.Label(r, i == _nav.CurrentSweep ? "<b>" + i + "</b>" : i.ToString(), _center);
            }
        }

        void Panel(Rect r, string text)
        {
            Block(r);
            GUI.Box(r, GUIContent.none, _panel);
            GUI.Label(new Rect(r.x + 10, r.y + 8, r.width - 20, r.height - 16), text, _label);
        }

        void DrawBar(Rect r, float t)
        {
            GUI.DrawTexture(r, _buttonTex);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(t), r.height), _buttonOnTex);
        }

        void Block(Rect r)
        {
            if (Event.current.type == EventType.Repaint || Event.current.type == EventType.Layout) _nav.BlockedGuiRects.Add(r);
        }
    }
}
