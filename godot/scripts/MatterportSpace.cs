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
    /// A Matterport space in Godot (mirrors the Unity MatterportSpace): downloads into the shared cache if needed,
    /// runs the alignment solver once, loads the Core <see cref="SpaceModel"/> on a worker thread, builds the mesh and
    /// sky, and writes the navigator's <see cref="ViewBinding"/> to the shaders every frame. The node stays at the origin.
    /// </summary>
    public partial class MatterportSpace : Node3D
    {
        public string Link = "", CacheFolder = "";
        public int StartSweep;
        public bool DownloadIfMissing = true;
        public ViewerSettings Settings = new ViewerSettings();
        public string ModelId { get; private set; }
        public string SpaceDir { get; private set; }
        public SpaceModel Model { get; private set; }
        public PanoramaScheduler Scheduler { get; private set; }
        public PanoramaCache Panoramas { get; private set; }
        public bool IsLoaded { get; private set; }
        public string Status { get; private set; } = "";
        public string Error { get; private set; }
        public int TexturesLoaded { get; private set; }
        public float LastLoadSeconds { get; private set; }
        /// <summary>What the shaders show; the navigator sets it every frame.</summary>
        public ViewBinding View = ViewBinding.None;
        /// <summary>Walk mode's props and the one under the pointer (set by the navigator every frame): each prop's mesh
        /// follows its pose.</summary>
        public PropWorld Props;
        public int HighlightProp = -1;

        readonly List<ShaderMaterial> _materials = new List<ShaderMaterial>(); // every material reading the view state
        ShaderMaterial _sky;
        readonly Dictionary<string, ShaderMaterial> _partMaterials = new Dictionary<string, ShaderMaterial>();
        readonly List<MeshInstance3D> _propMeshes = new List<MeshInstance3D>(); // per prop (null: nothing to draw)
        Color[] _propTints = System.Array.Empty<Color>();
        Texture2DArray _empty;
        CancellationTokenSource _cts;
        BufferLog _log = new BufferLog();

        public override void _Ready()
        {
            ProcessPriority = 100; // after the navigator, so the shaders match this frame's camera
            _cts = new CancellationTokenSource();
            Load(_cts.Token);
        }

        public override void _ExitTree()
        {
            _cts?.Cancel();
            Panoramas?.Dispose();
        }

        public override void _Process(double delta)
        {
            if (!IsLoaded) return;
            Scheduler.Update((float)delta);
            Panoramas.Update();
            ApplyView();
        }

        // ------------------------------------------------------------------ loading

        async void Load(CancellationToken ct)
        {
            _log = new BufferLog { Forward = new GodotLog(Settings.VerboseLogging) };
            ulong started = Time.GetTicksMsec();
            if (!MatterportLink.TryParseModelId(Link, out var modelId)) { Fail("Not a Matterport link: " + Link); return; }
            ModelId = modelId;
            SpaceDir = CacheLayout.SpaceDir(GodotUtil.CacheRoot(CacheFolder), modelId);
            try
            {
                if (!SpaceManifest.Exists(SpaceDir))
                {
                    if (!DownloadIfMissing) { Fail($"Space {modelId} is not downloaded ({SpaceDir})."); return; }
                    using (var http = new HttpClientTransport())
                    {
                        var downloader = new SpaceDownloader(http, _log);
                        var task = downloader.DownloadAsync(modelId, SpaceDir, new DownloadOptions { PanoSizes = new[] { 512, Settings.PanoMaxSize } }, ct);
                        while (!task.IsCompleted)
                        {
                            Status = "Downloading: " + downloader.Status;
                            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                        }
                        await task;
                    }
                }
                var manifest = SpaceManifest.Load(SpaceDir);
                var cal = SpaceCalibration.LoadOrDefault(SpaceDir);
                if (manifest.Sweeps.Count == 0) { Fail("The cached space has no panoramas."); return; }
                string dir = SpaceDir;
                var log = _log;
                if (cal.NeedsSolve && manifest.Mesh != null)
                {
                    Status = "Aligning panoramas (first load only)";
                    try { cal = await Task.Run(() => CalibrationPipeline.Run(dir, GodotUtil.LoadLuma, log, ct), ct); }
                    catch (Exception e) when (!(e is OperationCanceledException)) { _log.Warn("Panorama alignment failed: " + GodotUtil.Unwrap(e)); }
                }
                Status = "Building geometry";
                var (model, pieces) = await Task.Run(() => { var m = SpaceModel.Load(dir, manifest, cal, log); return (m, BuildPieces(m)); }, ct);
                if (ct.IsCancellationRequested) return;
                Model = model;
                CreateMeshes(pieces);
                CreateSky();
                SetLayout(model.Calibration);
                Panoramas = new PanoramaCache(this);
                Scheduler = Panoramas.Scheduler = new PanoramaScheduler(model.Sweeps, Settings, Panoramas, model.Geometry != null);
                IsLoaded = true;
                LastLoadSeconds = (Time.GetTicksMsec() - started) / 1000f;
                Status = $"Loaded \"{manifest.Name}\" ({model.Sweeps.Count} sweeps) in {LastLoadSeconds:0.0}s";
                _log.Info(Status);
                WriteLoadLog();
                await LoadTextures(manifest, ct); // mesh textures stream in afterwards (dollhouse view, occluded areas)
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

        /// <summary>The room and each prop (<see cref="SpaceModel.MeshPieces"/>) as compact surfaces, one per material
        /// (worker thread). Mirroring Z flips the winding, which matches Godot's front-face convention (dollhouse back-face
        /// culling shows the inside), so indices stay as they are.</summary>
        static List<List<Surface>> BuildPieces(SpaceModel model)
        {
            var geo = model.Geometry;
            var pieces = new List<List<Surface>>();
            foreach (var piece in model.MeshPieces())
            {
                var surfaces = new List<Surface>();
                foreach (var (part, indices) in piece) surfaces.Add(BuildSurface(geo, geo.Parts[part].Material ?? "", indices));
                pieces.Add(surfaces);
            }
            return pieces;
        }

        static Surface BuildSurface(SpaceGeometry geo, string material, int[] indices)
        {
                var map = new Dictionary<int, int>();
                var verts = new List<Vector3>();
                var uvs = new List<Vector2>();
                var idx = new int[indices.Length];
                for (int i = 0; i < idx.Length; i++)
                {
                    int v = indices[i];
                    if (!map.TryGetValue(v, out int k))
                    {
                        map[v] = k = verts.Count;
                        verts.Add(geo.Positions[v].ToGodot());
                        uvs.Add(geo.HasUvs ? new Vector2(geo.Uvs[v].X, 1f - geo.Uvs[v].Y) : Vector2.Zero);
                    }
                    idx[i] = k;
                }
                return new Surface { Material = material, Vertices = verts.ToArray(), Uvs = uvs.ToArray(), Indices = idx };
        }

        /// <summary>The room's mesh and one per prop, sharing a material per part (so one parameter write reaches all).</summary>
        void CreateMeshes(List<List<Surface>> pieces)
        {
            var shader = GD.Load<Shader>("res://shaders/mesh.gdshader");
            for (int piece = 0; piece < pieces.Count; piece++)
            {
                var mesh = new ArrayMesh();
                foreach (var s in pieces[piece])
                {
                    var arrays = new Godot.Collections.Array();
                    arrays.Resize((int)Mesh.ArrayType.Max);
                    arrays[(int)Mesh.ArrayType.Vertex] = s.Vertices;
                    arrays[(int)Mesh.ArrayType.TexUV] = s.Uvs;
                    arrays[(int)Mesh.ArrayType.Index] = s.Indices;
                    mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                    if (!_partMaterials.TryGetValue(s.Material, out var mat))
                    {
                        _partMaterials[s.Material] = mat = new ShaderMaterial { Shader = shader };
                        _materials.Add(mat);
                    }
                    mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
                }
                var instance = mesh.GetSurfaceCount() == 0 ? null : new MeshInstance3D
                {
                    Name = piece == 0 ? "Matterport Mesh" : "Prop " + (piece - 1), Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                if (instance != null) AddChild(instance);
                if (piece > 0) _propMeshes.Add(instance);
            }
            _propTints = new Color[_propMeshes.Count];
        }

        void CreateSky()
        {
            var mat = _sky = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sky.gdshader") };
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

        async Task LoadTextures(SpaceManifest manifest, CancellationToken ct)
        {
            var pending = new List<(ShaderMaterial mat, Task<Image> image)>();
            foreach (var entry in manifest.Textures)
            {
                var path = CacheLayout.Resolve(SpaceDir, entry.File);
                if (!_partMaterials.TryGetValue(entry.Material ?? "", out var mat) || !File.Exists(path)) continue;
                pending.Add((mat, Task.Run(() => { var img = GodotUtil.LoadImage(path); img.GenerateMipmaps(); return img; }, ct)));
            }
            foreach (var (mat, image) in pending)
            {
                try { mat.SetShaderParameter("mesh_tex", ImageTexture.CreateFromImage(await image)); TexturesLoaded++; }
                catch (Exception e) when (!(e is OperationCanceledException)) { _log.Warn("Mesh texture failed: " + GodotUtil.Unwrap(e)); }
            }
        }

        void Fail(string message)
        {
            Error = Status = message;
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
            if (!CubeFaces.TryBuildLookup(cal.Faces, out var slice, out var right, out var up))
                CubeFaces.TryBuildLookup(CubeFaces.DefaultMatterportLayout(), out slice, out right, out up);
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

        /// <summary>Binds the navigator's view: the photos and depth cubes of the chosen sweeps, their poses, the state,
        /// back-face culling (dollhouse and floor plan show the inside of the walls).</summary>
        void ApplyView()
        {
            var v = View;
            _empty ??= EmptyArray();
            var bg = Settings.BackgroundColor;
            var parameters = new (string name, Variant value)[]
            {
                ("pano_a", Panoramas.Faces(v.PanoA) ?? _empty), ("pano_b", Panoramas.Faces(v.PanoB) ?? _empty),
                ("depth_a", Panoramas.Depth(v.DepthA) ?? _empty), ("depth_b", Panoramas.Depth(v.DepthB) ?? _empty),
                ("pos_a", Model.Sweeps[v.PoseA].Position.ToGodot()), ("pos_b", Model.Sweeps[v.PoseB].Position.ToGodot()),
                ("rot_a", GodotUtil.ShaderRotation(Model.Sweeps[v.PoseA].WorldFromPano)), ("rot_b", GodotUtil.ShaderRotation(Model.Sweeps[v.PoseB].WorldFromPano)),
                ("mp_state", new Vector4(v.Blend, v.Projection, v.UseDepth ? 1f : 0f, v.DebugView)),
                ("mp_valid", new Vector4(v.ValidA ? 1f : 0f, v.ValidB ? 1f : 0f, v.ClipHeight, 0f)),
                ("mp_background", new Vector3(bg.R, bg.G, bg.B)),
            };
            foreach (var m in _materials)
            {
                foreach (var (name, value) in parameters) m.SetShaderParameter(name, value);
                if (m == _sky) continue;
                m.SetShaderParameter("cull_back", v.CullBack);
                m.SetShaderParameter("space_matrix", GlobalTransform);
            }
            if (Props == null) return;
            for (int i = 0; i < _propMeshes.Count; i++)
            {
                if (_propMeshes[i] == null) continue;
                var pose = Props.Poses[i];
                _propMeshes[i].Transform = new Transform3D(new Basis(pose.Rotation.ToGodot()), pose.Translation.ToGodot());
                var t = Props.Tint(i, HighlightProp, v.DebugView);
                var tint = new Color(t.R, t.G, t.B, t.A);
                if (tint == _propTints[i]) continue;
                _propTints[i] = tint;
                _propMeshes[i].SetInstanceShaderParameter("prop_tint", tint);
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
    }
}
