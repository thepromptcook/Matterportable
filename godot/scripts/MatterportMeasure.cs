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

        readonly MeasureOverlay _items = new MeasureOverlay();
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
            _pillSelected.BgColor = MeasureOverlay.Accent.ToGodot();
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
            if (on && (Space == null || !Space.IsLoaded || Space.Model.Geometry == null || Navigator.Core == null)) return;
            if (on == Active) return;
            Active = on;
            if (on && _toolGeometry != Space.Model.Geometry)
            {
                Tool = new MeasureTool(Space.Model.Geometry);
                _toolGeometry = Space.Model.Geometry;
            }
            if (!on) Tool?.Finish();
            Navigator.Core.Measuring = on;
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
            return new MeasureInput
            {
                RayOrigin = Navigator.ProjectRayOrigin(mouse).ToCore(),
                RayDirection = Navigator.ProjectRayNormal(mouse).ToCore(),
                PixelAngle = Mathf.DegToRad(Navigator.Fov) / Mathf.Max(1f, GetViewport().GetVisibleRect().Size.Y),
                AxisLock = Input.IsKeyPressed(Key.Shift),
                NoSnap = Input.IsKeyPressed(Key.Alt),
                PointerBlocked = blocked,
                View = Navigator.Core.Mode,
                ClipHeight = Navigator.Core.ViewClipHeight,
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
            bool blocked = !vp.GetVisibleRect().HasPoint(mouse) || vp.GuiGetHoveredControl() != null || Navigator.Core.IsChangingMode;
            Tool.Update(InputAt(mouse, blocked));
            if (Navigator.Core.Clicked) Tool.Click();
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

        // ------------------------------------------------------------------ drawing (screen space, items from the Core)

        void DrawOverlay()
        {
            var nav = Navigator.Core;
            if (!Active || Tool == null || nav == null) return;
            var size = GetViewport().GetVisibleRect().Size;
            Tool.BuildOverlay(nav.CameraPosition, nav.CameraRotation, nav.Fov, size.X, size.Y, _items);
            var font = ThemeDB.FallbackFont;
            const int fontSize = 15;
            foreach (var it in _items.Items)
            {
                var at = new Vector2(it.X, it.Y);
                var color = it.Color.ToGodot();
                if (it.Kind == MeasureOverlay.Kind.Line) _overlay.DrawLine(at, new Vector2(it.X2, it.Y2), color, it.Size, true);
                else if (it.Kind == MeasureOverlay.Kind.Dot) _overlay.DrawCircle(at, it.Size, color);
                else if (it.Kind == MeasureOverlay.Kind.Ring) _overlay.DrawArc(at, it.Size, 0, Mathf.Tau, 28, color, 2f, true);
                else
                {
                    var textSize = font.GetStringSize(it.Text, HorizontalAlignment.Left, -1, fontSize);
                    var rect = new Rect2(at - new Vector2(textSize.X / 2 + 8, 11), new Vector2(textSize.X + 16, 22));
                    _overlay.DrawStyleBox(it.Selected ? _pillSelected : _pill, rect);
                    _overlay.DrawString(font, new Vector2(rect.Position.X + 8, rect.Position.Y + 16), it.Text, HorizontalAlignment.Left, -1, fontSize,
                        (it.Selected ? MeasureOverlay.White : MeasureOverlay.Ink).ToGodot());
                }
            }
        }
    }
}
