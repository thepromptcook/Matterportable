using Matterportable.Core;
using UnityEngine;

namespace Matterportable
{
    /// <summary>
    /// Zero-setup on-screen UI (IMGUI; mirrors the Godot Hud): loading status, view modes, floors, measure, tour, key
    /// hint and the F1 statistics (text from Core <see cref="ViewerText"/>). H hides it. Put it next to the navigator.
    /// </summary>
    [RequireComponent(typeof(MatterportNavigator))]
    [AddComponentMenu("Matterportable/Matterport HUD")]
    public sealed class MatterportHud : MonoBehaviour
    {
        public bool visible = true;
        public bool showStats;

        MatterportNavigator _nav;
        MatterportMeasure _measure;
        float _fps;

        void Awake()
        {
            _nav = GetComponent<MatterportNavigator>();
            _measure = GetComponent<MatterportMeasure>();
        }

        void Update()
        {
            NavInput.BeginFrame();
            if (NavInput.KeyDown(KeyCode.F1)) showStats = !showStats;
            if (NavInput.KeyDown(KeyCode.H)) visible = !visible;
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(1e-5f, Time.unscaledDeltaTime), 0.05f);
        }

        void OnGUI()
        {
            MpGui.Ensure();
            var space = _nav.space;
            var nav = _nav.Core;
            if (space == null)
            {
                Panel(new Rect(12, 12, 420, 46), "No MatterportSpace in the scene. Press Esc for the menu.");
                return;
            }
            if (nav == null)
            {
                var text = space.Error != null ? "<b>Matterportable</b>\n" + space.Error : "<b>Loading Matterport space…</b>\n" + space.Status;
                var r = new Rect(12, 12, 460, space.Error != null ? 90 : 64);
                Panel(r, text);
                if (space.Error != null) return;
                GUI.DrawTexture(new Rect(r.x + 10, r.yMax - 12, r.width - 20, 4), MpGui.BarBack);
                GUI.DrawTexture(new Rect(r.x + 10, r.yMax - 12, (r.width - 20) * Mathf.Clamp01(space.Progress), 4), MpGui.BarFill);
                return;
            }
            if (nav.IsWaitingForPanorama) GUI.Label(new Rect(Screen.width / 2f - 60, Screen.height / 2f + 30, 120, 22), "Loading…", MpGui.Center);
            if (!visible) return;
            DrawButtons(space, nav);
            var hint = new GUIContent(ViewerText.KeyHint);
            float w = Mathf.Min(MpGui.Small.CalcSize(hint).x + 24, Screen.width - 24);
            var bar = new Rect((Screen.width - w) / 2f, 12, w, 30);
            GUI.Box(bar, GUIContent.none, MpGui.Panel);
            GUI.Label(new Rect(bar.x + 12, bar.y + 7, bar.width - 24, 20), hint, MpGui.Small);
            if (!showStats) return;
            var stats = new GUIContent(ViewerText.Stats(space.Model, nav, space.Scheduler, _fps, space.LastLoadSeconds, space.TexturesLoaded));
            float h = MpGui.Small.CalcHeight(stats, 410) + 16;
            Panel(new Rect(12, 50, 430, h), stats, MpGui.Small);
        }

        void DrawButtons(MatterportSpace space, Navigator nav)
        {
            float x = 12, y = Screen.height - 46;
            bool Button(string label, bool on, float gap = 4)
            {
                var r = new Rect(x + gap - 4, y, Mathf.Max(36, label.Length * 8 + 26), 32);
                x = r.xMax + 4;
                MpGui.Block(_nav, r);
                return GUI.Button(r, label, on ? MpGui.ButtonOn : MpGui.Button);
            }
            if (Button("Inside", nav.Mode == ViewMode.Inside)) nav.SetMode(ViewMode.Inside);
            if (Button("Dollhouse", nav.Mode == ViewMode.Dollhouse)) nav.SetMode(ViewMode.Dollhouse);
            if (Button("Floor plan", nav.Mode == ViewMode.Floorplan)) nav.SetMode(ViewMode.Floorplan);
            bool mesh = space.Model.Geometry != null; // walking and measuring need it
            if (mesh && Button("Walk", nav.Mode == ViewMode.Walk)) nav.SetMode(ViewMode.Walk);
            if (_measure != null && mesh && Button("Measure", _measure.Active, 12)) _measure.SetActive(!_measure.Active);
            if (Button(nav.AutoTour ? "■ Tour" : "▶ Tour", nav.AutoTour, 12)) nav.AutoTour = !nav.AutoTour;
            if (nav.Mode == ViewMode.Inside || space.Model.FloorCount < 2) return;
            if (Button("All", nav.SelectedFloor < 0, 16)) nav.SetFloor(-1);
            for (int f = 0; f < space.Model.FloorCount; f++)
                if (Button(ViewerText.FloorLabel(space.Model, f), nav.SelectedFloor == f)) nav.SetFloor(f);
        }

        void Panel(Rect r, string text) => Panel(r, new GUIContent(text), MpGui.Label);

        void Panel(Rect r, GUIContent content, GUIStyle style)
        {
            MpGui.Block(_nav, r);
            GUI.Box(r, GUIContent.none, MpGui.Panel);
            GUI.Label(new Rect(r.x + 10, r.y + 8, r.width - 20, r.height - 16), content, style);
        }
    }
}
