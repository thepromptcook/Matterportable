using System;
using System.Text;
using Godot;

namespace Matterportable
{
    /// <summary>On-screen controls (mirrors the Unity MatterportHud): view modes, floors, tour, menu, key hint, F1 stats.</summary>
    public partial class Hud : CanvasLayer
    {
        public MatterportSpace Space;
        public MatterportNavigator Navigator;
        public MatterportMeasure Measure;
        public event Action MenuRequested;

        Label _status, _stats, _hint;
        HBoxContainer _floors;
        Button[] _modes;
        Button _tour, _measure;
        int _floorCount = -1;

        public override void _Ready()
        {
            var root = new Control { AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore };
            AddChild(root);
            var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, GrowVertical = Control.GrowDirection.Begin };
            top.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            top.OffsetLeft = 12;
            top.OffsetBottom = -12;
            root.AddChild(top);
            top.AddChild(MakeButton("☰ Menu", () => MenuRequested?.Invoke()));
            _modes = new[]
            {
                MakeButton("Inside", () => Navigator.SetMode(MatterportNavigator.ViewMode.Inside)),
                MakeButton("Dollhouse", () => Navigator.SetMode(MatterportNavigator.ViewMode.Dollhouse)),
                MakeButton("Floor plan", () => Navigator.SetMode(MatterportNavigator.ViewMode.Floorplan)),
            };
            foreach (var b in _modes) { b.ToggleMode = true; top.AddChild(b); }
            _measure = MakeButton("📏 Measure", () => Measure.SetActive(!Measure.Active));
            _measure.ToggleMode = true;
            top.AddChild(_measure);
            _tour = MakeButton("▶ Tour", () => Navigator.AutoTour = !Navigator.AutoTour);
            top.AddChild(_tour);
            _floors = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            top.AddChild(_floors);

            _status = new Label { AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f, GrowHorizontal = Control.GrowDirection.Both, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            root.AddChild(_status);
            _stats = new Label { Position = new Vector2(12, 44), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            root.AddChild(_stats);
            _hint = new Label
            {
                AnchorRight = 1, OffsetTop = 12, HorizontalAlignment = HorizontalAlignment.Center,
                Text = "Drag to look · Click to move · W A S D step · ←/→ turn · Wheel zoom · 1/2/3 Inside/Dollhouse/Floor plan · M measure · [ ] prev/next · P tour · F1 stats · F2 render view · H hide · Esc menu",
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            root.AddChild(_hint);
        }

        static Button MakeButton(string text, Action onPressed)
        {
            var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
            b.Pressed += onPressed;
            return b;
        }

        public override void _UnhandledInput(InputEvent e)
        {
            if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
            if (k.Keycode == Key.F1) _stats.Visible = !_stats.Visible;
            if (k.Keycode == Key.H) Visible = !Visible;
        }

        public override void _Process(double delta)
        {
            if (Space == null) return;
            _status.Text = Space.Error != null ? "Matterportable\n" + Space.Error : Space.IsLoaded ? "" : $"Loading Matterport space…\n{Space.Status}";
            if (!Space.IsLoaded) return;
            for (int i = 0; i < _modes.Length; i++) _modes[i].SetPressedNoSignal((int)Navigator.Mode == i);
            _tour.Text = Navigator.AutoTour ? "■ Tour" : "▶ Tour";
            _measure.SetPressedNoSignal(Measure.Active);
            _measure.Visible = Space.Geometry != null;
            if (_floorCount != Space.FloorCount) BuildFloorButtons();
            _floors.Visible = Navigator.Mode != MatterportNavigator.ViewMode.Inside && Space.FloorCount > 1;
            if (_stats.Visible) _stats.Text = Stats();
        }

        void BuildFloorButtons()
        {
            _floorCount = Space.FloorCount;
            foreach (var c in _floors.GetChildren()) c.QueueFree();
            _floors.AddChild(MakeButton("All", () => Navigator.SetFloor(-1)));
            for (int f = 0; f < _floorCount; f++)
            {
                int floor = f;
                _floors.AddChild(MakeButton("Floor " + (f + 1), () => Navigator.SetFloor(floor)));
            }
        }

        string Stats()
        {
            var sb = new StringBuilder();
            sb.Append(Space.Manifest.Name).Append("  (").Append(Space.ModelId).Append(")\n");
            sb.AppendFormat("{0} fps\n", Engine.GetFramesPerSecond());
            int cur = Navigator.CurrentSweep;
            if (cur >= 0) sb.AppendFormat("Sweep {0}/{1}  id {2}  floor {3}\n", cur, Space.Sweeps.Count - 1, Space.Sweeps[cur].Id, Space.Sweeps[cur].Floor);
            sb.AppendFormat("Mode {0}  FOV {1:0}°\n", Navigator.Mode, Navigator.Fov);
            int loaded = 0, depth = 0;
            foreach (var e in Space.Panoramas.Entries) { if (e.Faces != null) loaded++; if (e.Depth != null) depth++; }
            sb.AppendFormat("Panoramas: {0} cached ({1:0} MB), {2} depth maps, loading {3}, queued {4}\n", loaded, Space.Panoramas.BytesUsed / 1048576.0, depth, Space.Panoramas.ActiveLoads, Space.Panoramas.Queued);
            if (Space.Geometry != null) sb.AppendFormat("Mesh: {0:N0} triangles, textures {1}/{2}\n", Space.Geometry.TriangleCount, Space.TexturesLoaded, Space.Manifest.Textures.Count);
            var cal = Space.Calibration;
            sb.AppendFormat("Alignment: {0}{1}\n", cal.Method, double.IsNaN(cal.Score) ? "" : $" (score {cal.Score:0.00})");
            sb.AppendFormat("Render view (F2): {0}\n", MatterportSpace.DebugViewNames[Space.DebugView]);
            sb.AppendFormat("Loaded in {0:0.0}s", Space.LastLoadSeconds);
            return sb.ToString();
        }
    }
}
