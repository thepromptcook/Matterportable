// Compile-check stand-in for the Input System package API used by NavInput (only what Matterportable calls).
namespace UnityEngine.InputSystem
{
    namespace Controls
    {
        public class ButtonControl { public bool wasPressedThisFrame, isPressed, wasReleasedThisFrame; }
        public class KeyControl : ButtonControl { }
        public class Vector2Control { public UnityEngine.Vector2 ReadValue() => default; }
    }
    public enum Key { None, W, A, S, D, Q, E, F, H, P, UpArrow, DownArrow, LeftArrow, RightArrow, Digit1, Digit2, Digit3, Digit4, F1, F2, F3, Home, PageUp, PageDown, LeftBracket, RightBracket, Escape, LeftShift, RightShift, LeftAlt, RightAlt, M, Delete, Backspace, Enter, NumpadEnter }
    public class Mouse { public static Mouse current; public Controls.Vector2Control position, scroll; public Controls.ButtonControl leftButton, rightButton, middleButton; }
    public class Keyboard { public static Keyboard current; public Controls.KeyControl this[Key key] => null; }
}
