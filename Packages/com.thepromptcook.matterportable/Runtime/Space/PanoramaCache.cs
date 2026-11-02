using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Matterportable.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace Matterportable
{
    /// <summary>
    /// Loads what the Core <see cref="PanoramaScheduler"/> asks for (mirrors the Godot PanoramaCache): six faces per
    /// sweep into a mipmapped texture array (decoded off the main thread by <see cref="GpuTextures"/>, assembled and
    /// mipmapped on the GPU), the occlusion depth cube (<see cref="CubeFaces.DepthCube"/> on a worker, uploaded one
    /// per frame) and the clean photos (furniture painted out) like the photos, with the map of which prop each pixel had
    /// painted out (decoded on a worker).
    /// </summary>
    public sealed class PanoramaCache : IPanoramaLoader, IDisposable
    {
        static readonly int IdRect = Shader.PropertyToID("_Rect");

        readonly MatterportSpace _space;
        readonly Dictionary<int, RenderTexture> _faces = new Dictionary<int, RenderTexture>(), _clean = new Dictionary<int, RenderTexture>();
        readonly Dictionary<int, Texture2DArray> _depth = new Dictionary<int, Texture2DArray>(), _painted = new Dictionary<int, Texture2DArray>();
        readonly ConcurrentQueue<(int sweep, ushort[] depth, int size)> _depthResults = new ConcurrentQueue<(int, ushort[], int)>();
        readonly Material _blit;
        bool _disposed;

        public PanoramaScheduler Scheduler;

        public PanoramaCache(MatterportSpace space)
        {
            _space = space;
            var shader = Shader.Find("Hidden/Matterportable/FaceBlit");
            if (shader == null) throw new InvalidOperationException("Shader Hidden/Matterportable/FaceBlit not found.");
            _blit = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public Texture Faces(int sweep) => sweep >= 0 && _faces.TryGetValue(sweep, out var t) ? t : null;
        public Texture Depth(int sweep) => sweep >= 0 && _depth.TryGetValue(sweep, out var t) ? t : null;
        public Texture Clean(int sweep) => sweep >= 0 && _clean.TryGetValue(sweep, out var t) ? t : null;
        /// <summary>Which prop each clean pixel had painted out (prop + 1).</summary>
        public Texture Painted(int sweep) => sweep >= 0 && _painted.TryGetValue(sweep, out var t) ? t : null;

        public void Load(int sweep, PanoTier tier) => _space.StartCoroutine(LoadRoutine(sweep, tier));

        public void LoadClean(int sweep, IReadOnlyList<string> faces, IReadOnlyList<string> painted) =>
            _space.StartCoroutine(LoadCleanRoutine(sweep, faces, painted));

        public void ComputeDepth(int sweep)
        {
            var bvh = _space.Model.Geometry.Bvh;
            var pieces = _space.Model.PieceOfTriangle;
            var origin = _space.Model.Sweeps[sweep].Position;
            int size = _space.settings.DepthMapSize;
            Task.Run(() =>
            {
                try { _depthResults.Enqueue((sweep, CubeFaces.DepthCube(bvh, origin, size, pieces), size)); }
                catch (Exception e) { Debug.LogException(e); }
            });
        }

        public void Unload(int sweep)
        {
            if (_faces.TryGetValue(sweep, out var faces)) MpUtil.Release(ref faces);
            if (_depth.TryGetValue(sweep, out var depth)) MpUtil.SafeDestroy(depth);
            if (_clean.TryGetValue(sweep, out var clean)) MpUtil.Release(ref clean);
            if (_painted.TryGetValue(sweep, out var painted)) MpUtil.SafeDestroy(painted);
            _faces.Remove(sweep);
            _depth.Remove(sweep);
            _clean.Remove(sweep);
            _painted.Remove(sweep);
        }

        /// <summary>Uploads one finished depth cube per frame.</summary>
        public void Update()
        {
            if (_disposed || !_depthResults.TryDequeue(out var r)) return;
            var tex = new Texture2DArray(r.size, r.size, 6, TextureFormat.RGHalf, false, true) // distance, piece seen
            {
                name = "Matterport depth " + r.sweep,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point, // the shader filters the visibility test, not the depths (MP_DepthVisibility)
                hideFlags = HideFlags.HideAndDontSave,
            };
            int n = r.size * r.size;
            for (int f = 0; f < 6; f++) tex.SetPixelData(r.depth, 0, f, f * n * 2);
            tex.Apply(false, true);
            if (_depth.TryGetValue(r.sweep, out var old)) MpUtil.SafeDestroy(old);
            _depth[r.sweep] = tex;
            Scheduler.DepthReady(r.sweep);
        }

        IEnumerator LoadRoutine(int sweep, PanoTier tier)
        {
            int tiles = Mathf.Max(1, tier.Tiles);
            var requests = new List<(UnityWebRequest req, int face, int x, int y)>();
            for (int face = 0; face < 6; face++)
            for (int y = 0; y < tiles; y++)
            for (int x = 0; x < tiles; x++)
                requests.Add((GpuTextures.Load(CacheLayout.Resolve(_space.SpaceDir, tiles == 1 ? tier.FacePath(face) : tier.TilePath(face, x, y))), face, x, y));
            try
            {
                foreach (var r in requests)
                    while (!r.req.isDone) yield return null;
                if (!_disposed) Assemble(sweep, tier, tiles, requests);
            }
            finally
            {
                foreach (var r in requests) r.req.Dispose();
            }
        }

        /// <summary>Copies the decoded faces (or tiles) into one mipmapped array and hands it to the scheduler.</summary>
        void Assemble(int sweep, PanoTier tier, int tiles, List<(UnityWebRequest req, int face, int x, int y)> requests)
        {
            RenderTexture rt = null;
            try
            {
                rt = Blit(requests, tiles, $"Matterport pano {sweep}");
                if (!Scheduler.Loaded(sweep, rt.width)) return; // a larger tier is already there
                if (_faces.TryGetValue(sweep, out var old)) MpUtil.Release(ref old);
                _faces[sweep] = rt;
                rt = null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Matterportable] Could not load panorama {tier.Directory}: {e.Message}");
                Scheduler.Failed(sweep);
            }
            finally
            {
                MpUtil.Release(ref rt);
            }
        }

        /// <summary>The decoded faces (or tiles) in one mipmapped array.</summary>
        RenderTexture Blit(List<(UnityWebRequest req, int face, int x, int y)> requests, int tiles, string name)
        {
            RenderTexture rt = null;
            try
            {
                float inv = 1f / tiles;
                foreach (var r in requests)
                {
                    if (!GpuTextures.Succeeded(r.req)) throw new Exception($"face {r.face}: {r.req.error}");
                    var tex = GpuTextures.Content(r.req);
                    if (rt == null) rt = GpuTextures.Create(tex.width * tiles, tex.width * tiles, 6, TextureWrapMode.Clamp, name);
                    _blit.SetVector(IdRect, new Vector4(r.x * inv, 1f - (r.y + 1) * inv, inv, inv)); // tile rows count from the top
                    Graphics.Blit(tex, rt, _blit, 0, r.face);
                    MpUtil.SafeDestroy(tex);
                }
                rt.GenerateMips();
                return rt;
            }
            catch
            {
                MpUtil.Release(ref rt);
                throw;
            }
        }

        IEnumerator LoadCleanRoutine(int sweep, IReadOnlyList<string> faces, IReadOnlyList<string> painted)
        {
            var requests = new List<(UnityWebRequest req, int face, int x, int y)>();
            for (int face = 0; face < 6; face++) requests.Add((GpuTextures.Load(CacheLayout.Resolve(_space.SpaceDir, faces[face])), face, 0, 0));
            var dir = _space.SpaceDir;
            var maps = Task.Run(() => (CleanPanoramas.LoadPainted(dir, painted, out int size), size));
            RenderTexture rt = null;
            Texture2DArray ids = null;
            try
            {
                foreach (var r in requests)
                    while (!r.req.isDone) yield return null;
                while (!maps.IsCompleted) yield return null;
                if (_disposed) yield break;
                try
                {
                    rt = Blit(requests, 1, $"Matterport clean pano {sweep}");
                    var (data, size) = maps.Result;
                    ids = new Texture2DArray(size, size, 6, TextureFormat.R8, false, true)
                    {
                        name = "Matterport painted props " + sweep,
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Point, // prop numbers: never blended
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    for (int f = 0; f < 6; f++) ids.SetPixelData(data, 0, f, f * size * size);
                    ids.Apply(false, true);
                    if (!Scheduler.CleanLoaded(sweep, rt.width)) yield break; // dropped meanwhile
                    if (_clean.TryGetValue(sweep, out var old)) MpUtil.Release(ref old);
                    if (_painted.TryGetValue(sweep, out var oldIds)) MpUtil.SafeDestroy(oldIds);
                    _clean[sweep] = rt;
                    _painted[sweep] = ids;
                    rt = null;
                    ids = null;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Matterportable] Could not load the clean photos of sweep {sweep}: {(e is AggregateException ae ? ae.InnerException : e).Message}");
                }
            }
            finally
            {
                foreach (var r in requests) r.req.Dispose();
                MpUtil.Release(ref rt);
                if (ids != null) MpUtil.SafeDestroy(ids);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var sweep in new List<int>(_faces.Keys)) Unload(sweep);
            foreach (var sweep in new List<int>(_depth.Keys)) Unload(sweep);
            foreach (var sweep in new List<int>(_clean.Keys)) Unload(sweep);
            MpUtil.SafeDestroy(_blit);
        }
    }
}
