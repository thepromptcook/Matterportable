using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Matterportable.Core;
using UnityEngine;

namespace Matterportable
{
    /// <summary>
    /// In-game start menu: paste a Matterport link (or pick a downloaded space / the offline demo) and explore it.
    /// Press Esc or the Menu button to come back and load another space. Appears automatically when you press Play
    /// in a scene that has no MatterportSpace, so an empty scene is all you need.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Matterportable/Matterport Launcher")]
    public sealed class MatterportLauncher : MonoBehaviour
    {
        const string LastLinkKey = "Matterportable.LastLink";

        [Tooltip("Optional settings applied to spaces opened from the menu.")]
        public MatterportViewerSettings settings;
        [Tooltip("Link pre-filled the first time (later the last link you opened is remembered).")]
        public string defaultLink = "https://discover.matterport.com/space/rGMMnZDh3Tf";

        string _link;
        bool _menuOpen = true;
        string _message;
        bool _busy;
        GameObject _spaceGo, _rigGo, _menuCamGo;
        MatterportSpace _space;
        readonly List<(string id, string name)> _cached = new List<(string, string)>();
        Vector2 _scroll;
        GUIStyle _title, _label, _small, _field, _button, _panel;
        Texture2D _panelTex, _buttonTex, _accentTex;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
#if UNITY_2023_1_OR_NEWER
            bool hasSpace = FindAnyObjectByType<MatterportSpace>() != null;
            bool hasLauncher = FindAnyObjectByType<MatterportLauncher>() != null;
#else
            bool hasSpace = FindObjectOfType<MatterportSpace>() != null;
            bool hasLauncher = FindObjectOfType<MatterportLauncher>() != null;
#endif
            if (!hasSpace && !hasLauncher) new GameObject("Matterport Launcher").AddComponent<MatterportLauncher>();
        }

        void Start()
        {
            _link = PlayerPrefs.GetString(LastLinkKey, defaultLink);
            ShowMenu();
        }

        void OnDestroy()
        {
            MpUtil.SafeDestroy(_panelTex);
            MpUtil.SafeDestroy(_buttonTex);
            MpUtil.SafeDestroy(_accentTex);
        }

        void Update()
        {
            NavInput.BeginFrame();
            if (!_menuOpen && NavInput.KeyDown(KeyCode.Escape)) ShowMenu();
            if (_space != null && _space.Error != null && !_menuOpen)
            {
                _message = _space.Error;
                ShowMenu();
            }
        }

        // ------------------------------------------------------------------ actions

        void ShowMenu()
        {
            CloseSpace();
            _menuOpen = true;
            if (_menuCamGo == null)
            {
                _menuCamGo = new GameObject("Menu Camera");
                _menuCamGo.transform.SetParent(transform, false);
                var cam = _menuCamGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.07f, 0.07f, 0.08f);
                cam.cullingMask = 0;
            }
            _menuCamGo.SetActive(true);
            RefreshCached();
        }

        void Open(string link)
        {
            if (!MatterportLink.TryParseModelId(link, out _))
            {
                _message = "That doesn't look like a Matterport link. Paste something like https://discover.matterport.com/space/rGMMnZDh3Tf";
                return;
            }
            PlayerPrefs.SetString(LastLinkKey, link.Trim());
            PlayerPrefs.Save();
            _message = null;
            CloseSpace();

            _spaceGo = new GameObject("Matterport Space");
            _space = _spaceGo.AddComponent<MatterportSpace>();
            _space.link = link.Trim();
            _space.settings = settings;
            _space.downloadIfMissing = true;

            _rigGo = new GameObject("Matterport Camera");
            _rigGo.tag = "MainCamera";
            var cam = _rigGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = 75f;
            _rigGo.AddComponent<AudioListener>();
            _rigGo.AddComponent<MatterportNavigator>().space = _space;
            _rigGo.AddComponent<MatterportHud>();

            if (_menuCamGo != null) _menuCamGo.SetActive(false);
            _menuOpen = false;
        }

        void CloseSpace()
        {
            if (_rigGo != null) Destroy(_rigGo);
            if (_spaceGo != null) Destroy(_spaceGo);
            _rigGo = _spaceGo = null;
            _space = null;
        }

        IEnumerator GenerateDemo()
        {
            _busy = true;
            _message = "Building the demo house (this takes a few seconds the first time)…";
            var dir = MatterportPaths.SpaceDir("", "DemoSpace01");
            if (!SpaceManifest.Exists(dir))
            {
                var task = Task.Run(() => SyntheticSpace.Generate(new SyntheticOptions { PanoSizes = new[] { 128, 512 }, ImageExtension = "jpg" }));
                while (!task.IsCompleted) yield return null;
                if (task.IsFaulted)
                {
                    _message = "Demo failed: " + task.Exception?.GetBaseException().Message;
                    _busy = false;
                    yield break;
                }
                task.Result.WriteTo(dir, img => MpUtil.EncodeJpg(img, 92));
            }
            _busy = false;
            Open("DemoSpace01");
        }

        void RefreshCached()
        {
            _cached.Clear();
            var root = MatterportPaths.DefaultCacheRoot;
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (!SpaceManifest.Exists(dir)) continue;
                try
                {
                    var m = SpaceManifest.Load(dir);
                    _cached.Add((m.ModelId, string.IsNullOrEmpty(m.Name) ? m.ModelId : m.Name));
                }
                catch (System.Exception) { /* skip unreadable entries */ }
            }
        }

        // ------------------------------------------------------------------ UI

        void OnGUI()
        {
            EnsureStyles();
            if (!_menuOpen)
            {
                var r = new Rect(Screen.width - 92, 12, 80, 30);
                var nav = _rigGo != null ? _rigGo.GetComponent<MatterportNavigator>() : null;
                if (nav != null && (Event.current.type == EventType.Layout || Event.current.type == EventType.Repaint)) nav.BlockedGuiRects.Add(r);
                if (GUI.Button(r, "☰ Menu", _button)) ShowMenu();
                return;
            }

            float w = Mathf.Min(620, Screen.width - 40);
            float h = Mathf.Min(520, Screen.height - 40);
            var panel = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
            GUI.Box(panel, GUIContent.none, _panel);
            GUILayout.BeginArea(new Rect(panel.x + 24, panel.y + 20, w - 48, h - 40));
            GUILayout.Label("Matterportable", _title);
            GUILayout.Label("Paste a Matterport link to explore it. The first visit downloads the space (a minute or two); after that it opens instantly.", _small);
            GUILayout.Space(12);

            bool wasEnabled = GUI.enabled;
            GUI.enabled = !_busy;
            {
                GUILayout.BeginHorizontal();
                GUI.SetNextControlName("link");
                _link = GUILayout.TextField(_link ?? "", _field, GUILayout.Height(34));
                if (GUILayout.Button("Paste", _button, GUILayout.Width(70), GUILayout.Height(34)))
                    _link = GUIUtility.systemCopyBuffer.Trim();
                GUILayout.EndHorizontal();
                GUILayout.Space(8);
                if (GUILayout.Button("Open space", _button, GUILayout.Height(40))) Open(_link);
                if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
                {
                    Event.current.Use();
                    Open(_link);
                }
                GUILayout.Space(4);
                if (GUILayout.Button("Try the offline demo house", _button, GUILayout.Height(30))) StartCoroutine(GenerateDemo());
            }
            GUI.enabled = wasEnabled;

            if (!string.IsNullOrEmpty(_message))
            {
                GUILayout.Space(8);
                GUILayout.Label(_message, _label);
            }

            if (_cached.Count > 0)
            {
                GUILayout.Space(12);
                GUILayout.Label("Downloaded spaces", _label);
                _scroll = GUILayout.BeginScrollView(_scroll);
                foreach (var (id, name) in _cached)
                    if (GUILayout.Button(name + "   (" + id + ")", _button, GUILayout.Height(28))) Open(id);
                GUILayout.EndScrollView();
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label("Only open spaces you own or have permission to use.", _small);
            GUILayout.EndArea();
        }

        void EnsureStyles()
        {
            if (_panel != null) return;
            _panelTex = Solid(new Color(0.12f, 0.12f, 0.13f, 0.97f));
            _buttonTex = Solid(new Color(0.22f, 0.22f, 0.24f, 1f));
            _accentTex = Solid(new Color(0.18f, 0.45f, 0.85f, 1f));
            _panel = new GUIStyle(GUI.skin.box) { normal = { background = _panelTex } };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, normal = { textColor = Color.white } };
            _small = new GUIStyle(_label) { fontSize = 12, normal = { textColor = new Color(0.75f, 0.75f, 0.78f) } };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 15, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(8, 8, 6, 6) };
            _button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                normal = { background = _buttonTex, textColor = Color.white },
                hover = { background = _accentTex, textColor = Color.white },
                active = { background = _accentTex, textColor = Color.white },
            };
        }

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
