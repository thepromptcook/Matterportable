using System;
using Godot;
using Matterportable.Core;

namespace Matterportable
{
    /// <summary>On-screen controls (mirrors the Unity MatterportHud): view modes, floors, measure, tour, menu, key hint,
    /// F1 stats (text from Core <see cref="ViewerText"/>).</summary>
    public partial class Hud : CanvasLayer
    {
        public MatterportSpace Space;
        public MatterportNavigator Navigator;
        public MatterportMeasure Measure;
        public event Action MenuRequested;

        Label _status, _stats;
        HBoxContainer _floors;
        Button[] _modes;
        Button _tour, _measure;
        int _floorCount = -1;
        Navigator Nav => Navigator.Core;

        public override void _Ready()
        {
            var root = new Control { AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore };
            AddChild(root);
            var bar = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, GrowVertical = Control.GrowDirection.Begin };
            bar.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            bar.OffsetLeft = 12;
            bar.OffsetBottom = -12;
            root.AddChild(bar);
            bar.AddChild(MakeButton("☰ Menu", () => MenuRequested?.Invoke()));
            _modes = new[]
            {
                MakeButton("Inside", () => Nav?.SetMode(ViewMode.Inside)),
                MakeButton("Dollhouse", () => Nav?.SetMode(ViewMode.Dollhouse)),
                MakeButton("Floor plan", () => Nav?.SetMode(ViewMode.Floorplan)),
            };
            foreach (var b in _modes) { b.ToggleMode = true; bar.AddChild(b); }
            bar.AddChild(_measure = MakeButton("📏 Measure", () => Measure.SetActive(!Measure.Active)));
            _measure.ToggleMode = true;
            bar.AddChild(_tour = MakeButton("▶ Tour", () => { if (Nav != null) Nav.AutoTour = !Nav.AutoTour; }));
            bar.AddChild(_floors = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore });
            root.AddChild(_status = new Label { AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f, GrowHorizontal = Control.GrowDirection.Both, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore });
            root.AddChild(_stats = new Label { Position = new Vector2(12, 44), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore });
            root.AddChild(new Label { AnchorRight = 1, OffsetTop = 12, HorizontalAlignment = HorizontalAlignment.Center, Text = ViewerText.KeyHint, MouseFilter = Control.MouseFilterEnum.Ignore });
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
            _status.Text = Space.Error != null ? "Matterportable\n" + Space.Error : Space.IsLoaded && Nav != null ? "" : $"Loading Matterport space…\n{Space.Status}";
            if (Nav == null) return;
            for (int i = 0; i < _modes.Length; i++) _modes[i].SetPressedNoSignal((int)Nav.Mode == i);
            _tour.Text = Nav.AutoTour ? "■ Tour" : "▶ Tour";
            _measure.SetPressedNoSignal(Measure.Active);
            _measure.Visible = Space.Model.Geometry != null;
            if (_floorCount != Space.Model.FloorCount) BuildFloorButtons();
            _floors.Visible = Nav.Mode != ViewMode.Inside && Space.Model.FloorCount > 1;
            if (_stats.Visible) _stats.Text = ViewerText.Stats(Space.Model, Nav, Space.Scheduler, Engine.GetFramesPerSecond(), Space.LastLoadSeconds, Space.TexturesLoaded);
        }

        void BuildFloorButtons()
        {
            _floorCount = Space.Model.FloorCount;
            foreach (var c in _floors.GetChildren()) c.QueueFree();
            _floors.AddChild(MakeButton("All", () => Nav.SetFloor(-1)));
            for (int f = 0; f < _floorCount; f++)
            {
                int floor = f;
                _floors.AddChild(MakeButton(ViewerText.FloorLabel(Space.Model, f), () => Nav.SetFloor(floor)));
            }
        }
    }
}
