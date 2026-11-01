using Matterportable.Core;
using UnityEngine;

namespace Matterportable
{
    /// <summary>
    /// Matterport's measurement mode (mirrors the Godot MatterportMeasure; the behaviour lives in Core
    /// <see cref="MeasureTool"/>). Toggle with the HUD's Measure button or M. Click to place points, Escape or Confirm
    /// ends a line, Escape again leaves the mode; Shift locks to an axis, Alt disables snapping, Delete removes the
    /// selected measurement. Draws lines, points, labels and the + cursor in screen space, faded where hidden.
    /// </summary>
    [RequireComponent(typeof(MatterportNavigator))]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)] // after the navigator, which reports this frame's click
    [AddComponentMenu("Matterportable/Matterport Measure")]
    public sealed class MatterportMeasure : MonoBehaviour
    {
        public bool Active { get; private set; }
        public MeasureTool Tool { get; private set; }

        static int _activeCount, _escapeFrame = -1;

        /// <summary>True while measure mode is open, or Escape was used by it this frame (the launcher then keeps
        /// Escape away from its menu).</summary>
        public static bool OwnsEscape => _activeCount > 0 || _escapeFrame == Time.frameCount;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _activeCount = 0;
            _escapeFrame = -1;
        }

        readonly MeasureOverlay _items = new MeasureOverlay();
        MatterportNavigator _nav;
        SpaceGeometry _toolGeometry;
        static Texture2D _lineTex, _dotTex, _ringTex, _pillTex, _pillSelectedTex;
        static GUIStyle _pill, _pillSelected;

        void Awake() => _nav = GetComponent<MatterportNavigator>();

        void OnDisable() => SetActive(false);

        /// <summary>Enters or leaves measure mode. Measurements stay until the space is closed (they are temporary).</summary>
        public void SetActive(bool on)
        {
            var geometry = _nav != null && _nav.space != null && _nav.Core != null ? _nav.space.Model.Geometry : null;
            if (on && geometry == null) return;
            if (on == Active) return;
            Active = on;
            _activeCount += on ? 1 : -1;
            if (on && _toolGeometry != geometry)
            {
                Tool = new MeasureTool(geometry);
                _toolGeometry = geometry;
            }
            if (!on) Tool?.Finish();
            if (_nav != null && _nav.Core != null) _nav.Core.Measuring = on; // the navigator then leaves clicks to us
        }

        void Update()
        {
            NavInput.BeginFrame();
            if (NavInput.KeyDown(KeyCode.M)) SetActive(!Active);
            if (!Active) return;
            var nav = _nav.Core;
            if (nav == null)
            {
                SetActive(false);
                return;
            }
            if (NavInput.KeyDown(KeyCode.Escape))
            {
                _escapeFrame = Time.frameCount;
                if (!Tool.Escape()) SetActive(false);
                if (!Active) return;
            }
            if (NavInput.KeyDown(KeyCode.Delete) || NavInput.KeyDown(KeyCode.Backspace)) Tool.DeleteSelected();
            if (NavInput.KeyDown(KeyCode.Return) || NavInput.KeyDown(KeyCode.KeypadEnter)) Tool.Finish();

            var mouse = NavInput.MousePosition;
            var (origin, direction) = _nav.PointerRay(mouse);
            Tool.Update(new MeasureInput
            {
                RayOrigin = origin,
                RayDirection = direction,
                PixelAngle = nav.Fov * Mathf.Deg2Rad / Mathf.Max(1, Screen.height),
                AxisLock = NavInput.Key(KeyCode.LeftShift) || NavInput.Key(KeyCode.RightShift),
                NoSnap = NavInput.Key(KeyCode.LeftAlt) || NavInput.Key(KeyCode.RightAlt),
                PointerBlocked = !_nav.PointerOverView(mouse) || nav.IsChangingMode,
                View = nav.Mode,
                ClipHeight = nav.ViewClipHeight,
            });
            if (nav.Clicked) Tool.Click();
        }

        // ------------------------------------------------------------------ drawing (IMGUI, screen space)

        void OnGUI()
        {
            if (!Active || Tool == null || _nav.Core == null) return;
            GUI.depth = 1; // behind the HUD
            MpGui.Ensure();
            EnsureStyles();
            if (Event.current.type == EventType.Repaint) DrawScene();
            DrawPanel();
        }

        /// <summary>Draws the Core's overlay items (lines, dots, rings, labels) with IMGUI.</summary>
        void DrawScene()
        {
            var nav = _nav.Core;
            Tool.BuildOverlay(nav.CameraPosition, nav.CameraRotation, nav.Fov, Screen.width, Screen.height, _items);
            foreach (var it in _items.Items)
            {
                var at = new Vector2(it.X, it.Y);
                if (it.Kind == MeasureOverlay.Kind.Line) Line(at, new Vector2(it.X2, it.Y2), it.Size, it.Color.ToUnity());
                else if (it.Kind != MeasureOverlay.Kind.Label) Texture(it.Kind == MeasureOverlay.Kind.Dot ? _dotTex : _ringTex, at, it.Size, it.Color.ToUnity());
                else
                {
                    var style = it.Selected ? _pillSelected : _pill;
                    var content = new GUIContent(it.Text);
                    float w = style.CalcSize(content).x;
                    GUI.Label(new Rect(at.x - w / 2, at.y - 11, w, 22), content, style);
                }
            }
        }

        static void Line(Vector2 a, Vector2 b, float width, Color color)
        {
            var d = b - a;
            float length = d.magnitude;
            if (length < 0.5f) return;
            var matrix = GUI.matrix;
            var tint = GUI.color;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            GUI.color = color;
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, length, width), _lineTex);
            GUI.matrix = matrix;
            GUI.color = tint;
        }

        static void Texture(Texture2D tex, Vector2 centre, float radius, Color color)
        {
            var tint = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(centre.x - radius, centre.y - radius, radius * 2, radius * 2), tex);
            GUI.color = tint;
        }

        // ------------------------------------------------------------------ panel (bottom right, like the Godot one)

        void DrawPanel()
        {
            const float w = 440, row = 26;
            int lines = Tool.Lines.Count;
            float h = 32 + 40 + lines * row + 40;
            var r = new Rect(Screen.width - w - 12, Screen.height - h - 12, w, h);
            MpGui.Block(_nav, r);
            GUI.Box(r, GUIContent.none, MpGui.Panel);
            float x = r.x + 10, y = r.y + 6;
            GUI.Label(new Rect(x, y, w - 20, 24), "<b>Measure</b>", MpGui.Label);
            y += 26;
            GUI.Label(new Rect(x, y, w - 20, 40), "Click to place points · Shift: lock to axis · Alt: no snapping\nEsc: finish line, again to exit · Del: delete selected", MpGui.Small);
            y += 40;
            for (int i = 0; i < lines; i++)
            {
                var line = Tool.Lines[i];
                var text = $"{i + 1}.  {Tool.Format(line.TotalLength)}" + (line.SegmentCount > 1 ? $"  ({line.SegmentCount} segments)" : "") + (line.FloorPlan ? "  · floor plan" : "");
                if (GUI.Button(new Rect(x, y, w - 56, row - 2), text, Tool.Selected == i ? MpGui.ButtonOn : MpGui.Button)) Tool.Selected = Tool.Selected == i ? -1 : i;
                if (GUI.Button(new Rect(r.xMax - 42, y, 32, row - 2), "✕", MpGui.Button))
                {
                    Tool.Delete(i);
                    break;
                }
                y += row;
            }
            y += 4;
            float bx = x;
            bool Button(string label, bool on, float width)
            {
                var b = new Rect(bx, y, width, 30);
                bx = b.xMax + 4;
                return GUI.Button(b, label, on ? MpGui.ButtonOn : MpGui.Button);
            }
            if (Button("Snapping", Tool.Snapping, 84)) Tool.Snapping = !Tool.Snapping;
            if (Button("Continuous lines", Tool.Continuous, 128)) Tool.Continuous = !Tool.Continuous;
            if (Button(Tool.Units == MeasureUnits.Imperial ? "Units: ft" : "Units: m", false, 76))
                Tool.Units = Tool.Units == MeasureUnits.Imperial ? MeasureUnits.Metric : MeasureUnits.Imperial;
            if (Tool.Draft != null && Button("✓ Confirm", false, 84)) Tool.Finish();
            if (Button("Done", false, 52)) SetActive(false);
        }

        static void EnsureStyles()
        {
            if (_pill != null && _pillTex != null) return;
            _lineTex = new Texture2D(1, 4, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            _lineTex.SetPixels(new[] { new Color(1, 1, 1, 0.3f), Color.white, Color.white, new Color(1, 1, 1, 0.3f) });
            _lineTex.Apply();
            _dotTex = Circle(32, 15f, 0f, Color.white);
            _ringTex = Circle(32, 15f, 12f, Color.white);
            _pillTex = Circle(20, 9.5f, 0f, new Color(1f, 1f, 1f, 0.95f));
            _pillSelectedTex = Circle(20, 9.5f, 0f, MeasureOverlay.Accent.ToUnity());
            _pill = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 2, 2),
                border = new RectOffset(10, 10, 10, 10),
                normal = { background = _pillTex, textColor = MeasureOverlay.Ink.ToUnity() },
            };
            _pillSelected = new GUIStyle(_pill) { normal = { background = _pillSelectedTex, textColor = Color.white } };
        }

        /// <summary>An anti-aliased disc (or ring when <paramref name="inner"/> &gt; 0) for dots, the snap ring and the
        /// rounded label background (9-sliced).</summary>
        static Texture2D Circle(int size, float outer, float inner, Color color)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[size * size];
            float c = (size - 1) / 2f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float a = Mathf.Clamp01(outer - d + 0.5f) * (inner > 0 ? Mathf.Clamp01(d - inner + 0.5f) : 1f);
                px[y * size + x] = new Color(color.r, color.g, color.b, color.a * a);
            }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
    }
}
