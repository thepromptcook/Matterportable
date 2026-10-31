using System;
using Godot;
using Matterportable.Core;

namespace Matterportable
{
    /// <summary>
    /// Matterport's measurement mode (mirrors the Unity MatterportMeasure; the behaviour lives in Core
    /// <see cref="MeasureTool"/>). Toggle with the HUD's Measure button or M. Click to place points, Escape or Confirm
    /// ends a line, Escape again leaves the mode; Shift locks to an axis, Alt disables snapping, Delete removes the
    /// selected measurement. Draws lines, points, labels and the + cursor in screen space, faded where hidden.
    /// </summary>
    public partial class MatterportMeasure : CanvasLayer
    {
        public MatterportSpace Space;
        public MatterportNavigator Navigator;
        public bool Active { get; private set; }
        public MeasureTool Tool { get; private set; }

        static readonly Color White = new Color(1f, 1f, 1f), Shadow = new Color(0f, 0f, 0f, 0.55f), Ink = new Color(0.1f, 0.1f, 0.12f);
        static readonly Color Accent = new Color(0.18f, 0.45f, 0.85f), Hover = new Color(0.6f, 0.78f, 1f);
        static readonly Color AxisX = new Color(0.95f, 0.36f, 0.3f), AxisZ = new Color(0.38f, 0.85f, 0.42f), AxisVertical = new Color(0.35f, 0.62f, 1f);

        readonly MeasureDrawList _draw = new MeasureDrawList();
        SpaceGeometry _toolGeometry;
        Control _overlay;
        PanelContainer _panel;
        VBoxContainer _list;
        Button _snapping, _continuous, _units, _confirm;
        string _listState = "";
        StyleBoxFlat _pill, _pillSelected;

        public override void _Ready()
        {
            _overlay = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            _overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _overlay.Draw += DrawOverlay;
            AddChild(_overlay);
            _pill = new StyleBoxFlat { BgColor = new Color(1f, 1f, 1f, 0.95f), ContentMarginLeft = 8, ContentMarginRight = 8 };
            _pill.SetCornerRadiusAll(10);
            _pillSelected = (StyleBoxFlat)_pill.Duplicate();
            _pillSelected.BgColor = Accent;
            BuildPanel();
        }

        void BuildPanel()
        {
            _panel = new PanelContainer { Visible = false, GrowHorizontal = Control.GrowDirection.Begin, GrowVertical = Control.GrowDirection.Begin };
            _panel.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
            _panel.OffsetRight = -12;
            _panel.OffsetBottom = -12;
            AddChild(_panel);
            var box = new VBoxContainer();
            _panel.AddChild(box);
            box.AddChild(new Label { Text = "Measure", ThemeTypeVariation = "HeaderSmall" });
            box.AddChild(new Label
            {
                Text = "Click to place points · Shift: lock to axis · Alt: no snapping\nEsc: finish line, again to exit · Del: delete selected",
                Modulate = new Color(1, 1, 1, 0.75f),
            });
            _list = new VBoxContainer();
            box.AddChild(_list);
            var row = new HBoxContainer();
            box.AddChild(row);
            _snapping = Toggle(row, "Snapping", () => Tool.Snapping = !Tool.Snapping);
            _continuous = Toggle(row, "Continuous lines", () => Tool.Continuous = !Tool.Continuous);
            _units = Toggle(row, "ft", () => Tool.Units = Tool.Units == MeasureUnits.Imperial ? MeasureUnits.Metric : MeasureUnits.Imperial);
            _units.ToggleMode = false;
            _confirm = Toggle(row, "✓ Confirm", () => Tool.Finish());
            _confirm.ToggleMode = false;
            var close = Toggle(row, "Done", () => SetActive(false));
            close.ToggleMode = false;
        }

        static Button Toggle(Container parent, string text, Action onPressed)
        {
            var b = new Button { Text = text, ToggleMode = true, FocusMode = Control.FocusModeEnum.None };
            b.Pressed += onPressed;
            parent.AddChild(b);
            return b;
        }

        /// <summary>Enters or leaves measure mode. Measurements stay until the space is closed (they are temporary).</summary>
        public void SetActive(bool on)
        {
            if (on && (Space == null || !Space.IsLoaded || Space.Geometry == null)) return;
            if (on == Active) return;
            Active = on;
            if (on && _toolGeometry != Space.Geometry)
            {
                Tool = new MeasureTool(Space.Geometry);
                _toolGeometry = Space.Geometry;
            }
            if (!on) Tool?.Finish();
            Navigator.ClickHandler = on ? _ => { Tool.Click(); return true; } : (Func<Vector2, bool>)null;
            _panel.Visible = on;
            _overlay.QueueRedraw();
        }

        /// <summary>Places a point (or selects) at a screen position, as a click there would. For automated checks.</summary>
        public void ClickAt(Vector2 screen)
        {
            Tool.Update(InputAt(screen, false));
            Tool.Click();
        }

        MeasureInput InputAt(Vector2 mouse, bool blocked)
        {
            var mode = Navigator.Mode;
            return new MeasureInput
            {
                RayOrigin = Navigator.ProjectRayOrigin(mouse).ToCore(),
                RayDirection = Navigator.ProjectRayNormal(mouse).ToCore(),
                PixelAngle = Mathf.DegToRad(Navigator.Fov) / Mathf.Max(1f, GetViewport().GetVisibleRect().Size.Y),
                AxisLock = Input.IsKeyPressed(Key.Shift),
                NoSnap = Input.IsKeyPressed(Key.Alt),
                PointerBlocked = blocked,
                View = mode == MatterportNavigator.ViewMode.Inside ? MeasureView.Inside : mode == MatterportNavigator.ViewMode.Dollhouse ? MeasureView.Dollhouse : MeasureView.FloorPlan,
                ClipHeight = Navigator.ViewClipHeight,
            };
        }

        public override void _UnhandledInput(InputEvent e)
        {
            if (e is not InputEventKey k || !k.Pressed || k.Echo) return;
            if (k.Keycode == Key.M) SetActive(!Active);
            else if (!Active) return;
            else if (k.Keycode == Key.Escape) { if (!Tool.Escape()) SetActive(false); }
            else if (k.Keycode == Key.Delete || k.Keycode == Key.Backspace) Tool.DeleteSelected();
            else if (k.Keycode == Key.Enter || k.Keycode == Key.KpEnter) Tool.Finish();
            else return;
            GetViewport().SetInputAsHandled();
        }

        public override void _Process(double delta)
        {
            if (!Active) return;
            if (Space == null || !Space.IsLoaded) { SetActive(false); return; }
            var vp = GetViewport();
            var mouse = vp.GetMousePosition();
            bool blocked = !vp.GetVisibleRect().HasPoint(mouse) || vp.GuiGetHoveredControl() != null || Navigator.IsChangingMode;
            Tool.Update(InputAt(mouse, blocked));
            Tool.BuildDrawList(Navigator.GlobalPosition.ToCore(), _draw);
            _overlay.QueueRedraw();
            UpdatePanel();
        }

        void UpdatePanel()
        {
            _snapping.SetPressedNoSignal(Tool.Snapping);
            _continuous.SetPressedNoSignal(Tool.Continuous);
            _units.Text = Tool.Units == MeasureUnits.Imperial ? "Units: ft" : "Units: m";
            _confirm.Visible = Tool.Draft != null;
            // The list: one row per measurement (select / delete), rebuilt only when it changes.
            var state = $"{Tool.Units}|{Tool.Selected}|{Tool.Lines.Count}|{(Tool.Lines.Count > 0 ? Tool.Lines[Tool.Lines.Count - 1].Points.Count : 0)}";
            if (state == _listState) return;
            _listState = state;
            foreach (var c in _list.GetChildren()) c.QueueFree();
            for (int i = 0; i < Tool.Lines.Count; i++)
            {
                int index = i;
                var line = Tool.Lines[i];
                var row = new HBoxContainer();
                _list.AddChild(row);
                var text = $"{i + 1}.  {Tool.Format(line.TotalLength)}" + (line.SegmentCount > 1 ? $"  ({line.SegmentCount} segments)" : "") + (line.FloorPlan ? "  · floor plan" : "");
                var select = Toggle(row, text, () => Tool.Selected = Tool.Selected == index ? -1 : index);
                select.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                select.Alignment = HorizontalAlignment.Left;
                select.SetPressedNoSignal(Tool.Selected == i);
                var delete = Toggle(row, "✕", () => Tool.Delete(index));
                delete.ToggleMode = false;
            }
        }

        // ------------------------------------------------------------------ drawing (screen space)

        Color SegmentColor(MeasureDrawList.Segment s)
        {
            if (s.Line >= 0 && s.Line == Tool.Selected) return Accent;
            if (s.Line >= 0 && s.Line == Tool.Hovered) return Hover;
            return s.Line < 0 ? AxisColor(s.Axis) : White;
        }

        /// <summary>Lines being drawn with Shift take the colour of their axis.</summary>
        static Color AxisColor(MeasureAxis axis) =>
            axis == MeasureAxis.X ? AxisX : axis == MeasureAxis.Z ? AxisZ : axis == MeasureAxis.Vertical ? AxisVertical : White;

        bool Project(Vec3 p, out Vector2 screen)
        {
            var world = p.ToGodot();
            screen = Navigator.UnprojectPosition(world);
            return !Navigator.IsPositionBehind(world);
        }

        void DrawOverlay()
        {
            if (!Active || Tool == null) return;
            var eye = Navigator.GlobalPosition.ToCore();
            var forward = (-Navigator.GlobalBasis.Z).ToCore();
            float near = Navigator.Near + 0.01f;

            for (int pass = 0; pass < 2; pass++) // hidden pieces first, faded; then the visible ones
            foreach (var s in _draw.Segments)
            {
                if (s.Occluded != (pass == 0)) continue;
                Vec3 a = s.A, b = s.B;
                if (!MeasureTool.ClipToFront(ref a, ref b, eye, forward, near)) continue;
                Project(a, out var pa);
                Project(b, out var pb);
                var c = SegmentColor(s);
                if (s.Occluded) c.A = 0.4f;
                _overlay.DrawLine(pa, pb, new Color(Shadow, Shadow.A * c.A), s.Occluded ? 3f : 5f, true);
                _overlay.DrawLine(pa, pb, c, s.Occluded ? 1.5f : 3f, true);
            }

            foreach (var p in _draw.Points)
            {
                if (!Project(p.At, out var sp)) continue;
                _overlay.DrawCircle(sp, 5.5f, Shadow);
                _overlay.DrawCircle(sp, 4f, p.Line >= 0 && p.Line == Tool.Selected ? Accent : White);
            }

            var font = ThemeDB.FallbackFont;
            const int size = 15;
            foreach (var l in _draw.Labels)
            {
                if (!Project(l.At, out var sp)) continue;
                var textSize = font.GetStringSize(l.Text, HorizontalAlignment.Left, -1, size);
                var rect = new Rect2(sp - new Vector2(textSize.X / 2 + 8, 11), new Vector2(textSize.X + 16, 22));
                bool selected = l.Line >= 0 && l.Line == Tool.Selected;
                _overlay.DrawStyleBox(selected ? _pillSelected : _pill, rect);
                _overlay.DrawString(font, new Vector2(rect.Position.X + 8, rect.Position.Y + 16), l.Text, HorizontalAlignment.Left, -1, size, selected ? White : Ink);
            }

            var cursor = Tool.Cursor;
            if (cursor.Valid && Project(cursor.Point, out var cp))
            {
                foreach (var (w, col) in new[] { (4f, Shadow), (2f, White) })
                {
                    _overlay.DrawLine(cp - new Vector2(10, 0), cp + new Vector2(10, 0), col, w, true);
                    _overlay.DrawLine(cp - new Vector2(0, 10), cp + new Vector2(0, 10), col, w, true);
                }
                if (cursor.Snap != MeasureSnap.Surface) _overlay.DrawArc(cp, 9f, 0, Mathf.Tau, 28, AxisColor(cursor.Axis), 2f, true);
            }
        }
    }
}
