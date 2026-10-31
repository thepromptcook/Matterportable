using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Matterportable.Core;

namespace Matterportable
{
    /// <summary>
    /// A Matterport space in Godot (mirrors the Unity MatterportSpace): resolves the link, downloads into the shared
    /// cache if needed, runs the alignment solver once, builds the mesh on a worker thread, then streams panoramas.
    /// Writes the view state to the shaders once per frame (after the navigator). The node stays at the origin.
    /// </summary>
    public partial class MatterportSpace : Node3D
    {
        static readonly ViewerSettings DefaultSettings = new ViewerSettings();
        public static readonly string[] DebugViewNames = { "Final", "Mesh texture", "Panorama only", "Visibility A/B", "Blend weights" };
        const float NoClip = 1e9f;

        [Export] public string Link = "";
        [Export] public string CacheFolder = "";
        [Export] public int StartSweep;
        [Export] public bool DownloadIfMissing = true;
        [Export] public ViewerSettings SettingsResource;

        public ViewerSettings Settings => SettingsResource ?? DefaultSettings;
        public string ModelId { get; private set; }
        public string SpaceDir { get; private set; }
        public SpaceManifest Manifest { get; private set; }
        public SpaceCalibration Calibration { get; private set; }
        public SpaceGeometry Geometry { get; private set; }
        public IReadOnlyList<SweepInfo> Sweeps => _sweeps;
        public PanoramaCache Panoramas { get; private set; }
        public bool IsLoaded { get; private set; }
        public string Status { get; private set; } = "";
        public float Progress { get; private set; }
        public string Error { get; private set; }
        public int FloorCount => _floorHeights.Count;
        public int TexturesLoaded { get; private set; }
        public float LastLoadSeconds { get; private set; }
        public event Action<MatterportSpace> Loaded;

        // View state (set by the navigator through SetView).
        public int ViewA = -1, ViewB = -1;
        public float Blend, Projection = 1f, ClipHeight = float.PositiveInfinity;
        int _debugView;

        List<SweepInfo> _sweeps = new List<SweepInfo>();
        readonly List<float> _floorHeights = new List<float>();
        readonly List<ShaderMaterial> _materials = new List<ShaderMaterial>(); // every material reading the view state
        readonly Dictionary<string, ShaderMaterial> _partMaterials = new Dictionary<string, ShaderMaterial>();
        Texture2DArray _empty;
        CancellationTokenSource _cts;
        BufferLog _log = new BufferLog();

        public int DebugView
        {
            get => _debugView;
            set => _debugView = ((value % DebugViewNames.Length) + DebugViewNames.Length) % DebugViewNames.Length;
        }

        public override void _Ready()
        {
            ProcessPriority = 100; // after the navigator, so the shaders match this frame's camera
            Reload();
        }

        public override void _ExitTree() => _cts?.Cancel();

        public override void _Process(double delta)
        {
            if (!IsLoaded) return;
            Panoramas.Update();
            ApplyView();
        }

        public void Reload()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            Load(_cts.Token);
        }

        // ------------------------------------------------------------------ loading

        async void Load(CancellationToken ct)
        {
            IsLoaded = false;
            Error = null;
            _log = new BufferLog { Forward = new GodotLog(Settings.VerboseLogging) };
            ulong started = Time.GetTicksMsec();
            SetStatus("Resolving link", 0f);
            if (!MatterportLink.TryParseModelId(Link, out var modelId))
            {
                Fail(string.IsNullOrWhiteSpace(Link) ? "No Matterport link set." : "Not a Matterport link: " + Link);
                return;
            }
            ModelId = modelId;
            SpaceDir = CacheLayout.SpaceDir(GodotUtil.CacheRoot(CacheFolder), modelId);
            try
            {
                // 1) Download if needed.
                if (!SpaceManifest.Exists(SpaceDir))
                {
                    if (!DownloadIfMissing) { Fail($"Space {modelId} is not downloaded ({SpaceDir})."); return; }
                    using (var http = new HttpClientTransport())
                    {
                        var downloader = new SpaceDownloader(http, _log);
                        var task = downloader.DownloadAsync(modelId, SpaceDir, new DownloadOptions { PanoSizes = new[] { 512, Settings.PanoMaxSize } }, ct);
                        while (!task.IsCompleted)
                        {
                            SetStatus("Downloading: " + downloader.Status, 0.05f + 0.5f * downloader.Status.Fraction);
                            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                        }
                        await task;
                    }
                }

                // 2) Manifest and calibration (the solver runs once per space; failures are not retried automatically).
                Manifest = SpaceManifest.Load(SpaceDir);
                Calibration = SpaceCalibration.LoadOrDefault(SpaceDir);
                if (Manifest.Sweeps.Count == 0) { Fail("The cached space has no panoramas."); return; }
                if (Calibration.NeedsSolve && Manifest.Mesh != null)
                {
                    SetStatus("Aligning panoramas (first load only)", 0.55f);
                    var dir = SpaceDir;
                    var log = _log;
                    try { Calibration = await Task.Run(() => CalibrationPipeline.Run(dir, GodotUtil.LoadLuma, log, ct), ct); }
                    catch (Exception e) when (!(e is OperationCanceledException)) { _log.Warn("Panorama alignment failed: " + GodotUtil.Unwrap(e)); }
                }

                // 3) Geometry and Godot mesh arrays on a worker thread.
                SetStatus("Building geometry", 0.65f);
                var (geo, sweeps, surfaces) = await Task.Run(() => BuildGeometry(SpaceDir, Manifest, Calibration, _log), ct);
                if (ct.IsCancellationRequested) return;
                Geometry = geo;
                _sweeps = sweeps;
                ComputeFloorHeights();

                // 4) Scene objects.
                SetStatus("Creating meshes", 0.75f);
                CreateMesh(surfaces);
                CreateSky();
                SetLayout(Calibration);
                Panoramas = new PanoramaCache(this) { IsPinned = s => s == ViewA || s == ViewB };
                StartSweep = Mathf.Clamp(StartSweep, 0, _sweeps.Count - 1);
                Panoramas.RequestProgressive(StartSweep, Settings.PanoMaxSize, 0f);
                Panoramas.RequestDepth(StartSweep);

                IsLoaded = true;
                LastLoadSeconds = (Time.GetTicksMsec() - started) / 1000f;
                SetStatus($"Loaded \"{Manifest.Name}\" ({_sweeps.Count} sweeps) in {LastLoadSeconds:0.0}s", 1f);
                _log.Info(Status);
                WriteLoadLog();
                Loaded?.Invoke(this);

                // 5) Mesh textures stream in afterwards (dollhouse view and occluded areas).
                await LoadTextures(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Fail(GodotUtil.Unwrap(e)); }
        }

        sealed class Surface
        {
            public string Material;
            public Vector3[] Vertices;
            public Vector2[] Uvs;
            public int[] Indices;
        }

        static (SpaceGeometry, List<SweepInfo>, List<Surface>) BuildGeometry(string dir, SpaceManifest manifest, SpaceCalibration cal, ILogSink log)
        {
            SpaceGeometry geo = null;
            var surfaces = new List<Surface>();
            if (manifest.Mesh != null)
            {
                var bytes = File.ReadAllBytes(CacheLayout.Resolve(dir, manifest.Mesh.File));
                var mesh = DamDecoder.Decode(bytes, log);
                if (!mesh.HasUvs) DamDecoder.WriteDiagnostics(dir, bytes);
                geo = SpaceGeometry.Build(mesh, cal);
                // One compact surface per material. Mirroring Z flips the winding, which matches Godot's front-face
                // convention (verified: dollhouse back-face culling shows the inside), so indices are kept as they are.
                foreach (var part in geo.Parts)
                {
                    var map = new Dictionary<int, int>();
                    var verts = new List<Vector3>();
                    var uvs = new List<Vector2>();
                    var idx = new int[part.Indices.Length];
                    for (int i = 0; i < idx.Length; i++)
                    {
                        int v = part.Indices[i];
                        if (!map.TryGetValue(v, out int k))
                        {
                            map[v] = k = verts.Count;
                            verts.Add(geo.Positions[v].ToGodot());
                            uvs.Add(geo.HasUvs ? new Vector2(geo.Uvs[v].X, 1f - geo.Uvs[v].Y) : Vector2.Zero);
                        }
                        idx[i] = k;
                    }
                    surfaces.Add(new Surface { Material = part.Material ?? "", Vertices = verts.ToArray(), Uvs = uvs.ToArray(), Indices = idx });
                }
            }
            return (geo, SweepBuilder.Build(manifest, cal, geo, log), surfaces);
        }

        void CreateMesh(List<Surface> surfaces)
        {
            foreach (var child in GetChildren()) child.QueueFree();
            _materials.Clear();
            _partMaterials.Clear();
            TexturesLoaded = 0;
            if (surfaces.Count == 0) return;
            var shader = GD.Load<Shader>("res://shaders/mesh.gdshader");
            var mesh = new ArrayMesh();
            foreach (var s in surfaces)
            {
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = s.Vertices;
                arrays[(int)Mesh.ArrayType.TexUV] = s.Uvs;
                arrays[(int)Mesh.ArrayType.Index] = s.Indices;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                var mat = new ShaderMaterial { Shader = shader };
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
                _materials.Add(mat);
                _partMaterials[s.Material] = mat;
            }
            AddChild(new MeshInstance3D { Name = "Matterport Mesh", Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }

        void CreateSky()
        {
            var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sky.gdshader") };
            _materials.Add(mat);
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = mat, RadianceSize = Sky.RadianceSizeEnum.Size32 },
                AmbientLightSource = Godot.Environment.AmbientSource.Disabled,
                ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            };
            AddChild(new WorldEnvironment { Name = "Matterport Sky", Environment = env });
        }

        async Task LoadTextures(CancellationToken ct)
        {
            var pending = new List<(ShaderMaterial mat, Task<Image> image)>();
            foreach (var entry in Manifest.Textures)
            {
                var path = CacheLayout.Resolve(SpaceDir, entry.File);
                if (!_partMaterials.TryGetValue(entry.Material ?? "", out var mat) || !File.Exists(path)) continue;
                pending.Add((mat, Task.Run(() =>
                {
                    var img = GodotUtil.LoadImage(path);
                    img.GenerateMipmaps();
                    return img;
                }, ct)));
            }
            foreach (var (mat, image) in pending)
            {
                try { mat.SetShaderParameter("mesh_tex", ImageTexture.CreateFromImage(await image)); TexturesLoaded++; }
                catch (Exception e) when (!(e is OperationCanceledException)) { _log.Warn("Mesh texture failed: " + GodotUtil.Unwrap(e)); }
            }
        }

        void ComputeFloorHeights()
        {
            _floorHeights.Clear();
            int floors = 0;
            foreach (var s in _sweeps) floors = Math.Max(floors, s.Floor + 1);
            for (int f = 0; f < floors; f++)
            {
                var ys = new List<float>();
                foreach (var s in _sweeps) if (s.Floor == f) ys.Add(s.FloorPoint.Y);
                ys.Sort();
                _floorHeights.Add(ys.Count > 0 ? ys[ys.Count / 2] : (f > 0 ? _floorHeights[f - 1] + 3f : 0f));
            }
        }

        void SetStatus(string s, float progress)
        {
            Status = s;
            Progress = progress;
        }

        void Fail(string message)
        {
            Error = message;
            SetStatus(message, 0f);
            _log.Error(message);
            WriteLoadLog();
        }

        void WriteLoadLog()
        {
            if (string.IsNullOrEmpty(SpaceDir) || !Directory.Exists(SpaceDir)) return;
            try
            {
                var env = $"Godot {Engine.GetVersionInfo()["string"]}, {OS.GetName()}, {RenderingServer.GetVideoAdapterName()} ({RenderingServer.GetCurrentRenderingMethod()})";
                IoUtil.WriteAllTextAtomic(Path.Combine(SpaceDir, CacheLayout.DebugDir, "load.log"), $"{DateTime.Now:u}  {env}\n{_log.Text()}");
            }
            catch (IOException) { /* diagnostics only */ }
        }

        // ------------------------------------------------------------------ shader state

        void SetLayout(SpaceCalibration cal)
        {
            if (cal == null || !CubeFaces.TryBuildLookup(cal.Faces, out var slice, out var right, out var up))
            {
                _log.Warn("Invalid panorama face layout; using the default.");
                CubeFaces.TryBuildLookup(CubeFaces.DefaultMatterportLayout(), out slice, out right, out up);
            }
            var r = new Vector4[6];
            var u = new Vector4[6];
            for (int c = 0; c < 6; c++)
            {
                r[c] = new Vector4(right[c].X, right[c].Y, right[c].Z, slice[c]);
                u[c] = new Vector4(up[c].X, up[c].Y, up[c].Z, 0);
            }
            foreach (var m in _materials)
            {
                m.SetShaderParameter("face_right", r);
                m.SetShaderParameter("face_up", u);
            }
        }

        /// <summary>Sets what the shaders show (called by the navigator every frame).</summary>
        public void SetView(int sweepA, int sweepB, float blend, float projection, float clipHeight = float.PositiveInfinity)
        {
            ViewA = sweepA;
            ViewB = sweepB;
            Blend = Mathf.Clamp(blend, 0f, 1f);
            Projection = Mathf.Clamp(projection, 0f, 1f);
            ClipHeight = clipHeight;
            Panoramas?.Touch(sweepA);
            Panoramas?.Touch(sweepB);
        }

        public void SetCullBack(bool cull)
        {
            foreach (var m in _materials) if (m.Shader.GetMode() == Shader.Mode.Spatial) m.SetShaderParameter("cull_back", cull);
        }

        /// <summary>Same logic as the Unity SpaceRenderer.Apply: if only one panorama is ready, both slots show it.</summary>
        void ApplyView()
        {
            var a = ViewA >= 0 ? Panoramas.Get(ViewA) : null;
            var b = ViewB >= 0 ? Panoramas.Get(ViewB) : null;
            bool validA = a?.Faces != null, validB = b?.Faces != null;
            bool depth = Settings.DepthMapSize > 0 && a?.Depth != null && (b == null || b.Depth != null || Blend <= 0f);
            _empty ??= EmptyArray();
            var texA = validA ? a.Faces : validB ? b.Faces : _empty;
            var texB = validB ? b.Faces : texA;
            var depthA = a?.Depth ?? _empty;
            var depthB = b?.Depth ?? a?.Depth ?? _empty;
            int ia = validA ? ViewA : validB ? ViewB : Math.Max(0, ViewA);
            int ib = validB ? ViewB : ia;
            ia = Mathf.Clamp(ia, 0, _sweeps.Count - 1);
            ib = Mathf.Clamp(ib, 0, _sweeps.Count - 1);
            var state = new Vector4(validA && validB ? Blend : validB ? 1f : 0f, Projection, depth ? 1f : 0f, _debugView);
            var valid = new Vector4(validA || validB ? 1f : 0f, validA && validB ? 1f : 0f, Math.Min(ClipHeight, NoClip), 0f);
            var rotA = GodotUtil.ShaderRotation(_sweeps[ia].WorldFromPano);
            var rotB = GodotUtil.ShaderRotation(_sweeps[ib].WorldFromPano);
            var bg = Settings.BackgroundColor;
            foreach (var m in _materials)
            {
                m.SetShaderParameter("pano_a", texA);
                m.SetShaderParameter("pano_b", texB);
                m.SetShaderParameter("depth_a", depthA);
                m.SetShaderParameter("depth_b", depthB);
                m.SetShaderParameter("pos_a", SweepWorldPosition(ia));
                m.SetShaderParameter("pos_b", SweepWorldPosition(ib));
                m.SetShaderParameter("rot_a", rotA);
                m.SetShaderParameter("rot_b", rotB);
                m.SetShaderParameter("mp_state", state);
                m.SetShaderParameter("mp_valid", valid);
                m.SetShaderParameter("mp_background", new Vector3(bg.R, bg.G, bg.B));
            }
        }

        static Texture2DArray EmptyArray()
        {
            var layers = new Godot.Collections.Array<Image>();
            for (int i = 0; i < 6; i++)
            {
                var img = Image.CreateEmpty(1, 1, false, Image.Format.Rgb8);
                img.Fill(new Color(0.5f, 0.5f, 0.5f));
                layers.Add(img);
            }
            var tex = new Texture2DArray();
            tex.CreateFromImages(layers);
            return tex;
        }

        // ------------------------------------------------------------------ queries (Godot world space)

        public Vector3 SweepWorldPosition(int i) => _sweeps[i].Position.ToGodot();
        public Vector3 MarkerWorldPosition(int i) => _sweeps[i].FloorPoint.ToGodot();
        public float FloorHeightWorld(int floor) => floor >= 0 && floor < _floorHeights.Count ? _floorHeights[floor] : 0f;

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal, out float distance)
        {
            point = normal = Vector3.Zero;
            distance = 0;
            if (Geometry == null || !Geometry.Bvh.Raycast(origin.ToCore(), direction.Normalized().ToCore(), maxDistance, out var hit)) return false;
            point = hit.Point.ToGodot();
            normal = hit.Normal.ToGodot();
            distance = hit.Distance;
            return true;
        }

        /// <summary>Raycast that ignores everything above a clip height (dollhouse floor filtering).</summary>
        public bool RaycastBelow(Vector3 origin, Vector3 direction, float maxDistance, float clipHeight, out Vector3 point, out Vector3 normal)
        {
            point = normal = Vector3.Zero;
            float travelled = 0;
            for (int i = 0; i < 8; i++)
            {
                if (!Raycast(origin, direction, maxDistance - travelled, out point, out normal, out float dist)) return false;
                if (point.Y <= clipHeight) return true;
                travelled += dist + 0.01f;
                origin = point + direction * 0.01f;
            }
            return false;
        }

        public bool LineOfSight(Vector3 from, Vector3 to) => Geometry == null || !Geometry.Bvh.Occluded(from.ToCore(), to.ToCore(), 0.1f);

        public Aabb WorldBounds
        {
            get
            {
                if (Geometry == null)
                {
                    var bb = new Aabb(SweepWorldPosition(0), Vector3.Zero);
                    for (int i = 1; i < _sweeps.Count; i++) bb = bb.Expand(SweepWorldPosition(i));
                    return bb;
                }
                return new Aabb(Geometry.BoundsMin.ToGodot(), Vector3.Zero).Expand(Geometry.BoundsMax.ToGodot());
            }
        }
    }
}
