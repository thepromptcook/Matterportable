using System;
using System.IO;
using System.Threading;
using Godot;
using Matterportable.Core;

namespace Matterportable
{
    /// <summary>
    /// The start menu (mirrors the Unity MatterportLauncher): paste a Matterport link, open the offline demo or a
    /// downloaded space. Esc or the Menu button returns here. Command line (after "--"): --open &lt;link|demo&gt;,
    /// --sweep N, --mode inside|dollhouse|floorplan, --view N (F2 render view), --goto N (move there after loading), --measure (draws a measurement), --compare dir [--compare-sweeps N] (see RunCompare),
    /// --screenshot file.png (quits after saving), for headless checks.
    /// </summary>
    public partial class Launcher : Node
    {
        const string ConfigPath = "user://matterportable.cfg";

        Control _menu;
        LineEdit _link;
        Label _status;
        ItemList _spaces;
        MatterportSpace _space;
        MatterportNavigator _nav;
        Hud _hud;
        MatterportMeasure _measure;
        int _measureStep = -1;
        string _screenshot, _compare;
        int _compareSweeps = 6;
        string _mode;
        int _view, _goto = -1;
        int _screenshotFrames = -1;

        public override void _Ready()
        {
            BuildMenu();
            var args = OS.GetCmdlineUserArgs();
            int sweep = 0;
            string open = null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--open") open = args[i + 1];
                if (args[i] == "--sweep") int.TryParse(args[i + 1], out sweep);
                if (args[i] == "--mode") _mode = args[i + 1];
                if (args[i] == "--view") int.TryParse(args[i + 1], out _view);
                if (args[i] == "--goto") int.TryParse(args[i + 1], out _goto);
                if (args[i] == "--screenshot") _screenshot = args[i + 1];
                if (args[i] == "--compare") _compare = args[i + 1];
                if (args[i] == "--compare-sweeps") int.TryParse(args[i + 1], out _compareSweeps);
            }
            if (Array.IndexOf(args, "--measure") >= 0) _measureStep = 0;
            if (open == "demo") OpenDemo(sweep);
            else if (open != null) Open(open, sweep);
        }

        void BuildMenu()
        {
            var layer = new CanvasLayer { Layer = 10 };
            AddChild(layer);
            _menu = new PanelContainer { AnchorRight = 1, AnchorBottom = 1 };
            _menu.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.08f, 0.08f, 0.09f) });
            layer.AddChild(_menu);
            var center = new CenterContainer();
            _menu.AddChild(center);
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) };
            box.AddThemeConstantOverride("separation", 10);
            center.AddChild(box);

            box.AddChild(new Label { Text = "Matterportable", HorizontalAlignment = HorizontalAlignment.Center, ThemeTypeVariation = "HeaderLarge" });
            box.AddChild(new Label { Text = "Paste a Matterport link (or model ID) to walk through it.", HorizontalAlignment = HorizontalAlignment.Center });
            var row = new HBoxContainer();
            box.AddChild(row);
            _link = new LineEdit { PlaceholderText = "https://discover.matterport.com/space/rGMMnZDh3Tf", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _link.TextSubmitted += t => Open(t, 0);
            row.AddChild(_link);
            row.AddChild(MakeButton("Paste", () => _link.Text = DisplayServer.ClipboardGet().Trim()));
            box.AddChild(MakeButton("Open space", () => Open(_link.Text, 0)));
            box.AddChild(MakeButton("Offline demo (no network)", () => OpenDemo(0)));
            _status = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            box.AddChild(_status);
            box.AddChild(new Label { Text = "Downloaded spaces (shared with the Unity project):" });
            _spaces = new ItemList { CustomMinimumSize = new Vector2(0, 160) };
            _spaces.ItemActivated += i => Open((string)_spaces.GetItemMetadata((int)i), 0);
            box.AddChild(_spaces);

            var cfg = new ConfigFile();
            if (cfg.Load(ConfigPath) == Error.Ok) _link.Text = (string)cfg.GetValue("launcher", "last_link", "");
            RefreshSpaces();
        }

        static Button MakeButton(string text, Action onPressed)
        {
            var b = new Button { Text = text };
            b.Pressed += onPressed;
            return b;
        }

        void RefreshSpaces()
        {
            _spaces.Clear();
            var root = GodotUtil.CacheRoot();
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (!SpaceManifest.Exists(dir)) continue;
                string name = Path.GetFileName(dir);
                try { name = SpaceManifest.Load(dir).Name + "  (" + Path.GetFileName(dir) + ")"; } catch (Exception) { /* list it anyway */ }
                int i = _spaces.AddItem(name);
                _spaces.SetItemMetadata(i, Path.GetFileName(dir));
            }
        }

        async void OpenDemo(int sweep)
        {
            _status.Text = "Generating the demo house…";
            try
            {
                var dir = await GodotUtil.EnsureDemoAsync(384, CancellationToken.None);
                Open(Path.GetFileName(dir), sweep);
            }
            catch (Exception e) { _status.Text = "Demo failed: " + GodotUtil.Unwrap(e); }
        }

        void Open(string link, int sweep)
        {
            if (!MatterportLink.TryParseModelId(link, out _))
            {
                _status.Text = "That doesn't look like a Matterport link or model ID.";
                return;
            }
            var cfg = new ConfigFile();
            cfg.Load(ConfigPath);
            cfg.SetValue("launcher", "last_link", link);
            cfg.Save(ConfigPath);

            CloseSpace();
            _space = new MatterportSpace { Name = "MatterportSpace", Link = link, StartSweep = sweep };
            _nav = new MatterportNavigator { Name = "Camera", Space = _space };
            _measure = new MatterportMeasure { Name = "Measure", Space = _space, Navigator = _nav };
            _hud = new Hud { Space = _space, Navigator = _nav, Measure = _measure };
            _hud.MenuRequested += ShowMenu;
            _space.Loaded += s => s.DebugView = _view;
            _nav.StartMode = _mode == "dollhouse" ? MatterportNavigator.ViewMode.Dollhouse : _mode == "floorplan" ? MatterportNavigator.ViewMode.Floorplan : MatterportNavigator.ViewMode.Inside;
            AddChild(_space);
            AddChild(_nav);
            AddChild(_measure);
            AddChild(_hud);
            _nav.MakeCurrent();
            _menu.Visible = false;
        }

        void CloseSpace()
        {
            _space?.QueueFree();
            _nav?.QueueFree();
            _hud?.QueueFree();
            _measure?.QueueFree();
            _measure = null;
            _space = null;
            _nav = null;
            _hud = null;
        }

        void ShowMenu()
        {
            CloseSpace();
            _status.Text = "";
            RefreshSpaces();
            _menu.Visible = true;
        }

        /// <summary>
        /// Automated check of measure mode: a two-segment line placed by clicks across the floor and up a wall, ended
        /// with a real Escape key event (which must not open the menu), then a line in progress following the cursor.
        /// </summary>
        void MeasureDemo()
        {
            var size = GetViewport().GetVisibleRect().Size;
            switch (_measureStep++)
            {
                case 0:
                    _measure.SetActive(true);
                    _measure.ClickAt(new Vector2(size.X * 0.30f, size.Y * 0.85f));
                    _measure.ClickAt(new Vector2(size.X * 0.62f, size.Y * 0.80f));
                    _measure.ClickAt(new Vector2(size.X * 0.62f, size.Y * 0.40f));
                    Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true });
                    break;
                case 3: // a few frames later: Escape has been delivered
                    _measure.ClickAt(new Vector2(size.X * 0.20f, size.Y * 0.60f));
                    GetViewport().WarpMouse(new Vector2(size.X * 0.45f, size.Y * 0.55f));
                    _measureStep = -1;
                    break;
            }
        }

        /// <summary>
        /// Alignment check on real spaces: at evenly spaced sweeps and four headings, saves the same view twice -
        /// Matterport's baked mesh texture only (render view 1) and the sweep's photo only (render view 2) - as
        /// dir/s{sweep}_y{yaw}_{mesh|pano}.png, plus compare.txt (camera, fov, sizes). At a sweep Matterport shows
        /// exactly that photo, and it baked the mesh textures from the same photos, so the pairs must line up;
        /// tools/compare_alignment.py measures any offset. Quits when done.
        /// </summary>
        async void RunCompare(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            _hud.Visible = false;
            _measure.Visible = false;
            _space.Settings.ShowReticle = false;
            var info = new System.Text.StringBuilder();
            info.AppendLine($"space {_space.ModelId} \"{_space.Manifest.Name}\" sweeps {_space.Sweeps.Count} fov {_space.Settings.DefaultFov} calibration {_space.Calibration.Method} score {_space.Calibration.Score:0.00}");
            int n = Math.Max(1, Math.Min(_compareSweeps, _space.Sweeps.Count));
            for (int k = 0; k < n; k++)
            {
                int sweep = (int)((k + 0.5f) * _space.Sweeps.Count / n);
                _nav.Teleport(sweep, 0f, -10f);
                // Wait for the full-size photo (or 10 s) and all mesh textures.
                for (int f = 0; f < 600; f++)
                {
                    var e = _space.Panoramas.Get(sweep);
                    var largest = _space.Manifest.Sweeps[sweep].TierAtMost(_space.Settings.PanoMaxSize);
                    if (e?.Faces != null && (largest == null || e.Size >= largest.Size) && _space.TexturesLoaded >= _space.Manifest.Textures.Count) break;
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                foreach (int yaw in new[] { 0, 90, 180, 270 })
                {
                    _nav.Teleport(sweep, yaw, -10f);
                    foreach (var (view, name) in new[] { (1, "mesh"), (2, "pano") })
                    {
                        _space.DebugView = view;
                        for (int f = 0; f < 3; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"s{sweep:D3}_y{yaw:D3}_{name}.png"));
                    }
                }
                info.AppendLine($"sweep {sweep} id {_space.Sweeps[sweep].Id} pano {_space.Panoramas.Get(sweep)?.Size}px");
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "compare.txt"), info.ToString());
            GD.Print($"[Matterportable] compare: {n} sweeps saved to {dir}");
            GetTree().Quit();
        }

        public override void _UnhandledInput(InputEvent e)
        {
            if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape && _space != null) ShowMenu();
        }

        public override void _Process(double delta)
        {
            if (_compare != null && _space != null && _space.IsLoaded && _nav.CurrentSweep >= 0)
            {
                var dir = _compare;
                _compare = null;
                RunCompare(dir);
            }
            if (_screenshot == null || _space == null) return;
            if (_space.Error != null) { GD.PrintErr("[Matterportable] " + _space.Error); GetTree().Quit(1); return; }
            // Wait for the space, its panoramas and the mode animation to settle, then save the frame and quit.
            if (_goto >= 0 && _space.IsLoaded && _nav.CurrentSweep >= 0 && !_nav.IsChangingMode)
            {
                _nav.GoTo(_goto);
                _goto = -1;
                return;
            }
            if (_measureStep >= 0 && _space.IsLoaded && _nav.CurrentSweep >= 0 && !_nav.IsChangingMode && _space.Panoramas.ActiveLoads == 0)
            {
                MeasureDemo();
                return;
            }
            if (_screenshotFrames < 0 && _space.IsLoaded && !_nav.IsMoving && !_nav.IsWaitingForPanorama && _goto < 0 && _space.Panoramas.ActiveLoads == 0 && _space.Panoramas.Queued == 0 && !_nav.IsChangingMode && _space.TexturesLoaded >= _space.Manifest.Textures.Count)
                _screenshotFrames = 30;
            if (_screenshotFrames > 0 && --_screenshotFrames == 0)
            {
                var err = GetViewport().GetTexture().GetImage().SavePng(_screenshot);
                var measured = _measure.Tool == null ? "" : $"; {_measure.Tool.Lines.Count} measurement(s), drawing {(_measure.Tool.Draft != null ? "yes" : "no")}, menu {(_menu.Visible ? "open" : "closed")}";
                GD.Print($"[Matterportable] {_space.Status}; mode {_nav.Mode}, sweep {_nav.CurrentSweep}{measured}; screenshot {(err == Error.Ok ? "saved" : "failed: " + err)}");
                GetTree().Quit(err == Error.Ok ? 0 : 1);
            }
        }
    }
}
