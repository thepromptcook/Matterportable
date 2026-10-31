using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Matterportable.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Matterportable
{
    /// <summary>
    /// A Matterport space in the scene. Paste a link, press Play: the space is loaded from the cache folder
    /// (downloaded first if needed), the mesh is built, panoramas stream in, and a <see cref="MatterportNavigator"/>
    /// on the camera lets you walk it. In Edit mode it shows a textured preview with sweep gizmos.
    /// Collaborators: <see cref="SpaceMesh"/> (Unity mesh), <see cref="SpaceRenderer"/> (shader state),
    /// <see cref="PanoramaCache"/> (panorama streaming), <see cref="CalibrationRunner"/> (alignment).
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
        [Tooltip("Viewer settings asset. Leave empty for defaults; edits apply live in Play mode.")]
        public MatterportViewerSettings settings;
        [Tooltip("Sweep (panorama) index to start at.")]
        public int startSweep;
        [Tooltip("Download the space automatically when entering Play mode if it is not cached yet.")]
        public bool downloadIfMissing = true;
        [Tooltip("Show the textured mesh in the Scene view while editing.")]
        public bool previewInEditMode = true;

        public MatterportViewerSettings Settings => settings != null ? settings : MatterportViewerSettings.Defaults;
        public string ModelId { get; private set; }
        public string SpaceDir { get; private set; }
        public SpaceManifest Manifest { get; private set; }
        public SpaceCalibration Calibration { get; private set; }
        public SpaceGeometry Geometry { get; private set; }
        public IReadOnlyList<SweepInfo> Sweeps => _sweeps;
        public PanoramaCache Panoramas { get; private set; }
        public bool IsLoaded { get; private set; }
        public bool IsLoading { get; private set; }
        public string Status { get; private set; } = "";
        public float Progress { get; private set; }
        public string Error { get; private set; }
        public int TexturesLoaded => _mesh?.TexturesLoaded ?? 0;
        public GameObject MeshObject => _mesh?.Object;
        public int FloorCount => _floorHeights.Count;
        public float LastLoadSeconds { get; private set; }
        /// <summary>Everything logged during the last load (also saved to debug/load.log).</summary>
        public string LoadLog => _log.Text();

        public event Action<MatterportSpace> Loaded;

        List<SweepInfo> _sweeps = new List<SweepInfo>();
        readonly List<float> _floorHeights = new List<float>();
        SpaceMesh _mesh;
        SpaceRenderer _renderer;
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
            if (Application.isPlaying) return;
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
            if (Application.isPlaying && IsLoaded) Panoramas?.Update();
        }

        // After the navigator's Update, so the shaders always match this frame's camera.
        void LateUpdate()
        {
            if (Application.isPlaying && IsLoaded) _renderer?.Apply(this);
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
            IsLoading = false;
            Panoramas?.Dispose();
            Panoramas = null;
            _mesh?.Dispose();
            _mesh = null;
            _renderer?.Dispose();
            _renderer = null;
            _sweeps = new List<SweepInfo>();
            _floorHeights.Clear();
            Geometry = null;
        }

        // ------------------------------------------------------------------ loading (Play mode)

        IEnumerator LoadRoutine(CancellationToken ct)
        {
            IsLoading = true;
            Error = null;
            _log = new BufferLog { Forward = UnityLog.For(Settings) };
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
                    Fail($"Space {modelId} is not downloaded ({SpaceDir}). Use Window > Matterportable > Import Space.");
                    yield break;
                }
                using (var http = new HttpClientTransport())
                {
                    var downloader = new SpaceDownloader(http, _log);
                    var task = downloader.DownloadAsync(modelId, SpaceDir, new DownloadOptions { PanoSizes = new[] { 512, Settings.panoMaxSize } }, ct);
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
            try
            {
                Manifest = SpaceManifest.Load(SpaceDir);
                Calibration = SpaceCalibration.LoadOrDefault(SpaceDir);
            }
            catch (Exception e)
            {
                Fail("Could not read the cached space: " + e.Message);
                yield break;
            }
            if (Manifest.Sweeps.Count == 0) { Fail("The cached space has no panoramas."); yield break; }

            if (Calibration.NeedsSolve && Manifest.Mesh != null)
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
                    else if (!solve.IsCanceled) Calibration = solve.Result;
                }
            }

            // 3) Geometry on a worker thread (reused from the previous Play session when nothing changed).
            SetStatus("Building geometry", 0.65f);
            var manifest = Manifest;
            var cal = Calibration;
            var dir = SpaceDir;
            var log = _log;
            var geoTask = Task.Run(() => LoadGeometry(dir, manifest, cal, log), ct);
            while (!geoTask.IsCompleted) yield return null;
            if (geoTask.IsCanceled) yield break;
            if (geoTask.IsFaulted) { Fail("Could not build the mesh: " + Unwrap(geoTask.Exception)); yield break; }
            (Geometry, _sweeps) = geoTask.Result;
            ComputeFloorHeights();

            // 4) Unity objects.
            SetStatus("Creating meshes", 0.75f);
            try
            {
                if (Geometry != null) _mesh = SpaceMesh.Create(Geometry, transform, "Matterport " + Manifest.Name, true, Settings.addMeshCollider);
            }
            catch (Exception e) { Fail(e.Message); yield break; }
            _renderer = new SpaceRenderer(installSkybox: true);
            SpaceRenderer.SetLayout(Calibration, _log);
            Panoramas = new PanoramaCache(this) { IsPinned = s => s == _renderer.ViewA || s == _renderer.ViewB };
            startSweep = Mathf.Clamp(startSweep, 0, _sweeps.Count - 1);
            Panoramas.RequestProgressive(startSweep, Settings.panoMaxSize, 0f);
            Panoramas.RequestDepth(startSweep);

            IsLoading = false;
            IsLoaded = true;
            LastLoadSeconds = Time.realtimeSinceStartup - started;
            SetStatus($"Loaded \"{Manifest.Name}\" ({_sweeps.Count} sweeps) in {LastLoadSeconds:0.0}s", 1f);
            _log.Info(Status);
            foreach (var w in Manifest.Warnings) _log.Info("Download note: " + w);
            WriteLoadLog();
            Loaded?.Invoke(this);

            // 5) Mesh textures stream in afterwards (they matter for dollhouse view and occluded areas).
            if (_mesh != null) yield return _mesh.LoadTexturesRoutine(Manifest, SpaceDir);
        }

        static readonly object GeometryCacheLock = new object();
        static (string key, SpaceGeometry geometry, List<SweepInfo> sweeps) _geometryCache;

        /// <summary>
        /// Decodes the mesh, builds the BVH and sweeps. The last result stays in memory: with fast Enter Play Mode
        /// (no domain reload) the next Play skips this step entirely unless the mesh or calibration changed.
        /// </summary>
        static (SpaceGeometry, List<SweepInfo>) LoadGeometry(string dir, SpaceManifest manifest, SpaceCalibration cal, ILogSink log)
        {
            var meshPath = manifest.Mesh != null ? CacheLayout.Resolve(dir, manifest.Mesh.File) : null;
            var key = string.Join("|", dir, meshPath != null ? File.GetLastWriteTimeUtc(meshPath).Ticks : 0, manifest.Sweeps.Count, cal.ToJson().ToJson(false));
            lock (GeometryCacheLock)
                if (_geometryCache.key == key) return (_geometryCache.geometry, _geometryCache.sweeps);

            SpaceGeometry geo = null;
            if (meshPath != null)
            {
                var bytes = File.ReadAllBytes(meshPath);
                var mesh = DamDecoder.Decode(bytes, log);
                if (!mesh.HasUvs) DamDecoder.WriteDiagnostics(dir, bytes);
                geo = SpaceGeometry.Build(mesh, cal);
            }
            var sweeps = SweepBuilder.Build(manifest, cal, geo, log);
            lock (GeometryCacheLock) _geometryCache = (key, geo, sweeps);
            return (geo, sweeps);
        }

        void ComputeFloorHeights()
        {
            _floorHeights.Clear();
            int floors = 0;
            foreach (var s in _sweeps) floors = Mathf.Max(floors, s.Floor + 1);
            for (int f = 0; f < floors; f++)
            {
                var ys = new List<float>();
                foreach (var s in _sweeps) if (s.Floor == f) ys.Add(s.FloorPoint.Y);
                ys.Sort();
                _floorHeights.Add(ys.Count > 0 ? ys[ys.Count / 2] : (f > 0 ? _floorHeights[f - 1] + 3f : 0f));
            }
        }

        /// <summary>Replaces the calibration at runtime (inspector tweaks), recomputing sweep rotations.</summary>
        public void SetCalibration(SpaceCalibration cal, bool save)
        {
            Calibration = cal;
            foreach (var s in _sweeps) s.WorldFromPano = cal.WorldFromPano(s.Entry.Rotation);
            SpaceRenderer.SetLayout(cal, _log);
            if (save && !string.IsNullOrEmpty(SpaceDir)) cal.Save(SpaceDir);
        }

        void SetStatus(string s, float progress)
        {
            Status = s;
            Progress = progress;
        }

        void Fail(string message)
        {
            Error = message;
            IsLoading = false;
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

        // ------------------------------------------------------------------ view state (called by the navigator)

        /// <summary>
        /// Sets what the shaders show: panorama A, optionally blending to B, how much projection vs. mesh texture,
        /// and a world-space clip height (floors above are hidden in dollhouse/floor plan).
        /// </summary>
        public void SetView(int sweepA, int sweepB, float blend, float projection, float clipHeightWorld = float.PositiveInfinity)
        {
            if (_renderer == null) return;
            _renderer.ViewA = sweepA;
            _renderer.ViewB = sweepB;
            _renderer.Blend = Mathf.Clamp01(blend);
            _renderer.Projection = Mathf.Clamp01(projection);
            _renderer.ClipHeight = clipHeightWorld;
            Panoramas?.Touch(sweepA);
            Panoramas?.Touch(sweepB);
        }

        public void SetCullMode(CullMode mode) => _mesh?.SetCullMode(mode);

        /// <summary>Render debug view (0 final … see <see cref="DebugViewName"/>).</summary>
        public int DebugView
        {
            get => _renderer?.DebugView ?? 0;
            set { if (_renderer != null) _renderer.DebugView = value; }
        }

        public string DebugViewName => SpaceRenderer.DebugViewNames[DebugView];

        /// <summary>Names of the render debug views, in <see cref="DebugView"/> order.</summary>
        public static string[] DebugViewNames => (string[])SpaceRenderer.DebugViewNames.Clone();

        // ------------------------------------------------------------------ queries

        public Vector3 SweepWorldPosition(int i) => transform.TransformPoint(_sweeps[i].Position.ToUnity());
        public Vector3 MarkerWorldPosition(int i) => transform.TransformPoint(_sweeps[i].FloorPoint.ToUnity());

        /// <summary>Panorama-local → world rotation of a sweep.</summary>
        public Quaternion SweepWorldRotation(int i) => transform.rotation * _sweeps[i].WorldFromPano.ToQuaternion();

        public Matrix4x4 WorldToPano(int i) => Matrix4x4.Rotate(Quaternion.Inverse(SweepWorldRotation(i)));

        public float FloorHeightWorld(int floor) =>
            floor >= 0 && floor < _floorHeights.Count ? transform.TransformPoint(new Vector3(0, _floorHeights[floor], 0)).y : 0f;

        /// <summary>Raycasts the scanned mesh (no physics needed). Returns world-space point and normal.</summary>
        public bool Raycast(Ray worldRay, float maxDistance, out Vector3 point, out Vector3 normal, out float distance)
        {
            point = normal = Vector3.zero;
            distance = 0;
            if (Geometry == null) return false;
            var o = transform.InverseTransformPoint(worldRay.origin).ToCore();
            var d = transform.InverseTransformDirection(worldRay.direction).normalized.ToCore();
            if (!Geometry.Bvh.Raycast(o, d, maxDistance, out var hit)) return false;
            point = transform.TransformPoint(hit.Point.ToUnity());
            normal = transform.TransformDirection(hit.Normal.ToUnity());
            distance = hit.Distance;
            return true;
        }

        /// <summary>Raycast that ignores everything above a world-space clip height (dollhouse floor filtering).</summary>
        public bool RaycastBelow(Ray worldRay, float maxDistance, float clipHeightWorld, out Vector3 point, out Vector3 normal)
        {
            point = normal = Vector3.zero;
            var ray = worldRay;
            float travelled = 0;
            for (int i = 0; i < 8; i++)
            {
                if (!Raycast(ray, maxDistance - travelled, out point, out normal, out float dist)) return false;
                if (point.y <= clipHeightWorld) return true;
                travelled += dist + 0.01f;
                ray = new Ray(point + ray.direction * 0.01f, ray.direction);
            }
            return false;
        }

        public bool LineOfSight(Vector3 fromWorld, Vector3 toWorld)
        {
            if (Geometry == null) return true;
            return !Geometry.Bvh.Occluded(transform.InverseTransformPoint(fromWorld).ToCore(), transform.InverseTransformPoint(toWorld).ToCore(), 0.1f);
        }

        public Bounds WorldBounds
        {
            get
            {
                if (Geometry == null)
                {
                    var bb = new Bounds(transform.position, Vector3.one);
                    for (int i = 0; i < _sweeps.Count; i++) bb.Encapsulate(SweepWorldPosition(i));
                    return bb;
                }
                var mn = Geometry.BoundsMin.ToUnity();
                var mx = Geometry.BoundsMax.ToUnity();
                var b = new Bounds(transform.TransformPoint(mn), Vector3.zero);
                for (int i = 1; i < 8; i++)
                    b.Encapsulate(transform.TransformPoint(new Vector3((i & 1) == 0 ? mn.x : mx.x, (i & 2) == 0 ? mn.y : mx.y, (i & 4) == 0 ? mn.z : mx.z)));
                return b;
            }
        }

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
                Manifest = SpaceManifest.Load(SpaceDir);
                Calibration = SpaceCalibration.LoadOrDefault(SpaceDir);
                (Geometry, _sweeps) = LoadGeometry(SpaceDir, Manifest, Calibration, null);
                ComputeFloorHeights();
                if (Geometry != null)
                {
                    _mesh = SpaceMesh.Create(Geometry, transform, "Matterport " + Manifest.Name, false, false);
                    _mesh.LoadTexturesNow(Manifest, SpaceDir);
                }
                SpaceRenderer.ShowMeshTexturesOnly();
                Status = $"{Manifest.Name}: {_sweeps.Count} sweeps" + (Geometry != null ? $", {Geometry.TriangleCount:N0} triangles" : ", no mesh");
            }
            catch (Exception e)
            {
                Error = "Preview failed: " + e.Message;
                Debug.LogWarning("[Matterportable] " + Error);
            }
        }

        void OnDrawGizmos()
        {
            if (_sweeps.Count == 0 || Application.isPlaying) return;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
            for (int i = 0; i < _sweeps.Count; i++)
            {
                var p = SweepWorldPosition(i);
                Gizmos.DrawSphere(p, 0.08f);
                Gizmos.DrawLine(p, MarkerWorldPosition(i));
            }
        }
    }
}
