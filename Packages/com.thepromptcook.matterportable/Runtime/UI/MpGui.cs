using UnityEngine;

namespace Matterportable
{
    /// <summary>Shared IMGUI look for the HUD and the launcher. Call <see cref="Ensure"/> at the top of OnGUI.</summary>
    internal static class MpGui
    {
        static Texture2D _panelTex, _menuTex, _buttonTex, _accentTex, _onTex;

        public static GUIStyle Panel, MenuPanel, Title, Label, Small, Center, Field, Button, ButtonOn;
        public static Texture2D BarBack => _buttonTex;
        public static Texture2D BarFill => _onTex;

        public static void Ensure()
        {
            if (Panel != null && _panelTex != null) return;
            _panelTex = MpUtil.Solid(new Color(0.08f, 0.08f, 0.09f, 0.78f));
            _menuTex = MpUtil.Solid(new Color(0.12f, 0.12f, 0.13f, 0.97f));
            _buttonTex = MpUtil.Solid(new Color(0.16f, 0.16f, 0.18f, 0.92f));
            _accentTex = MpUtil.Solid(new Color(0.18f, 0.45f, 0.85f, 1f));
            _onTex = MpUtil.Solid(new Color(0.95f, 0.95f, 0.95f, 0.95f));

            Panel = new GUIStyle(GUI.skin.box) { normal = { background = _panelTex }, padding = new RectOffset(10, 10, 8, 8) };
            MenuPanel = new GUIStyle(GUI.skin.box) { normal = { background = _menuTex } };
            Label = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, richText = true, normal = { textColor = Color.white } };
            Small = new GUIStyle(Label) { fontSize = 12, normal = { textColor = new Color(0.8f, 0.8f, 0.83f) } };
            Center = new GUIStyle(Label) { alignment = TextAnchor.MiddleCenter };
            Title = new GUIStyle(Label) { fontSize = 26, fontStyle = FontStyle.Bold };
            Field = new GUIStyle(GUI.skin.textField) { fontSize = 15, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(8, 8, 6, 6) };
            Button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                padding = new RectOffset(10, 10, 6, 6),
                normal = { background = _buttonTex, textColor = Color.white },
                hover = { background = _accentTex, textColor = Color.white },
                active = { background = _accentTex, textColor = Color.white },
            };
            ButtonOn = new GUIStyle(Button)
            {
                normal = { background = _onTex, textColor = Color.black },
                hover = { background = _onTex, textColor = Color.black },
            };
        }

        /// <summary>Tells the navigator that clicks inside this rectangle belong to the UI (call while drawing).</summary>
        public static void Block(MatterportNavigator nav, Rect guiRect)
        {
            if (nav != null && Event.current.type == EventType.Repaint) nav.BlockedGuiRects.Add(guiRect);
        }
    }
}
