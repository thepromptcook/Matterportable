using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Matterportable.Core;

namespace Matterportable
{
    /// <summary>
    /// The start menu (mirrors the Unity MatterportLauncher): paste a Matterport link, open the offline demo or a
    /// downloaded space, optionally in Walk mode. Esc or the Menu button returns here. Command line (after "--"):
    /// --open &lt;link|demo&gt;, --sweep N, --mode inside|dollhouse|floorplan|walk, --yaw degrees, --view N (F2 render view),
    /// --goto N (move there after loading), --walk seconds (hold W), --pitch degrees (with --yaw), --move-prop degrees
    /// (Walk mode: picks up what the view is on, turns that far, puts it down, turns back half way), --measure (draws a
    /// measurement),
    /// --compare dir [--compare-sweeps N] (see RunCompare), --fidelity dir (see RunFidelity), --depth N (depth cube size),
    /// --screenshot file.png (quits after saving; with --views 0,1,2: one file per render view, file_v0.png …), for
    /// headless checks.
    /// </summary>
    public partial class Launcher : Node
    {
        const string ConfigPath = "user://matterportable.cfg";

        Control _menu;
        LineEdit _link;
        Label _status;
        CheckBox _walkMode;
        ItemList _spaces;
        MatterportSpace _space;
        MatterportNavigator _nav;
        Hud _hud;
        MatterportMeasure _measure;
        int _measureStep = -1;
        string _screenshot, _compare, _fidelity;
        int _depth;
        int _compareSweeps = 6;
        string _mode;
        int _view = -1, _goto = -1;
        int _screenshotFrames = -1;
        float _yaw = float.NaN, _walk, _pitch, _moveProp, _lookYaw;
        int _moveStep, _moveWait = 30, _moved = -1;
        bool _walking;
        string[] _views;
        int _viewIndex;

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
                if (args[i] == "--yaw") float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _yaw);
                if (args[i] == "--pitch") float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _pitch);
                if (args[i] == "--move-prop") float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _moveProp);
                if (args[i] == "--walk") float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _walk);
                if (args[i] == "--screenshot") _screenshot = args[i + 1];
                if (args[i] == "--views") _views = args[i + 1].Split(',');
                if (args[i] == "--compare") _compare = args[i + 1];
                if (args[i] == "--fidelity") _fidelity = args[i + 1];
                if (args[i] == "--depth") int.TryParse(args[i + 1], out _depth);
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
            box.AddChild(_walkMode = new CheckBox { Text = "Start in Walk mode: move freely with W A S D (walls and furniture block you)" });
            box.AddChild(MakeButton("Offline demo (no network)", () => OpenDemo(0)));
            _status = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            box.AddChild(_status);
            box.AddChild(new Label { Text = "Downloaded spaces (shared with the Unity project):" });
            _spaces = new ItemList { CustomMinimumSize = new Vector2(0, 160) };
            _spaces.ItemActivated += i => Open((string)_spaces.GetItemMetadata((int)i), 0);
            box.AddChild(_spaces);

            var cfg = new ConfigFile();
            if (cfg.Load(ConfigPath) == Error.Ok)
            {
                _link.Text = (string)cfg.GetValue("launcher", "last_link", "");
                _walkMode.ButtonPressed = (bool)cfg.GetValue("launcher", "walk", false);
            }
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
            cfg.SetValue("launcher", "walk", _walkMode.ButtonPressed);
            cfg.Save(ConfigPath);

            CloseSpace();
            _space = new MatterportSpace { Name = "MatterportSpace", Link = link, StartSweep = sweep };
            if (_depth > 0) _space.Settings.DepthMapSize = _depth;
            _nav = new MatterportNavigator { Name = "Camera", Space = _space };
            _measure = new MatterportMeasure { Name = "Measure", Space = _space, Navigator = _nav };
            _hud = new Hud { Space = _space, Navigator = _nav, Measure = _measure };
            _hud.MenuRequested += ShowMenu;
            _nav.StartMode = _mode == "dollhouse" ? ViewMode.Dollhouse : _mode == "floorplan" ? ViewMode.Floorplan
                : _mode == "walk" || (_mode == null && _walkMode.ButtonPressed) ? ViewMode.Walk : ViewMode.Inside;
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
            var model = _space.Model;
            info.AppendLine($"space {_space.ModelId} \"{model.Manifest.Name}\" sweeps {model.Sweeps.Count} fov {_space.Settings.DefaultFov} calibration {model.Calibration.Method} score {model.Calibration.Score:0.00}");
            int n = Math.Max(1, Math.Min(_compareSweeps, model.Sweeps.Count));
            for (int k = 0; k < n; k++)
            {
                int sweep = (int)((k + 0.5f) * model.Sweeps.Count / n);
                _nav.Core.Teleport(sweep, 0f, 10f);
                // Wait for the full-size photo (or 10 s) and all mesh textures.
                var largest = model.Sweeps[sweep].Entry.TierAtMost(_space.Settings.PanoMaxSize);
                for (int f = 0; f < 600 && (_space.Scheduler.Size(sweep) < (largest?.Size ?? 0) || _space.TexturesLoaded < model.Manifest.Textures.Count); f++)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                foreach (int yaw in new[] { 0, 90, 180, 270 })
                {
                    _nav.Core.Teleport(sweep, yaw, 10f);
                    foreach (var (view, name) in new[] { (1, "mesh"), (2, "pano") })
                    {
                        _nav.Core.DebugView = view;
                        for (int f = 0; f < 3; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"s{sweep:D3}_y{yaw:D3}_{name}.png"));
                    }
                }
                info.AppendLine($"sweep {sweep} id {model.Sweeps[sweep].Id} pano {_space.Scheduler.Size(sweep)}px");
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "compare.txt"), info.ToString());
            GD.Print($"[Matterportable] compare: {n} sweeps saved to {dir}");
            GetTree().Quit();
        }

        /// <summary>
        /// Novel-view fidelity check for Walk mode: at evenly spaced sweeps and four headings, saves the photo
        /// (dir/s{sweep}_y{yaw}_photo.png) and the Walk-mode view from the same spot rendered without that photo, from
        /// its neighbours' only (..._walk.png) - what Walk mode shows where no photo was taken.
        /// tools/compare_fidelity.py scores them. Quits when done.
        /// </summary>
        async void RunFidelity(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            _hud.Visible = false;
            _measure.Visible = false;
            _space.Settings.ShowReticle = false;
            var model = _space.Model;
            var scheduler = _space.Scheduler;
            int n = Math.Max(1, Math.Min(_compareSweeps, model.Sweeps.Count)), saved = 0;
            float offset = 0; // the walk camera must stand where the photo was taken
            async Task Frames(int count) { for (int f = 0; f < count; f++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            async Task Settle() { for (int f = 0; f < 900 && (scheduler.ActiveLoads > 0 || scheduler.Queued > 0); f++) await Frames(1); await Frames(30); }
            for (int k = 0; k < n; k++)
            {
                int sweep = (int)((k + 0.5f) * model.Sweeps.Count / n);
                var nav = _nav.Core;
                nav.ExcludedSweep = -1;
                nav.Teleport(sweep, 0f, 10f);
                await Settle();
                foreach (int yaw in new[] { 0, 90, 180, 270 })
                {
                    nav.Teleport(sweep, yaw, 10f);
                    nav.DebugView = 2;
                    await Frames(3);
                    GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"s{sweep:D3}_y{yaw:D3}_photo.png"));
                }
                nav.ExcludedSweep = sweep;
                nav.DebugView = 0;
                float radius = _space.Settings.BodyRadius;
                _space.Settings.BodyRadius = 0.01f; // stand exactly at the sweep: no pushing out of nearby furniture
                nav.SetMode(ViewMode.Walk);
                _space.Settings.BodyRadius = radius;
                if (nav.Mode != ViewMode.Walk) continue; // nowhere to stand here
                offset = Math.Max(offset, (nav.CameraPosition - model.Sweeps[sweep].Position).Length);
                await Settle();
                foreach (int yaw in new[] { 0, 90, 180, 270 })
                {
                    nav.Look(yaw, 10f);
                    await Frames(3);
                    GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"s{sweep:D3}_y{yaw:D3}_walk.png"));
                }
                saved++;
            }
            GD.Print($"[Matterportable] fidelity: {saved} sweeps saved to {dir}; walk camera at most {offset * 100:0.0} cm from the photo's spot");
            GetTree().Quit();
        }

        public override void _UnhandledInput(InputEvent e)
        {
            if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape && _space != null) ShowMenu();
        }

        /// <summary>--move-prop: F on what the middle of the view is on, turn, F again, turn back half way (real key presses
        /// through the navigator), so the screenshot shows where the prop was and where it went.</summary>
        void MovePropStep(Navigator nav)
        {
            if (_moveWait-- > 0) return;
            _moveWait = 15;
            _nav.CentrePointer = true;
            var props = nav.Props;
            switch (_moveStep++)
            {
                case 0:
                    if (props == null || nav.HighlightProp < 0) { GD.Print("[Matterportable] move-prop: nothing to pick up in the middle of the view"); _moveProp = 0; return; }
                    _moved = nav.HighlightProp;
                    _nav.ScriptedPressed = NavKeys.Use;
                    break;
                case 1:
                    nav.Look(_lookYaw + _moveProp, _pitch);
                    break;
                case 2:
                    _nav.ScriptedPressed = NavKeys.Use;
                    break;
                default:
                    nav.Look(_lookYaw + _moveProp / 2, _pitch);
                    var p = props.Props[_moved];
                    var at = props.Poses[_moved].Apply(p.Pivot);
                    GD.Print($"[Matterportable] move-prop: {p.Kind} {_moved} ({p.Triangles.Length} triangles) from ({p.Pivot.X:0.00}, {p.Pivot.Y:0.00}, {p.Pivot.Z:0.00}) to ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}), carried {(props.Carried >= 0 ? "still" : "no longer")}");
                    _moveProp = 0;
                    break;
            }
        }

        public override void _Process(double delta)
        {
            var nav = _nav?.Core;
            if (nav != null && _view >= 0) { nav.DebugView = _view; _view = -1; }
            if (nav != null && !float.IsNaN(_yaw)) { nav.Look(_lookYaw = _yaw, _pitch); _yaw = float.NaN; }
            if (_compare != null && nav != null && nav.CurrentSweep >= 0)
            {
                var dir = _compare;
                _compare = null;
                RunCompare(dir);
            }
            if (_fidelity != null && nav != null && nav.CurrentSweep >= 0)
            {
                var dir = _fidelity;
                _fidelity = null;
                RunFidelity(dir);
            }
            if (_screenshot == null || _space == null) return;
            if (_space.Error != null) { GD.PrintErr("[Matterportable] " + _space.Error); GetTree().Quit(1); return; }
            // Wait for the space, its panoramas and the mode animation to settle, then save the frame and quit.
            if (nav == null) return;
            if (_goto >= 0 && nav.CurrentSweep >= 0 && !nav.IsChangingMode)
            {
                nav.GoTo(_goto);
                _goto = -1;
                return;
            }
            if (_measureStep >= 0 && nav.CurrentSweep >= 0 && !nav.IsChangingMode && _space.Scheduler.ActiveLoads == 0)
            {
                MeasureDemo();
                return;
            }
            if (_walk > 0 && nav.Mode == ViewMode.Walk && (_walking || _space.Scheduler.ActiveLoads == 0))
            {
                _walking = true; // hold W for the given time, from when the first panoramas are in
                _walk -= (float)delta;
                _nav.ScriptedHeld = _walk > 0 ? NavKeys.Forward : NavKeys.None;
                return;
            }
            if (_moveProp != 0 && nav.Mode == ViewMode.Walk && _space.Scheduler.ActiveLoads == 0 && _space.Scheduler.Queued == 0)
            {
                MovePropStep(nav);
                return;
            }
            if (_screenshotFrames < 0 && !nav.IsMoving && !nav.IsWaitingForPanorama && _goto < 0 && _space.Scheduler.ActiveLoads == 0 && _space.Scheduler.Queued == 0 && !nav.IsChangingMode && _space.TexturesLoaded >= _space.Model.Manifest.Textures.Count)
                _screenshotFrames = 30;
            if (_screenshotFrames > 0 && _views != null && _viewIndex == 0 && nav.DebugView != int.Parse(_views[0])) nav.DebugView = int.Parse(_views[0]);
            if (_screenshotFrames > 0 && --_screenshotFrames == 0)
            {
                var path = _views == null ? _screenshot : _screenshot.Replace(".png", $"_v{_views[_viewIndex]}.png");
                var err = GetViewport().GetTexture().GetImage().SavePng(path);
                if (_views != null && ++_viewIndex < _views.Length)
                {
                    nav.DebugView = int.Parse(_views[_viewIndex]);
                    _screenshotFrames = 5;
                    return;
                }
                var measured = _measure.Tool == null ? "" : $"; {_measure.Tool.Lines.Count} measurement(s), drawing {(_measure.Tool.Draft != null ? "yes" : "no")}, menu {(_menu.Visible ? "open" : "closed")}";
                var at = nav.CameraPosition;
                GD.Print($"[Matterportable] {_space.Status}; mode {nav.Mode}, sweep {nav.CurrentSweep}, camera ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}){measured}; screenshot {(err == Error.Ok ? "saved" : "failed: " + err)}");
                GetTree().Quit(err == Error.Ok ? 0 : 1);
            }
        }
    }
}
