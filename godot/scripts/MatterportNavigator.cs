using Godot;
using Matterportable.Core;

namespace Matterportable
{
    /// <summary>
    /// The camera, driven by the Core <see cref="Navigator"/> (mirrors the Unity MatterportNavigator): reads Godot
    /// input, runs the shared navigation, and applies the camera pose, markers and view binding. UI controls consume
    /// their own clicks, so pointer presses arrive through _UnhandledInput.
    /// </summary>
    public partial class MatterportNavigator : Camera3D
    {
        public MatterportSpace Space;
        public ViewMode StartMode = ViewMode.Inside;
        /// <summary>Keys held, or pressed once, by a script (automated checks).</summary>
        public NavKeys ScriptedHeld, ScriptedPressed;
        /// <summary>Aim through the middle of the view instead of the mouse (scripted checks).</summary>
        public bool CentrePointer;
        public Navigator Core { get; private set; }

        SweepMarkers _markers;
        NavKeys _pressed;
        int _down, _up;
        float _scroll;

        public override void _Ready()
        {
            Near = 0.03f;
            Far = 1000f;
            _markers = new SweepMarkers(this);
        }

        public override void _UnhandledInput(InputEvent e)
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp) _scroll += 1;
            else if (e is InputEventMouseButton wd && wd.Pressed && wd.ButtonIndex == MouseButton.WheelDown) _scroll -= 1;
            else if (e is InputEventMouseButton b && b.ButtonIndex <= MouseButton.Middle)
            {
                int bit = b.ButtonIndex == MouseButton.Left ? 1 : b.ButtonIndex == MouseButton.Right ? 2 : 4;
                if (b.Pressed) _down |= bit;
                else _up |= bit;
            }
            else if (e is InputEventKey k && k.Pressed && !k.Echo) _pressed |= Map(k.Keycode);
        }

        static NavKeys Map(Key k) => k switch
        {
            Key.Key1 => NavKeys.Inside, Key.Key2 => NavKeys.Dollhouse, Key.Key3 => NavKeys.Floorplan, Key.Key4 => NavKeys.Walk,
            Key.W or Key.Up => NavKeys.Forward, Key.S or Key.Down => NavKeys.Back, Key.A => NavKeys.Left, Key.D => NavKeys.Right,
            Key.Bracketright => NavKeys.Next, Key.Bracketleft => NavKeys.Previous, Key.Home => NavKeys.Home,
            Key.Pageup => NavKeys.FloorUp, Key.Pagedown => NavKeys.FloorDown, Key.P => NavKeys.Tour, Key.F2 => NavKeys.DebugView, Key.F => NavKeys.Use,
            _ => NavKeys.None,
        };

        static NavKeys Held(params (Key key, NavKeys nav)[] keys)
        {
            var held = NavKeys.None;
            foreach (var (key, nav) in keys) if (Input.IsKeyPressed(key)) held |= nav;
            return held;
        }

        public override void _Process(double delta)
        {
            _markers.BeginFrame();
            if (Space?.IsLoaded != true) return;
            Core ??= new Navigator(Space.Model, Space.Settings, Space.Scheduler) { StartSweep = Space.StartSweep, StartMode = StartMode };
            var vp = GetViewport();
            var rect = vp.GetVisibleRect();
            var mouse = CentrePointer ? rect.GetCenter() : vp.GetMousePosition();
            Core.Update(new NavigatorInput
            {
                DeltaTime = (float)delta,
                PointerX = mouse.X,
                PointerY = mouse.Y,
                PointerOver = rect.HasPoint(mouse) && vp.GuiGetHoveredControl() == null,
                ButtonsDown = _down,
                ButtonsUp = _up,
                ButtonsHeld = (Input.IsMouseButtonPressed(MouseButton.Left) ? 1 : 0) | (Input.IsMouseButtonPressed(MouseButton.Right) ? 2 : 0) | (Input.IsMouseButtonPressed(MouseButton.Middle) ? 4 : 0),
                RayOrigin = ProjectRayOrigin(mouse).ToCore(),
                RayDirection = ProjectRayNormal(mouse).ToCore(),
                PixelAngle = Mathf.DegToRad(Fov) / Mathf.Max(1f, rect.Size.Y),
                ViewportHeight = rect.Size.Y,
                Scroll = _scroll,
                Pressed = _pressed | ScriptedPressed,
                Held = Held((Key.Left, NavKeys.TurnLeft), (Key.Q, NavKeys.TurnLeft), (Key.Right, NavKeys.TurnRight), (Key.E, NavKeys.TurnRight),
                    (Key.W, NavKeys.Forward), (Key.Up, NavKeys.Forward), (Key.S, NavKeys.Back), (Key.Down, NavKeys.Back), (Key.A, NavKeys.Left), (Key.D, NavKeys.Right),
                    (Key.Shift, NavKeys.Run)) | ScriptedHeld,
            });
            _down = _up = 0;
            _scroll = 0;
            _pressed = ScriptedPressed = NavKeys.None;
            Position = Core.CameraPosition.ToGodot();
            Quaternion = Core.CameraRotation.ToGodot();
            Fov = Core.Fov;
            Space.View = Core.View;
            Space.Props = Core.Props;
            Space.HighlightProp = Core.HighlightProp;
            foreach (var m in Core.Markers) _markers.Draw(m.Position.ToGodot(), m.Normal.ToGodot(), m.Radius, m.Color.ToGodot(), m.Inner, m.Fill);
        }
    }
}
