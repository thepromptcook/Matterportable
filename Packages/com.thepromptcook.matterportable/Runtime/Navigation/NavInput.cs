using UnityEngine;
#if MATTERPORTABLE_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Matterportable
{
    /// <summary>
    /// Tiny input layer that works with both the legacy Input Manager and the Input System package, so the viewer
    /// runs in any project without extra setup. Call <see cref="BeginFrame"/> once per frame.
    /// </summary>
    public static class NavInput
    {
        static Vector2 _lastMouse;
        static bool _hasLast;
        static int _frame = -1;

        public static Vector2 MousePosition { get; private set; }
        /// <summary>Mouse movement this frame in pixels.</summary>
        public static Vector2 MouseDelta { get; private set; }
        /// <summary>Mouse wheel movement this frame in notches (positive = away from the user).</summary>
        public static float Scroll { get; private set; }

        public static void BeginFrame()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            var pos = ReadMousePosition();
            MouseDelta = _hasLast ? pos - _lastMouse : Vector2.zero;
            _lastMouse = pos;
            _hasLast = true;
            MousePosition = pos;
            Scroll = ReadScroll();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _hasLast = false;
            _frame = -1;
        }

#if MATTERPORTABLE_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        static Vector2 ReadMousePosition() => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
        static float ReadScroll() => Mouse.current != null ? Mouse.current.scroll.ReadValue().y / 120f : 0f;

        static UnityEngine.InputSystem.Controls.ButtonControl Button(int b)
        {
            var m = Mouse.current;
            if (m == null) return null;
            return b == 0 ? m.leftButton : b == 1 ? m.rightButton : m.middleButton;
        }

        public static bool ButtonDown(int b) => Button(b)?.wasPressedThisFrame ?? false;
        public static bool ButtonHeld(int b) => Button(b)?.isPressed ?? false;
        public static bool ButtonUp(int b) => Button(b)?.wasReleasedThisFrame ?? false;

        public static bool Key(KeyCode k)
        {
            var key = Map(k);
            return key != UnityEngine.InputSystem.Key.None && Keyboard.current != null && Keyboard.current[key].isPressed;
        }

        public static bool KeyDown(KeyCode k)
        {
            var key = Map(k);
            return key != UnityEngine.InputSystem.Key.None && Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame;
        }

        static UnityEngine.InputSystem.Key Map(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.W: return UnityEngine.InputSystem.Key.W;
                case KeyCode.A: return UnityEngine.InputSystem.Key.A;
                case KeyCode.S: return UnityEngine.InputSystem.Key.S;
                case KeyCode.D: return UnityEngine.InputSystem.Key.D;
                case KeyCode.Q: return UnityEngine.InputSystem.Key.Q;
                case KeyCode.E: return UnityEngine.InputSystem.Key.E;
                case KeyCode.F: return UnityEngine.InputSystem.Key.F;
                case KeyCode.H: return UnityEngine.InputSystem.Key.H;
                case KeyCode.P: return UnityEngine.InputSystem.Key.P;
                case KeyCode.UpArrow: return UnityEngine.InputSystem.Key.UpArrow;
                case KeyCode.DownArrow: return UnityEngine.InputSystem.Key.DownArrow;
                case KeyCode.LeftArrow: return UnityEngine.InputSystem.Key.LeftArrow;
                case KeyCode.RightArrow: return UnityEngine.InputSystem.Key.RightArrow;
                case KeyCode.Alpha1: return UnityEngine.InputSystem.Key.Digit1;
                case KeyCode.Alpha2: return UnityEngine.InputSystem.Key.Digit2;
                case KeyCode.Alpha3: return UnityEngine.InputSystem.Key.Digit3;
                case KeyCode.Alpha4: return UnityEngine.InputSystem.Key.Digit4;
                case KeyCode.F1: return UnityEngine.InputSystem.Key.F1;
                case KeyCode.F2: return UnityEngine.InputSystem.Key.F2;
                case KeyCode.F3: return UnityEngine.InputSystem.Key.F3;
                case KeyCode.Home: return UnityEngine.InputSystem.Key.Home;
                case KeyCode.PageUp: return UnityEngine.InputSystem.Key.PageUp;
                case KeyCode.PageDown: return UnityEngine.InputSystem.Key.PageDown;
                case KeyCode.LeftBracket: return UnityEngine.InputSystem.Key.LeftBracket;
                case KeyCode.RightBracket: return UnityEngine.InputSystem.Key.RightBracket;
                case KeyCode.Escape: return UnityEngine.InputSystem.Key.Escape;
                case KeyCode.LeftShift: return UnityEngine.InputSystem.Key.LeftShift;
                case KeyCode.RightShift: return UnityEngine.InputSystem.Key.RightShift;
                case KeyCode.LeftAlt: return UnityEngine.InputSystem.Key.LeftAlt;
                case KeyCode.RightAlt: return UnityEngine.InputSystem.Key.RightAlt;
                case KeyCode.M: return UnityEngine.InputSystem.Key.M;
                case KeyCode.Delete: return UnityEngine.InputSystem.Key.Delete;
                case KeyCode.Backspace: return UnityEngine.InputSystem.Key.Backspace;
                case KeyCode.Return: return UnityEngine.InputSystem.Key.Enter;
                case KeyCode.KeypadEnter: return UnityEngine.InputSystem.Key.NumpadEnter;
                default: return UnityEngine.InputSystem.Key.None;
            }
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        static Vector2 ReadMousePosition() => Input.mousePosition;
        static float ReadScroll() => Input.mouseScrollDelta.y;
        public static bool ButtonDown(int b) => Input.GetMouseButtonDown(b);
        public static bool ButtonHeld(int b) => Input.GetMouseButton(b);
        public static bool ButtonUp(int b) => Input.GetMouseButtonUp(b);
        public static bool Key(KeyCode k) => Input.GetKey(k);
        public static bool KeyDown(KeyCode k) => Input.GetKeyDown(k);
#else
        // The project uses the Input System but the package is not installed: input is unavailable.
        static Vector2 ReadMousePosition() => Vector2.zero;
        static float ReadScroll() => 0f;
        public static bool ButtonDown(int b) => false;
        public static bool ButtonHeld(int b) => false;
        public static bool ButtonUp(int b) => false;
        public static bool Key(KeyCode k) => false;
        public static bool KeyDown(KeyCode k) => false;
#endif
    }
}
