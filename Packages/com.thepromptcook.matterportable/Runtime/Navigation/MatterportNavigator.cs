using System.Collections.Generic;
using Matterportable.Core;
using UnityEngine;

namespace Matterportable
{
    /// <summary>
    /// The camera, driven by the Core <see cref="Navigator"/> (mirrors the Godot MatterportNavigator): reads Unity
    /// input, runs the shared Matterport-style navigation (drag to look, click to move, W/A/S/D, wheel zoom, Dollhouse,
    /// Floor plan, and Walk mode with collisions) in the space's local frame, and applies the camera pose, markers and
    /// view binding.
    /// Put it on the camera; it finds the <see cref="MatterportSpace"/> automatically.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    [AddComponentMenu("Matterportable/Matterport Navigator")]
    public sealed class MatterportNavigator : MonoBehaviour
    {
        [Tooltip("Space to navigate. Found automatically when empty.")]
        public MatterportSpace space;
        public bool enableMouse = true;
        public bool enableKeyboard = true;
        [Tooltip("Mode to start in. Walk: move freely with W A S D; walls, floors, stairs and furniture are solid.")]
        public ViewMode startMode = ViewMode.Inside;

        /// <summary>Navigation state and actions (GoTo, SetMode, SetFloor, AutoTour…); null until the space has loaded.</summary>
        public Navigator Core { get; private set; }
        /// <summary>
        /// Screen rectangles (GUI coordinates, y down) where clicks belong to UI. Filled while UI draws
        /// (<see cref="MpGui.Block"/>), read by the next frame's Updates and cleared in LateUpdate.
        /// </summary>
        public readonly List<Rect> BlockedGuiRects = new List<Rect>();

        static readonly (KeyCode key, NavKeys nav)[] PressedKeys =
        {
            (KeyCode.Alpha1, NavKeys.Inside), (KeyCode.Alpha2, NavKeys.Dollhouse), (KeyCode.Alpha3, NavKeys.Floorplan), (KeyCode.Alpha4, NavKeys.Walk),
            (KeyCode.W, NavKeys.Forward), (KeyCode.UpArrow, NavKeys.Forward), (KeyCode.S, NavKeys.Back), (KeyCode.DownArrow, NavKeys.Back),
            (KeyCode.A, NavKeys.Left), (KeyCode.D, NavKeys.Right), (KeyCode.RightBracket, NavKeys.Next), (KeyCode.LeftBracket, NavKeys.Previous),
            (KeyCode.Home, NavKeys.Home), (KeyCode.PageUp, NavKeys.FloorUp), (KeyCode.PageDown, NavKeys.FloorDown), (KeyCode.P, NavKeys.Tour),
            (KeyCode.F2, NavKeys.DebugView),
        };
        static readonly (KeyCode key, NavKeys nav)[] HeldKeys =
        {
            (KeyCode.LeftArrow, NavKeys.TurnLeft), (KeyCode.Q, NavKeys.TurnLeft), (KeyCode.RightArrow, NavKeys.TurnRight), (KeyCode.E, NavKeys.TurnRight),
            (KeyCode.W, NavKeys.Forward), (KeyCode.UpArrow, NavKeys.Forward), (KeyCode.S, NavKeys.Back), (KeyCode.DownArrow, NavKeys.Back),
            (KeyCode.A, NavKeys.Left), (KeyCode.D, NavKeys.Right), (KeyCode.LeftShift, NavKeys.Run), (KeyCode.RightShift, NavKeys.Run),
        };

        Camera _cam;
        SweepMarkers _markers;
        PanoramaScheduler _scheduler; // a new one per load, so a reload restarts the navigator

        void Awake() => _cam = GetComponent<Camera>();

        void OnDisable()
        {
            _markers?.Dispose();
            _markers = null;
            Core = null;
        }

        void Update()
        {
            if (space == null && Time.frameCount % 30 == 0) space = MpUtil.FindFirst<MatterportSpace>();
            if (space == null || !space.IsLoaded)
            {
                Core = null;
                return;
            }
            if (Core == null || _scheduler != space.Scheduler)
            {
                _scheduler = space.Scheduler;
                Core = new Navigator(space.Model, space.settings, _scheduler) { StartSweep = space.startSweep, StartMode = startMode };
                if (_markers == null) _markers = new SweepMarkers();
                _cam.clearFlags = CameraClearFlags.Skybox;
                _cam.nearClipPlane = 0.03f;
                _cam.farClipPlane = Mathf.Max(_cam.farClipPlane, 1000f);
            }
            NavInput.BeginFrame();
            var mouse = NavInput.MousePosition;
            var (origin, direction) = PointerRay(mouse);
            int down = 0, held = 0, up = 0;
            for (int b = 0; enableMouse && b < 3; b++)
            {
                if (NavInput.ButtonDown(b)) down |= 1 << b;
                if (NavInput.ButtonHeld(b)) held |= 1 << b;
                if (NavInput.ButtonUp(b)) up |= 1 << b;
            }
            Core.Update(new NavigatorInput
            {
                DeltaTime = Time.unscaledDeltaTime,
                PointerX = mouse.x,
                PointerY = Screen.height - mouse.y,
                PointerOver = enableMouse && PointerOverView(mouse),
                ButtonsDown = down,
                ButtonsHeld = held,
                ButtonsUp = up,
                RayOrigin = origin,
                RayDirection = direction,
                PixelAngle = _cam.fieldOfView * Mathf.Deg2Rad / Mathf.Max(1, Screen.height),
                ViewportHeight = Screen.height,
                Scroll = enableMouse ? NavInput.Scroll : 0f,
                Pressed = enableKeyboard ? Keys(PressedKeys, true) : NavKeys.None,
                Held = enableKeyboard ? Keys(HeldKeys, false) : NavKeys.None,
            });
            var t = space.transform;
            transform.SetPositionAndRotation(t.TransformPoint(Core.CameraPosition.ToUnity()), t.rotation * Core.CameraRotation.ToUnity());
            _cam.fieldOfView = Core.Fov;
            space.View = Core.View;
            foreach (var m in Core.Markers)
                _markers.Draw(_cam, t.TransformPoint(m.Position.ToUnity()), t.TransformDirection(m.Normal.ToUnity()), m.Radius, m.Color.ToUnity(), m.Inner, m.Fill);
        }

        void LateUpdate() => BlockedGuiRects.Clear();

        static NavKeys Keys((KeyCode key, NavKeys nav)[] map, bool pressedThisFrame)
        {
            var keys = NavKeys.None;
            foreach (var (key, nav) in map)
                if (pressedThisFrame ? NavInput.KeyDown(key) : NavInput.Key(key)) keys |= nav;
            return keys;
        }

        /// <summary>The ray under a screen position (pixels, y up) in the space's local (Core) frame.</summary>
        public (Vec3 origin, Vec3 direction) PointerRay(Vector2 mouse)
        {
            var ray = _cam.ScreenPointToRay(mouse);
            var t = space.transform;
            return (t.InverseTransformPoint(ray.origin).ToCore(), t.InverseTransformDirection(ray.direction).normalized.ToCore());
        }

        /// <summary>The mouse (pixels, y up) is inside the window and not over UI that registered with MpGui.Block.</summary>
        public bool PointerOverView(Vector2 mouse)
        {
            if (mouse.x < 0 || mouse.y < 0 || mouse.x > Screen.width || mouse.y > Screen.height) return false;
            var gui = new Vector2(mouse.x, Screen.height - mouse.y);
            foreach (var r in BlockedGuiRects) if (r.Contains(gui)) return false;
            return true;
        }
    }
}
