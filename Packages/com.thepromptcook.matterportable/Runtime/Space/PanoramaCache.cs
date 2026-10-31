using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Matterportable.Core;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace Matterportable
{
    /// <summary>
    /// Streams sweep panoramas from the cache folder into GPU texture arrays (6 slices, mipmapped), with progressive
    /// resolution (small tier first, then the large one), an LRU memory budget, and occlusion depth maps built on
    /// worker threads from the mesh BVH.
    /// </summary>
    public sealed class PanoramaCache : IDisposable
    {
        public sealed class Entry
        {
            public int Sweep;
            public RenderTexture Faces;
            public int Size;
            public int LoadingSize;
            public Texture2DArray Depth;
            public bool DepthRequested;
            public float LastUsed;
            public float LoadSeconds;

            public long Bytes => (Faces == null ? 0 : (long)Size * Size * 4 * 6 * 4 / 3) +
                                 (Depth == null ? 0 : (long)Depth.width * Depth.height * 2 * 6);
        }

        struct PendingLoad
        {
            public int Sweep;
            public PanoTier Tier;
            public float Priority;
        }

        readonly MatterportSpace _space;
        readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        readonly List<PendingLoad> _queue = new List<PendingLoad>();
        readonly ConcurrentQueue<(int sweep, float[] depth, int size)> _depthResults = new ConcurrentQueue<(int, float[], int)>();
        readonly Material _blit;
        int _activeLoads;
        bool _disposed;

        public int MaxConcurrentLoads = 2;
        /// <summary>Sweeps that must not be evicted (current, transition target).</summary>
        public Func<int, bool> IsPinned = _ => false;
        public event Action<int> PanoramaLoaded;

        public int ActiveLoads => _activeLoads;
        public int Queued => _queue.Count;
        public IEnumerable<Entry> Entries => _entries.Values;

        public long BytesUsed
        {
            get
            {
                long b = 0;
                foreach (var e in _entries.Values) b += e.Bytes;
                return b;
            }
        }

        public PanoramaCache(MatterportSpace space)
        {
            _space = space;
            var shader = Shader.Find("Hidden/Matterportable/FaceBlit");
            if (shader == null) throw new InvalidOperationException("Shader Hidden/Matterportable/FaceBlit not found.");
            _blit = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public Entry Get(int sweep) => _entries.TryGetValue(sweep, out var e) ? e : null;
        public bool IsReady(int sweep) => Get(sweep)?.Faces != null;
        public bool IsLoading(int sweep) => Get(sweep)?.LoadingSize > 0 || _queue.Exists(r => r.Sweep == sweep);

        Entry GetOrCreate(int sweep)
        {
            if (!_entries.TryGetValue(sweep, out var e))
            {
                e = new Entry { Sweep = sweep };
                _entries[sweep] = e;
            }
            return e;
        }

        /// <summary>Ask for a sweep at up to <paramref name="maxSize"/> pixels per face. Lower priority loads first.</summary>
        public void Request(int sweep, int maxSize, float priority)
        {
            if (_disposed || sweep < 0 || sweep >= _space.Manifest.Sweeps.Count) return;
            var tier = _space.Manifest.Sweeps[sweep].TierAtMost(maxSize);
            if (tier == null) return;
            var e = GetOrCreate(sweep);
            e.LastUsed = Time.unscaledTime;
            if (e.Size >= tier.Size || e.LoadingSize >= tier.Size) return;
            int existing = _queue.FindIndex(r => r.Sweep == sweep);
            if (existing >= 0)
            {
                var r = _queue[existing];
                if (r.Tier.Size >= tier.Size && r.Priority <= priority) return;
                _queue.RemoveAt(existing);
                if (r.Tier.Size > tier.Size) tier = r.Tier;
                priority = Mathf.Min(priority, r.Priority);
            }
            _queue.Add(new PendingLoad { Sweep = sweep, Tier = tier, Priority = priority });
        }

        /// <summary>Progressive request: smallest tier immediately, then the target size.</summary>
        public void RequestProgressive(int sweep, int maxSize, float priority)
        {
            if (sweep < 0 || sweep >= _space.Manifest.Sweeps.Count) return;
            var entry = _space.Manifest.Sweeps[sweep];
            if (!IsReady(sweep) && entry.SmallestTier != null) Request(sweep, entry.SmallestTier.Size, priority - 0.5f);
            Request(sweep, maxSize, priority);
        }

        public void Touch(int sweep)
        {
            var e = Get(sweep);
            if (e != null) e.LastUsed = Time.unscaledTime;
        }

        public void RequestDepth(int sweep)
        {
            int size = _space.Settings.depthMapSize;
            var geo = _space.Geometry;
            if (_disposed || size <= 0 || geo == null || sweep < 0 || sweep >= _space.Sweeps.Count) return;
            var e = GetOrCreate(sweep);
            if (e.DepthRequested) return;
            e.DepthRequested = true;
            var origin = _space.Sweeps[sweep].Position;
            Task.Run(() =>
            {
                try
                {
                    var depth = ComputeDepth(geo.Bvh, origin, size);
                    _depthResults.Enqueue((sweep, depth, size));
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            });
        }

        /// <summary>Distance from the sweep to the first surface, per canonical (space-aligned) cube face texel.</summary>
        public static float[] ComputeDepth(Bvh bvh, Vec3 origin, int size)
        {
            var data = new float[6 * size * size];
            Parallel.For(0, 6 * size, row =>
            {
                int face = row / size, y = row % size;
                int offset = face * size * size + y * size;
                for (int x = 0; x < size; x++)
                {
                    var dir = CubeFaces.Direction(face, (x + 0.5f) / size, (y + 0.5f) / size).Normalized;
                    data[offset + x] = bvh.Raycast(origin, dir, 500f, out var hit) ? hit.Distance : 1000f;
                }
            });
            return data;
        }

        public void Update()
        {
            if (_disposed) return;

            // Upload finished depth maps (a couple per frame).
            for (int i = 0; i < 2 && _depthResults.TryDequeue(out var r); i++)
            {
                if (!_entries.TryGetValue(r.sweep, out var e)) continue;
                var format = SystemInfo.SupportsTextureFormat(TextureFormat.RHalf) ? TextureFormat.RHalf : TextureFormat.RFloat;
                var tex = new Texture2DArray(r.size, r.size, 6, format, false, true)
                {
                    name = "Matterport depth " + r.sweep,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                int n = r.size * r.size;
                if (format == TextureFormat.RHalf)
                {
                    var half = new ushort[n];
                    for (int f = 0; f < 6; f++)
                    {
                        for (int k = 0; k < n; k++) half[k] = Mathf.FloatToHalf(r.depth[f * n + k]);
                        tex.SetPixelData(half, 0, f);
                    }
                }
                else
                {
                    var buf = new float[n];
                    for (int f = 0; f < 6; f++)
                    {
                        Array.Copy(r.depth, f * n, buf, 0, n);
                        tex.SetPixelData(buf, 0, f);
                    }
                }
                tex.Apply(false, true);
                if (e.Depth != null) MpUtil.SafeDestroy(e.Depth);
                e.Depth = tex;
            }

            // Start queued loads, best priority first.
            while (_activeLoads < MaxConcurrentLoads && _queue.Count > 0)
            {
                int best = 0;
                for (int i = 1; i < _queue.Count; i++) if (_queue[i].Priority < _queue[best].Priority) best = i;
                var req = _queue[best];
                _queue.RemoveAt(best);
                var e = GetOrCreate(req.Sweep);
                if (e.Size >= req.Tier.Size || e.LoadingSize >= req.Tier.Size) continue;
                _space.StartCoroutine(Load(e, req.Tier));
            }

            EvictOverBudget();
        }

        void EvictOverBudget()
        {
            long budget = (long)Mathf.Max(64, _space.Settings.panoMemoryBudgetMB) * 1024 * 1024;
            long used = BytesUsed;
            if (used <= budget) return;
            var candidates = new List<Entry>(_entries.Values);
            candidates.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
            foreach (var e in candidates)
            {
                if (used <= budget) break;
                if (IsPinned(e.Sweep) || e.LoadingSize > 0 || e.Faces == null) continue;
                used -= e.Bytes;
                MpUtil.Release(ref e.Faces);
                e.Size = 0;
                if (e.Depth != null) { MpUtil.SafeDestroy(e.Depth); e.Depth = null; }
                e.DepthRequested = false;
            }
        }

        IEnumerator Load(Entry e, PanoTier tier)
        {
            _activeLoads++;
            e.LoadingSize = tier.Size;
            float started = Time.realtimeSinceStartup;
            int tiles = Mathf.Max(1, tier.Tiles);
            var requests = new List<(UnityWebRequest req, int face, int x, int y)>();
            try
            {
                for (int face = 0; face < 6; face++)
                for (int y = 0; y < tiles; y++)
                for (int x = 0; x < tiles; x++)
                {
                    var rel = tiles == 1 ? tier.FacePath(face) : tier.TilePath(face, x, y);
                    var req = UnityWebRequestTexture.GetTexture(MpUtil.FileUri(CacheLayout.Resolve(_space.SpaceDir, rel)), true);
                    req.SendWebRequest();
                    requests.Add((req, face, x, y));
                }

                foreach (var r in requests)
                    while (!r.req.isDone) yield return null;
                if (_disposed) yield break;

                foreach (var r in requests)
                {
                    if (!Succeeded(r.req))
                    {
                        Debug.LogWarning($"[Matterportable] Could not load panorama {tier.Directory} face {r.face}: {r.req.error}");
                        yield break;
                    }
                }

                RenderTexture rt = null;
                int size = 0;
                foreach (var r in requests)
                {
                    var tex = DownloadHandlerTexture.GetContent(r.req);
                    if (rt == null)
                    {
                        size = tex.width * tiles;
                        rt = CreateArray(size, $"Matterport pano {e.Sweep} ({size})");
                    }
                    float inv = 1f / tiles;
                    // Tile rows are numbered from the top of the face image.
                    _blit.SetVector("_Rect", new Vector4(r.x * inv, 1f - (r.y + 1) * inv, inv, inv));
                    Graphics.Blit(tex, rt, _blit, 0, r.face);
                    MpUtil.SafeDestroy(tex);
                }
                rt.GenerateMips();

                if (e.Faces != null && e.Size >= size)
                {
                    MpUtil.Release(ref rt);
                }
                else
                {
                    MpUtil.Release(ref e.Faces);
                    e.Faces = rt;
                    e.Size = size;
                }
                e.LoadSeconds = Time.realtimeSinceStartup - started;
                e.LastUsed = Time.unscaledTime;
                PanoramaLoaded?.Invoke(e.Sweep);
            }
            finally
            {
                foreach (var r in requests) r.req.Dispose();
                e.LoadingSize = 0;
                _activeLoads--;
            }
        }

        static bool Succeeded(UnityWebRequest req)
        {
#if UNITY_2020_2_OR_NEWER
            return req.result == UnityWebRequest.Result.Success;
#else
            return !req.isNetworkError && !req.isHttpError;
#endif
        }

        static RenderTexture CreateArray(int size, string name)
        {
            var desc = new RenderTextureDescriptor(size, size, RenderTextureFormat.ARGB32, 0)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = 6,
                useMipMap = true,
                autoGenerateMips = false,
                sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear,
                msaaSamples = 1,
            };
            var rt = new RenderTexture(desc)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var e in _entries.Values)
            {
                MpUtil.Release(ref e.Faces);
                if (e.Depth != null) MpUtil.SafeDestroy(e.Depth);
                e.Depth = null;
            }
            _entries.Clear();
            _queue.Clear();
            MpUtil.SafeDestroy(_blit);
        }
    }
}
