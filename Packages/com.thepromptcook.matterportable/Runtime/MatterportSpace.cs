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
    /// (downloaded first if needed), the mesh is built, panoramas stream in, and a
    /// <see cref="MatterportNavigator"/> on the camera lets you walk it. In Edit mode it shows a textured preview.
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
        public int TexturesLoaded { get; private set; }
        public GameObject MeshObject => _meshObject;
        public IReadOnlyList<Material> MeshMaterials => _materials;
        /// <summary>Floor heights (space-local y of the floor surface) indexed by floor number.</summary>
        public IReadOnlyList<float> FloorHeights => _floorHeights;
        public int FloorCount => _floorHeights.Count;
        public float LastLoadSeconds { get; private set; }

        public event Action<MatterportSpace> Loaded;

        List<SweepInfo> _sweeps = new List<SweepInfo>();
        readonly List<Material> _materials = new List<Material>();
        readonly List<Texture2D> _textures = new List<Texture2D>();
        readonly List<float> _floorHeights = new List<float>();
        GameObject _meshObject;
        Mesh _mesh;
        Material _skybox;
        Material _previousSkybox;
        bool _skyboxApplied;
        Texture2DArray _dummyArray;
        CancellationTokenSource _cts;
        Coroutine _loadRoutine;
        readonly BufferLog _log = new BufferLog { Forward = UnityLog.Instance };

        // View state set by the navigator each frame.
        int _viewA = -1, _viewB = -1;
        float _blend, _projection = 1f, _clipHeight = float.PositiveInfinity;
        int _debugView;

        static readonly int IdPanoA = Shader.PropertyToID("_MP_PanoA");
        static readonly int IdPanoB = Shader.PropertyToID("_MP_PanoB");
        static readonly int IdDepthA = Shader.PropertyToID("_MP_DepthA");
        static readonly int IdDepthB = Shader.PropertyToID("_MP_DepthB");
        static readonly int IdPosA = Shader.PropertyToID("_MP_PosA");
        static readonly int IdPosB = Shader.PropertyToID("_MP_PosB");
        static readonly int IdRotA = Shader.PropertyToID("_MP_RotA");
        static readonly int IdRotB = Shader.PropertyToID("_MP_RotB");
        static readonly int IdSpaceRot = Shader.PropertyToID("_MP_SpaceRot");
        static readonly int IdFaceRight = Shader.PropertyToID("_MP_FaceRight");
        static readonly int IdFaceUp = Shader.PropertyToID("_MP_FaceUp");
        static readonly int IdState = Shader.PropertyToID("_MP_State");
        static readonly int IdValid = Shader.PropertyToID("_MP_Valid");
        static readonly int IdBackground = Shader.PropertyToID("_MP_Background");
        static readonly int IdCull = Shader.PropertyToID("_Cull");
        static readonly int IdMainTex = Shader.PropertyToID("_MainTex");

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
                if (id != ModelId || (previewInEditMode != (_meshObject != null))) BuildEditorPreview();
            };
#endif
        }

        void Update()
        {
            if (!Application.isPlaying || !IsLoaded) return;
            Panoramas?.Update();
            ApplyShaderState();
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
            if (_meshObject != null) MpUtil.SafeDestroy(_meshObject);
            _meshObject = null;
            if (_mesh != null) MpUtil.SafeDestroy(_mesh);
            _mesh = null;
            foreach (var m in _materials) MpUtil.SafeDestroy(m);
            _materials.Clear();
            foreach (var t in _textures) MpUtil.SafeDestroy(t);
            _textures.Clear();
            if (_skyboxApplied)
            {
                RenderSettings.skybox = _previousSkybox;
                _skyboxApplied = false;
            }
            if (_skybox != null) MpUtil.SafeDestroy(_skybox);
            _skybox = null;
            if (_dummyArray != null) MpUtil.SafeDestroy(_dummyArray);
            _dummyArray = null;
            _sweeps = new List<SweepInfo>();
            _floorHeights.Clear();
            Geometry = null;
            TexturesLoaded = 0;
        }

        // ------------------------------------------------------------------ loading (Play mode)

        IEnumerator LoadRoutine(CancellationToken ct)
        {
            IsLoading = true;
            Error = null;
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
                    Fail($"Space {modelId} is not downloaded ({SpaceDir}). Use Window > Matterportable > Import.");
                    yield break;
                }
                var http = new HttpClientTransport();
                var downloader = new SpaceDownloader(http, _log);
                var task = downloader.DownloadAsync(modelId, SpaceDir, new DownloadOptions { PanoSizes = new[] { 512, Settings.panoMaxSize } }, ct);
                while (!task.IsCompleted)
                {
                    SetStatus("Downloading: " + downloader.Status, 0.05f + 0.5f * downloader.Status.Fraction);
                    yield return null;
                }
                http.Dispose();
                if (task.IsFaulted) { Fail("Download failed: " + Unwrap(task.Exception)); yield break; }
                if (task.IsCanceled) yield break;
            }

            // 2) Manifest and calibration.
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

            if (!Calibration.IsSolved && Manifest.Mesh != null && Calibration.Method != "manual")
            {
                SetStatus("Aligning panoramas (first load only)", 0.55f);
                yield return null;
                Dictionary<string, LumaImage> images = null;
                try { images = CalibrationRunner.DecodeInputs(SpaceDir, Manifest); }
                catch (Exception e) { _log.Warn("Could not prepare panorama alignment: " + e.Message); }
                if (images != null)
                {
                    var solve = CalibrationRunner.SolveAsync(SpaceDir, Manifest, images, _log, ct);
                    while (!solve.IsCompleted) yield return null;
                    if (solve.IsFaulted) _log.Warn("Panorama alignment failed: " + Unwrap(solve.Exception));
                    else if (solve.IsCompleted && !solve.IsCanceled) Calibration = solve.Result;
                }
            }

            // 3) Geometry on a worker thread.
            SetStatus("Building geometry", 0.65f);
            var manifest = Manifest;
            var cal = Calibration;
            var dir = SpaceDir;
            var geoTask = Task.Run(() =>
            {
                SpaceGeometry geo = null;
                if (manifest.Mesh != null)
                {
                    var bytes = File.ReadAllBytes(CacheLayout.Resolve(dir, manifest.Mesh.File));
                    var mesh = DamDecoder.Decode(bytes, _log);
                    geo = SpaceGeometry.Build(mesh, cal);
                }
                var sweeps = SweepBuilder.Build(manifest, cal, geo, _log);
                return (geo, sweeps);
            }, ct);
            while (!geoTask.IsCompleted) yield return null;
            if (geoTask.IsFaulted) { Fail("Could not build the mesh: " + Unwrap(geoTask.Exception)); yield break; }
            if (geoTask.IsCanceled) yield break;
            Geometry = geoTask.Result.geo;
            _sweeps = geoTask.Result.sweeps;
            ComputeFloorHeights();

            // 4) Unity objects.
            SetStatus("Creating meshes", 0.75f);
            if (Geometry != null) BuildMeshObject(Geometry, true);
            SetupRendering();
            Panoramas = new PanoramaCache(this) { IsPinned = s => s == _viewA || s == _viewB };
            startSweep = Mathf.Clamp(startSweep, 0, _sweeps.Count - 1);
            Panoramas.RequestProgressive(startSweep, Settings.panoMaxSize, 0f);
            Panoramas.RequestDepth(startSweep);

            IsLoading = false;
            IsLoaded = true;
            LastLoadSeconds = Time.realtimeSinceStartup - started;
            SetStatus($"Loaded \"{Manifest.Name}\" ({_sweeps.Count} sweeps) in {LastLoadSeconds:0.0}s", 1f);
            _log.Info(Status);
            foreach (var w in Manifest.Warnings) _log.Warn(w);
            Loaded?.Invoke(this);

            // 5) Mesh textures stream in afterwards (they matter for dollhouse view and occluded areas).
            yield return LoadTexturesRoutine(Settings.compressMeshTextures);
        }

        IEnumerator LoadTexturesRoutine(bool compress)
        {
            var byMaterial = MaterialIndexByName();
            foreach (var entry in Manifest.Textures)
            {
                if (!byMaterial.TryGetValue(entry.Material ?? "", out int index)) continue;
                var path = CacheLayout.Resolve(SpaceDir, entry.File);
                var read = Task.Run(() => File.Exists(path) ? File.ReadAllBytes(path) : null);
                while (!read.IsCompleted) yield return null;
                if (read.IsFaulted || read.Result == null) continue;
                var tex = DecodeTexture(read.Result, compress, entry.Material);
                if (tex == null) continue;
                _textures.Add(tex);
                if (index < _materials.Count) _materials[index].SetTexture(IdMainTex, tex);
                TexturesLoaded++;
                yield return null; // spread decoding over frames
            }
        }

        static Texture2D DecodeTexture(byte[] bytes, bool compress, string name)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = name, hideFlags = HideFlags.DontSave };
            if (!tex.LoadImage(bytes, false))
            {
                MpUtil.SafeDestroy(tex);
                return null;
            }
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            if (compress && SystemInfo.SupportsTextureFormat(TextureFormat.DXT1)) tex.Compress(false);
            tex.Apply(false, true);
            return tex;
        }

        Dictionary<string, int> MaterialIndexByName()
        {
            var map = new Dictionary<string, int>();
            if (Geometry == null) return map;
            for (int i = 0; i < Geometry.Parts.Count; i++) map[Geometry.Parts[i].Material ?? ""] = i;
            return map;
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

        void BuildMeshObject(SpaceGeometry geo, bool runtime)
        {
            var shader = Shader.Find("Matterportable/Mesh");
            if (shader == null)
            {
                Fail("Shader Matterportable/Mesh not found (is the package installed correctly?).");
                return;
            }
            _mesh = new Mesh
            {
                name = "Matterport " + (Manifest?.Name ?? ModelId),
                indexFormat = geo.Positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                hideFlags = HideFlags.DontSave,
            };
            var verts = new Vector3[geo.Positions.Length];
            for (int i = 0; i < verts.Length; i++) verts[i] = geo.Positions[i].ToUnity();
            _mesh.vertices = verts;
            if (geo.HasUvs)
            {
                var uvs = new Vector2[geo.Uvs.Length];
                for (int i = 0; i < uvs.Length; i++) uvs[i] = new Vector2(geo.Uvs[i].X, geo.Uvs[i].Y);
                _mesh.uv = uvs;
            }
            _mesh.subMeshCount = geo.Parts.Count;
            for (int i = 0; i < geo.Parts.Count; i++) _mesh.SetTriangles(geo.Parts[i].Indices, i, false);
            _mesh.RecalculateBounds();

            _meshObject = new GameObject("Matterport Mesh");
            _meshObject.hideFlags = runtime ? HideFlags.DontSave : HideFlags.HideAndDontSave;
            _meshObject.transform.SetParent(transform, false);
            _meshObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = _meshObject.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var mats = new Material[geo.Parts.Count];
            for (int i = 0; i < mats.Length; i++)
            {
                mats[i] = new Material(shader) { name = geo.Parts[i].Material, hideFlags = HideFlags.DontSave };
                mats[i].SetTexture(IdMainTex, Texture2D.grayTexture);
                mats[i].SetFloat(IdCull, (float)CullMode.Off);
                _materials.Add(mats[i]);
            }
            mr.sharedMaterials = mats;
            if (runtime && Settings.addMeshCollider) _meshObject.AddComponent<MeshCollider>().sharedMesh = _mesh;
        }

        void SetupRendering()
        {
            _dummyArray = new Texture2DArray(1, 1, 6, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = "Matterport empty pano" };
            var px = new Color32[] { new Color32(128, 128, 128, 255) };
            for (int i = 0; i < 6; i++) _dummyArray.SetPixels32(px, i);
            _dummyArray.Apply(false, true);
            ApplyLayout(Calibration);

            var skyShader = Shader.Find("Matterportable/Skybox");
            if (skyShader != null && Application.isPlaying)
            {
                _skybox = new Material(skyShader) { hideFlags = HideFlags.DontSave, name = "Matterport Skybox" };
                _previousSkybox = RenderSettings.skybox;
                RenderSettings.skybox = _skybox;
                _skyboxApplied = true;
            }
        }

        /// <summary>Pushes a panorama face layout to the shaders (used after (re)calibration and by the inspector).</summary>
        public void ApplyLayout(SpaceCalibration cal)
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
            Shader.SetGlobalVectorArray(IdFaceRight, r);
            Shader.SetGlobalVectorArray(IdFaceUp, u);
        }

        /// <summary>Replaces the calibration at runtime (inspector tweaks), recomputing sweep rotations.</summary>
        public void SetCalibration(SpaceCalibration cal, bool save)
        {
            Calibration = cal;
            foreach (var s in _sweeps) s.WorldFromPano = cal.WorldFromPano(s.Entry.Rotation);
            ApplyLayout(cal);
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
            _viewA = sweepA;
            _viewB = sweepB;
            _blend = Mathf.Clamp01(blend);
            _projection = Mathf.Clamp01(projection);
            _clipHeight = clipHeightWorld;
            if (Panoramas != null)
            {
                Panoramas.Touch(sweepA);
                Panoramas.Touch(sweepB);
            }
        }

        public void SetCullMode(CullMode mode)
        {
            foreach (var m in _materials) m.SetFloat(IdCull, (float)mode);
        }

        public int DebugView
        {
            get => _debugView;
            set => _debugView = ((value % 5) + 5) % 5;
        }

        void ApplyShaderState()
        {
            var a = _viewA >= 0 && Panoramas != null ? Panoramas.Get(_viewA) : null;
            var b = _viewB >= 0 && Panoramas != null ? Panoramas.Get(_viewB) : null;
            bool validA = a?.Faces != null, validB = b?.Faces != null;
            bool depth = Settings.depthMapSize > 0 && a?.Depth != null && (b == null || b.Depth != null || _blend <= 0f);

            Texture texA = validA ? a.Faces : (validB ? (Texture)b.Faces : _dummyArray);
            Texture texB = validB ? b.Faces : texA;
            Shader.SetGlobalTexture(IdPanoA, texA);
            Shader.SetGlobalTexture(IdPanoB, texB);
            Shader.SetGlobalTexture(IdDepthA, a?.Depth != null ? (Texture)a.Depth : _dummyArray);
            Shader.SetGlobalTexture(IdDepthB, b?.Depth != null ? (Texture)b.Depth : (a?.Depth != null ? (Texture)a.Depth : _dummyArray));

            int ia = validA ? _viewA : (validB ? _viewB : Mathf.Max(0, _viewA));
            int ib = validB ? _viewB : ia;
            if (ia >= 0 && ia < _sweeps.Count)
            {
                Shader.SetGlobalVector(IdPosA, SweepWorldPosition(ia));
                Shader.SetGlobalMatrix(IdRotA, WorldToPano(ia));
            }
            if (ib >= 0 && ib < _sweeps.Count)
            {
                Shader.SetGlobalVector(IdPosB, SweepWorldPosition(ib));
                Shader.SetGlobalMatrix(IdRotB, WorldToPano(ib));
            }
            Shader.SetGlobalMatrix(IdSpaceRot, Matrix4x4.Rotate(Quaternion.Inverse(transform.rotation)));
            Shader.SetGlobalVector(IdState, new Vector4(validB && validA ? _blend : (validB ? 1f : 0f), _projection, depth ? 1f : 0f, _debugView));
            Shader.SetGlobalVector(IdValid, new Vector4(validA || validB ? 1f : 0f, validA && validB ? 1f : 0f, _clipHeight, 0f));
            Shader.SetGlobalColor(IdBackground, Settings.backgroundColor);
        }

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
                var b = new Bounds();
                var mn = Geometry.BoundsMin.ToUnity();
                var mx = Geometry.BoundsMax.ToUnity();
                for (int i = 0; i < 8; i++)
                {
                    var corner = transform.TransformPoint(new Vector3((i & 1) == 0 ? mn.x : mx.x, (i & 2) == 0 ? mn.y : mx.y, (i & 4) == 0 ? mn.z : mx.z));
                    if (i == 0) b = new Bounds(corner, Vector3.zero);
                    else b.Encapsulate(corner);
                }
                return b;
            }
        }

        public string LogText => _log.Text();

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
                if (Manifest.Mesh != null)
                {
                    var mesh = DamDecoder.Decode(File.ReadAllBytes(CacheLayout.Resolve(SpaceDir, Manifest.Mesh.File)));
                    Geometry = SpaceGeometry.Build(mesh, Calibration);
                }
                _sweeps = SweepBuilder.Build(Manifest, Calibration, Geometry);
                ComputeFloorHeights();
                if (Geometry != null)
                {
                    BuildMeshObject(Geometry, false);
                    var byMaterial = MaterialIndexByName();
                    foreach (var t in Manifest.Textures)
                    {
                        var path = CacheLayout.Resolve(SpaceDir, t.File);
                        if (!byMaterial.TryGetValue(t.Material ?? "", out int index) || !File.Exists(path)) continue;
                        var tex = DecodeTexture(File.ReadAllBytes(path), false, t.Material);
                        if (tex == null) continue;
                        _textures.Add(tex);
                        _materials[index].SetTexture(IdMainTex, tex);
                    }
                }
                // Show plain mesh textures in the Scene view.
                Shader.SetGlobalVector(IdState, new Vector4(0, 0, 0, 0));
                Shader.SetGlobalVector(IdValid, new Vector4(0, 0, float.PositiveInfinity, 0));
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
            if (_sweeps == null || _sweeps.Count == 0 || Application.isPlaying) return;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
            for (int i = 0; i < _sweeps.Count; i++)
            {
                var p = SweepWorldPosition(i);
                var f = MarkerWorldPosition(i);
                Gizmos.DrawSphere(p, 0.08f);
                Gizmos.DrawLine(p, f);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Nothing static to reset besides shared shader globals; keeps "Enter Play Mode without domain reload" safe.
            Shader.SetGlobalVector(IdState, new Vector4(0, 1, 0, 0));
        }
    }
}
