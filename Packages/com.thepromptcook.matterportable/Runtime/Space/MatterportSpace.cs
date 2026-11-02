using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Matterportable.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Matterportable
{
    /// <summary>
    /// A Matterport space in the scene (mirrors the Godot MatterportSpace). Paste a link, press Play: the space is
    /// loaded from the cache folder (downloaded first if needed), the alignment solver runs once, the Core
    /// <see cref="SpaceModel"/> loads on a worker thread, the mesh is built, and every frame the navigator's
    /// <see cref="ViewBinding"/> goes to the shader globals (MatterportCommon.hlsl). In Edit mode it shows a textured
    /// preview with sweep gizmos. Collaborators: <see cref="SpaceMesh"/> (Unity mesh), <see cref="PanoramaCache"/>
    /// (panorama loading), <see cref="CalibrationRunner"/> (alignment).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Matterportable/Matterport Space")]
    public sealed class MatterportSpace : MonoBehaviour
    {
        public const string PlayFromSweepKey = "Matterportable.PlayFromSweep";

        [Tooltip("Matterport link or model ID, e.g. https://discover.matterport.com/space/rGMMnZDh3Tf")]
        public string link = "";
        [Tooltip("Cache folder (relative to the project or absolute). Empty = <project>/MatterportCache.")]
        public string cacheFolder = "";
        [Tooltip("Sweep (panorama) index to start at.")]
        public int startSweep;
        [Tooltip("Download the space automatically when entering Play mode if it is not cached yet.")]
        public bool downloadIfMissing = true;
        [Tooltip("Show the textured mesh in the Scene view while editing.")]
        public bool previewInEditMode = true;
        [Tooltip("Adds a MeshCollider so your own gameplay code can raycast the space with Physics.")]
        public bool addMeshCollider;
        [Tooltip("Navigation, streaming and rendering tunables (the same in Godot and the web). Edits apply live in Play mode.")]
        public ViewerSettings settings = new ViewerSettings();

        public string ModelId { get; private set; }
        public string SpaceDir { get; private set; }
        /// <summary>Sweeps, floors, bounds and ray casts in the space's local frame (also set by the Edit-mode preview).</summary>
        public SpaceModel Model { get; private set; }
        public PanoramaScheduler Scheduler { get; private set; }
        public PanoramaCache Panoramas { get; private set; }
        public bool IsLoaded { get; private set; }
        public string Status { get; private set; } = "";
        public float Progress { get; private set; }
        public string Error { get; private set; }
        public int TexturesLoaded => _mesh?.TexturesLoaded ?? 0;
        public GameObject MeshObject => _mesh?.Object;
        public float LastLoadSeconds { get; private set; }
        /// <summary>What the shaders show; the navigator sets it every frame.</summary>
        public ViewBinding View { get; set; } = ViewBinding.None;
        /// <summary>Walk mode's props and the one under the pointer (set by the navigator every frame): each prop's mesh
        /// follows its pose.</summary>
        public PropWorld Props { get; set; }
        public int HighlightProp { get; set; } = -1;

        static readonly int IdPanoA = Shader.PropertyToID("_MP_PanoA"), IdPanoB = Shader.PropertyToID("_MP_PanoB");
        static readonly int IdDepthA = Shader.PropertyToID("_MP_DepthA"), IdDepthB = Shader.PropertyToID("_MP_DepthB");
        static readonly int IdPosA = Shader.PropertyToID("_MP_PosA"), IdPosB = Shader.PropertyToID("_MP_PosB");
        static readonly int IdRotA = Shader.PropertyToID("_MP_RotA"), IdRotB = Shader.PropertyToID("_MP_RotB");
        static readonly int IdSpaceRot = Shader.PropertyToID("_MP_SpaceRot"), IdState = Shader.PropertyToID("_MP_State");
        static readonly int IdFaceRight = Shader.PropertyToID("_MP_FaceRight"), IdFaceUp = Shader.PropertyToID("_MP_FaceUp");
        static readonly int IdValid = Shader.PropertyToID("_MP_Valid"), IdBackground = Shader.PropertyToID("_MP_Background");
        static readonly int IdSpaceToWorld = Shader.PropertyToID("_MP_SpaceToWorld"), IdMoved = Shader.PropertyToID("_MP_Moved");
        static readonly int IdCleanA = Shader.PropertyToID("_MP_CleanA"), IdCleanB = Shader.PropertyToID("_MP_CleanB"), IdClean = Shader.PropertyToID("_MP_Clean");
        static readonly int IdPaintedA = Shader.PropertyToID("_MP_PaintedA"), IdPaintedB = Shader.PropertyToID("_MP_PaintedB");
        readonly Vector4[] _moved = new Vector4[64]; // 1 per prop that has moved (prop i: element i / 4, component i % 4)

        SpaceMesh _mesh;
        Texture2DArray _empty;
        Material _skybox, _previousSkybox;
        bool _cullBack;
        ViewerSettings _live; // the instance the Core objects read
        CancellationTokenSource _cts;
        Coroutine _loadRoutine;
        BufferLog _log = new BufferLog();

        // ------------------------------------------------------------------ lifecycle

        void OnEnable()
        {
            if (!Application.isPlaying && previewInEditMode) BuildEditorPreview();
        }

        void Start()
        {
            if (!Application.isPlaying) return;
#if UNITY_EDITOR
            int requested = UnityEditor.SessionState.GetInt(PlayFromSweepKey, -1);
            if (requested >= 0)
            {
                startSweep = requested;
                UnityEditor.SessionState.EraseInt(PlayFromSweepKey);
            }
#endif
            Reload();
        }

        void OnDisable()
        {
            _cts?.Cancel();
            Unload();
        }

        void OnValidate()
        {
#if UNITY_EDITOR
            if (Application.isPlaying)
            {
                // Inspector edits reach the navigator and scheduler even if Unity replaced the settings instance.
                if (_live != null && !ReferenceEquals(_live, settings))
                {
                    JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(settings), _live);
                    settings = _live;
                }
                return;
            }
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null || !isActiveAndEnabled || Application.isPlaying) return;
                var id = MatterportLink.TryParseModelId(link, out var parsed) ? parsed : null;
                if (id != ModelId || previewInEditMode != (_mesh != null)) BuildEditorPreview();
            };
#endif
        }

        void Update()
        {
            if (!Application.isPlaying || !IsLoaded) return;
            Scheduler.Update(Time.unscaledDeltaTime);
            Panoramas.Update();
        }

        // After the navigator's Update, so the shaders always match this frame's camera.
        void LateUpdate()
        {
            if (Application.isPlaying && IsLoaded) ApplyView();
        }

        /// <summary>Reloads the space from the cache (downloading it if needed and allowed).</summary>
        public void Reload()
        {
            if (!Application.isPlaying)
            {
                BuildEditorPreview();
                return;
            }
            if (_loadRoutine != null) StopCoroutine(_loadRoutine);
            _cts?.Cancel();
            Unload();
            _cts = new CancellationTokenSource();
            _loadRoutine = StartCoroutine(LoadRoutine(_cts.Token));
        }

        void Unload()
        {
            IsLoaded = false;
            Panoramas?.Dispose();
            Panoramas = null;
            Scheduler = null;
            Model = null;
            View = ViewBinding.None;
            _mesh?.Dispose();
            _mesh = null;
            _cullBack = false;
            if (_skybox != null && RenderSettings.skybox == _skybox) RenderSettings.skybox = _previousSkybox;
            MpUtil.SafeDestroy(_skybox);
            MpUtil.SafeDestroy(_empty);
        }

        // ------------------------------------------------------------------ loading (Play mode)

        IEnumerator LoadRoutine(CancellationToken ct)
        {
            Error = null;
            _log = new BufferLog { Forward = UnityLog.For(settings.VerboseLogging) };
            float started = Time.realtimeSinceStartup;
            SetStatus("Resolving link", 0f);
            if (!MatterportLink.TryParseModelId(link, out var modelId))
            {
                Fail(string.IsNullOrWhiteSpace(link) ? "No Matterport link set on the MatterportSpace component." : $"Not a Matterport link: {link}");
                yield break;
            }
            ModelId = modelId;
            SpaceDir = MatterportPaths.SpaceDir(cacheFolder, modelId);

            // 1) Download if needed.
            if (!SpaceManifest.Exists(SpaceDir))
            {
                if (!downloadIfMissing)
                {
                    Fail($"Space {modelId} is not downloaded ({SpaceDir}). Open it from the in-game menu, or use the inspector's Download button.");
                    yield break;
                }
                using (var http = new HttpClientTransport())
                {
                    var downloader = new SpaceDownloader(http, _log);
                    var task = downloader.DownloadAsync(modelId, SpaceDir, new DownloadOptions { PanoSizes = new[] { 512, settings.PanoMaxSize } }, ct);
                    while (!task.IsCompleted)
                    {
                        SetStatus("Downloading: " + downloader.Status, 0.05f + 0.5f * downloader.Status.Fraction);
                        yield return null;
                    }
                    if (task.IsCanceled) yield break;
                    if (task.IsFaulted) { Fail("Download failed: " + Unwrap(task.Exception)); yield break; }
                }
            }

            // 2) Manifest and calibration (the solver runs once per space; failures are not retried automatically).
            SpaceManifest manifest;
            SpaceCalibration cal;
            try
            {
                manifest = SpaceManifest.Load(SpaceDir);
                cal = SpaceCalibration.LoadOrDefault(SpaceDir);
            }
            catch (Exception e)
            {
                Fail("Could not read the cached space: " + e.Message);
                yield break;
            }
            if (manifest.Sweeps.Count == 0) { Fail("The cached space has no panoramas."); yield break; }
            if (cal.NeedsSolve && manifest.Mesh != null)
            {
                SetStatus("Aligning panoramas (first load only)", 0.55f);
                yield return null;
                Task<SpaceCalibration> solve = null;
                try { solve = CalibrationRunner.RunAsync(SpaceDir, _log, ct); }
                catch (Exception e) { _log.Warn("Could not prepare panorama alignment: " + e.Message); }
                if (solve != null)
                {
                    while (!solve.IsCompleted) yield return null;
                    if (solve.IsFaulted) _log.Warn("Panorama alignment failed: " + Unwrap(solve.Exception));
                    else if (!solve.IsCanceled) cal = solve.Result;
                }
            }

            // 3) The model on a worker thread (reused from the previous Play session when nothing changed).
            SetStatus("Building geometry", 0.65f);
            string dir = SpaceDir;
            var log = _log;
            var load = Task.Run(() => SpaceModel.Load(dir, manifest, cal, log), ct);
            while (!load.IsCompleted) yield return null;
            if (load.IsCanceled) yield break;
            if (load.IsFaulted) { Fail("Could not build the mesh: " + Unwrap(load.Exception)); yield break; }

            // 4) Unity objects.
            SetStatus("Creating meshes", 0.75f);
            try
            {
                Model = load.Result;
                if (Model.Geometry != null) _mesh = SpaceMesh.Create(Model, transform, "Matterport " + manifest.Name, true, addMeshCollider);
                CreateShaderState();
                Panoramas = new PanoramaCache(this);
                Scheduler = Panoramas.Scheduler = new PanoramaScheduler(Model.Sweeps, _live = settings, Panoramas, Model.Geometry != null);
            }
            catch (Exception e) { Fail(e.Message); yield break; }
            IsLoaded = true;
            LastLoadSeconds = Time.realtimeSinceStartup - started;
            SetStatus($"Loaded \"{manifest.Name}\" ({Model.Sweeps.Count} sweeps) in {LastLoadSeconds:0.0}s", 1f);
            _log.Info(Status);
            foreach (var w in manifest.Warnings) _log.Info("Download note: " + w);
            WriteLoadLog();

            // 5) Mesh textures stream in afterwards (they matter for dollhouse view and occluded areas).
            if (_mesh != null) yield return _mesh.LoadTexturesRoutine(manifest, SpaceDir);
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
                IoUtil.WriteAllTextAtomic(Path.Combine(SpaceDir, CacheLayout.DebugDir, "load.log"),
                    $"{DateTime.Now:u}  {MpUtil.EnvironmentSummary()}\n{_log.Text()}");
            }
            catch (IOException) { /* diagnostics only */ }
        }

        static string Unwrap(Exception e)
        {
            while (e is AggregateException && e.InnerException != null) e = e.InnerException;
            return e?.Message ?? "unknown error";
        }

        // ------------------------------------------------------------------ shader state (the only writer of the globals)

        void CreateShaderState()
        {
            _empty = new Texture2DArray(1, 1, 6, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = "Matterport empty pano" };
            for (int i = 0; i < 6; i++) _empty.SetPixels32(new[] { new Color32(128, 128, 128, 255) }, i);
            _empty.Apply(false, true);
            var sky = Shader.Find("Matterportable/Skybox");
            if (sky != null)
            {
                _skybox = new Material(sky) { hideFlags = HideFlags.DontSave, name = "Matterport Skybox" };
                _previousSkybox = RenderSettings.skybox;
                RenderSettings.skybox = _skybox;
            }
            // Panorama face layout (from calibration).
            if (!CubeFaces.TryBuildLookup(Model.Calibration.Faces, out var slice, out var right, out var up))
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
            Shader.SetGlobalVectorArray(IdFaceRight, r);
            Shader.SetGlobalVectorArray(IdFaceUp, u);
        }

        /// <summary>Binds the navigator's view: the photos and depth cubes of the chosen sweeps, their poses, the state,
        /// back-face culling (dollhouse and floor plan show the inside of the walls).</summary>
        void ApplyView()
        {
            var v = View;
            var t = transform;
            Shader.SetGlobalTexture(IdPanoA, Or(Panoramas.Faces(v.PanoA)));
            Shader.SetGlobalTexture(IdPanoB, Or(Panoramas.Faces(v.PanoB)));
            Shader.SetGlobalTexture(IdDepthA, Or(Panoramas.Depth(v.DepthA)));
            Shader.SetGlobalTexture(IdDepthB, Or(Panoramas.Depth(v.DepthB)));
            Texture cleanA = Panoramas.Clean(v.CleanA), cleanB = Panoramas.Clean(v.CleanB);
            Shader.SetGlobalTexture(IdCleanA, Or(cleanA));
            Shader.SetGlobalTexture(IdCleanB, Or(cleanB));
            Shader.SetGlobalTexture(IdPaintedA, Or(Panoramas.Painted(v.CleanA)));
            Shader.SetGlobalTexture(IdPaintedB, Or(Panoramas.Painted(v.CleanB)));
            Shader.SetGlobalVector(IdClean, new Vector4(cleanA != null ? 1f : 0f, cleanB != null ? 1f : 0f, v.EmptyRooms ? 1f : 0f, 0f));
            Shader.SetGlobalVector(IdPosA, SweepWorldPosition(v.PoseA));
            Shader.SetGlobalVector(IdPosB, SweepWorldPosition(v.PoseB));
            Shader.SetGlobalMatrix(IdRotA, WorldToPano(v.PoseA));
            Shader.SetGlobalMatrix(IdRotB, WorldToPano(v.PoseB));
            Shader.SetGlobalMatrix(IdSpaceRot, Matrix4x4.Rotate(Quaternion.Inverse(t.rotation)));
            Shader.SetGlobalMatrix(IdSpaceToWorld, t.localToWorldMatrix);
            Shader.SetGlobalVector(IdState, new Vector4(v.Blend, v.Projection, v.UseDepth ? 1f : 0f, v.DebugView));
            float clip = v.ClipHeight >= 1e9f ? v.ClipHeight : t.TransformPoint(new Vector3(0, v.ClipHeight, 0)).y; // the shader clips world y
            Shader.SetGlobalVector(IdValid, new Vector4(v.ValidA ? 1f : 0f, v.ValidB ? 1f : 0f, clip, 0f));
            Shader.SetGlobalColor(IdBackground, settings.BackgroundColor.ToUnity());
            _mesh?.ApplyProps(Props, HighlightProp, v.DebugView);
            for (int i = 0; i < 256; i++) _moved[i >> 2][i & 3] = Props != null && i < Props.Props.Count && Props.Moved(i) ? 1f : 0f;
            Shader.SetGlobalVectorArray(IdMoved, _moved);
            if (v.CullBack == _cullBack) return;
            _cullBack = v.CullBack;
            _mesh?.SetCullMode(_cullBack ? CullMode.Back : CullMode.Off);
        }

        Texture Or(Texture t) => t != null ? t : _empty;

        public Vector3 SweepWorldPosition(int i) => transform.TransformPoint(Model.Sweeps[i].Position.ToUnity());

        Matrix4x4 WorldToPano(int i) => Matrix4x4.Rotate(Quaternion.Inverse(transform.rotation * Model.Sweeps[i].WorldFromPano.ToQuaternion()));

        // ------------------------------------------------------------------ edit-mode preview

        /// <summary>Synchronously builds the Scene-view preview (textured mesh + sweep data) from the cache.</summary>
        public void BuildEditorPreview()
        {
            if (Application.isPlaying) return;
            Unload();
            Error = null;
            Status = "";
            if (!previewInEditMode || !MatterportLink.TryParseModelId(link, out var id))
            {
                ModelId = null;
                return;
            }
            ModelId = id;
            SpaceDir = MatterportPaths.SpaceDir(cacheFolder, id);
            if (!SpaceManifest.Exists(SpaceDir))
            {
                Status = "Not downloaded yet.";
                return;
            }
            try
            {
                var manifest = SpaceManifest.Load(SpaceDir);
                Model = SpaceModel.Load(SpaceDir, manifest, SpaceCalibration.LoadOrDefault(SpaceDir), null);
                if (Model.Geometry != null)
                {
                    _mesh = SpaceMesh.Create(Model, transform, "Matterport " + manifest.Name, false, false);
                    _mesh.LoadTexturesNow(manifest, SpaceDir);
                }
                Shader.SetGlobalVector(IdState, Vector4.zero); // plain mesh textures, nothing clipped
                Shader.SetGlobalVector(IdValid, new Vector4(0, 0, 1e9f, 0));
                Status = $"{manifest.Name}: {Model.Sweeps.Count} sweeps" + (Model.Geometry != null ? $", {Model.Geometry.TriangleCount:N0} triangles" : ", no mesh");
            }
            catch (Exception e)
            {
                Error = "Preview failed: " + e.Message;
                Debug.LogWarning("[Matterportable] " + Error);
            }
        }

        void OnDrawGizmos()
        {
            if (Model == null || Application.isPlaying) return;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
            for (int i = 0; i < Model.Sweeps.Count; i++)
            {
                var p = SweepWorldPosition(i);
                Gizmos.DrawSphere(p, 0.08f);
                Gizmos.DrawLine(p, transform.TransformPoint(Model.Sweeps[i].FloorPoint.ToUnity()));
            }
        }
    }
}
