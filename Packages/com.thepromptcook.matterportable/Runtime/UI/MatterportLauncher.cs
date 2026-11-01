using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Matterportable.Core;
using UnityEngine;

namespace Matterportable
{
    /// <summary>
    /// In-game start menu: paste a Matterport link (or pick a downloaded space / the offline demo) and explore it.
    /// Esc or the Menu button returns here. Created automatically when Play starts in an empty scene
    /// (no camera, no space), so the menu is what you see first.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Matterportable/Matterport Launcher")]
    public sealed class MatterportLauncher : MonoBehaviour
    {
        const string LastLinkKey = "Matterportable.LastLink";

        [Tooltip("Settings for spaces opened from the menu.")]
        public ViewerSettings settings = new ViewerSettings();
        [Tooltip("Link pre-filled the first time (later the last link you opened is remembered).")]
        public string defaultLink = "https://discover.matterport.com/space/rGMMnZDh3Tf";

        string _link;
        bool _menuOpen = true;
        string _message;
        bool _busy;
        MatterportRig _rig;
        GameObject _menuCamera;
        readonly List<(string id, string name)> _cached = new List<(string, string)>();
        Vector2 _scroll;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (MpUtil.FindFirst<MatterportSpace>() == null && MpUtil.FindFirst<MatterportLauncher>() == null && MpUtil.FindFirst<Camera>() == null)
                new GameObject("Matterport Launcher").AddComponent<MatterportLauncher>();
        }

        void Start()
        {
            _link = PlayerPrefs.GetString(LastLinkKey, defaultLink);
            ShowMenu();
        }

        void Update()
        {
            NavInput.BeginFrame();
            if (_menuOpen) return;
            if (NavInput.KeyDown(KeyCode.Escape) && !MatterportMeasure.OwnsEscape) ShowMenu();
            else if (_rig?.Space != null && _rig.Space.Error != null)
            {
                _message = _rig.Space.Error;
                ShowMenu();
            }
        }

        void ShowMenu()
        {
            _rig?.Destroy();
            _rig = null;
            _menuOpen = true;
            if (_menuCamera == null)
            {
                _menuCamera = new GameObject("Menu Camera");
                _menuCamera.transform.SetParent(transform, false);
                var cam = _menuCamera.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.07f, 0.07f, 0.08f);
                cam.cullingMask = 0;
            }
            _menuCamera.SetActive(true);
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
            _rig?.Destroy();
            _rig = MatterportRig.Create(link.Trim(), "", settings);
            _menuCamera.SetActive(false);
            _menuOpen = false;
        }

        async void OpenDemo()
        {
            _busy = true;
            _message = "Building the demo house (a few seconds, first time only)…";
            try
            {
                await DemoSpace.EnsureAsync("", 384, false, CancellationToken.None);
                if (this == null) return;
                _busy = false;
                Open(DemoSpace.ModelId);
            }
            catch (Exception e)
            {
                _busy = false;
                _message = "Demo failed: " + e.GetBaseException().Message;
            }
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
                catch (Exception) { /* skip unreadable entries */ }
            }
        }

        void OnGUI()
        {
            MpGui.Ensure();
            if (!_menuOpen)
            {
                var r = new Rect(Screen.width - 92, 12, 80, 30);
                MpGui.Block(_rig?.Navigator, r);
                if (GUI.Button(r, "☰ Menu", MpGui.Button)) ShowMenu();
                return;
            }

            float w = Mathf.Min(620, Screen.width - 40), h = Mathf.Min(520, Screen.height - 40);
            var panel = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
            GUI.Box(panel, GUIContent.none, MpGui.MenuPanel);
            GUILayout.BeginArea(new Rect(panel.x + 24, panel.y + 20, w - 48, h - 40));
            GUILayout.Label("Matterportable", MpGui.Title);
            GUILayout.Label("Paste a Matterport link to explore it. The first visit downloads the space (a minute or two); after that it opens instantly.", MpGui.Small);
            GUILayout.Space(12);

            bool wasEnabled = GUI.enabled;
            GUI.enabled = !_busy;
            GUILayout.BeginHorizontal();
            _link = GUILayout.TextField(_link ?? "", MpGui.Field, GUILayout.Height(34));
            if (GUILayout.Button("Paste", MpGui.Button, GUILayout.Width(70), GUILayout.Height(34))) _link = GUIUtility.systemCopyBuffer.Trim();
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            if (GUILayout.Button("Open space", MpGui.Button, GUILayout.Height(40))) Open(_link);
            if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                Event.current.Use();
                Open(_link);
            }
            GUILayout.Space(4);
            if (GUILayout.Button("Try the offline demo house", MpGui.Button, GUILayout.Height(30))) OpenDemo();
            GUI.enabled = wasEnabled;

            if (!string.IsNullOrEmpty(_message))
            {
                GUILayout.Space(8);
                GUILayout.Label(_message, MpGui.Label);
            }
            if (_cached.Count > 0)
            {
                GUILayout.Space(12);
                GUILayout.Label("Downloaded spaces", MpGui.Label);
                _scroll = GUILayout.BeginScrollView(_scroll);
                foreach (var (id, name) in _cached)
                    if (GUILayout.Button(name + "   (" + id + ")", MpGui.Button, GUILayout.Height(28))) Open(id);
                GUILayout.EndScrollView();
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label("Only open spaces you own or have permission to use.", MpGui.Small);
            GUILayout.EndArea();
        }
    }
}
