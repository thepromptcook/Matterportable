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
    /// Streams sweep panoramas from the cache folder into GPU texture arrays (6 slices, mipmapped): small tier first,
    /// then the large one (one load per sweep at a time, so the preview really arrives first), within an LRU memory
    /// budget. Occlusion depth maps are ray-cast on worker threads from the mesh BVH.
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
        readonly ConcurrentQueue<(int sweep, ushort[] depth, int size)> _depthResults = new ConcurrentQueue<(int, ushort[], int)>();
        readonly Material _blit;
        int _activeLoads;
        bool _disposed;

        public int MaxConcurrentLoads = 2;
        /// <summary>Sweeps that must not be evicted (current, transition target).</summary>
        public Func<int, bool> IsPinned = _ => false;

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

        Entry GetOrCreate(int sweep)
        {
            if (!_entries.TryGetValue(sweep, out var e)) _entries[sweep] = e = new Entry { Sweep = sweep };
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
            int existing = _queue.FindIndex(r => r.Sweep == sweep && r.Tier.Size == tier.Size);
            if (existing < 0) _queue.Add(new PendingLoad { Sweep = sweep, Tier = tier, Priority = priority });
            else if (priority < _queue[existing].Priority) _queue[existing] = new PendingLoad { Sweep = sweep, Tier = tier, Priority = priority };
        }

        /// <summary>Progressive request: the smallest tier first (quick preview), then up to <paramref name="maxSize"/>.</summary>
        public void RequestProgressive(int sweep, int maxSize, float priority)
        {
            if (sweep < 0 || sweep >= _space.Manifest.Sweeps.Count) return;
            var smallest = _space.Manifest.Sweeps[sweep].SmallestTier;
            if (!IsReady(sweep) && smallest != null) Request(sweep, smallest.Size, priority - 0.5f);
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
                try { _depthResults.Enqueue((sweep, CubeFaces.DepthCube(geo.Bvh, origin, size), size)); }
                catch (Exception ex) { Debug.LogException(ex); }
            });
        }

        public void Update()
        {
            if (_disposed) return;

            // Upload one finished depth map per frame.
            if (_depthResults.TryDequeue(out var r) && _entries.TryGetValue(r.sweep, out var depthEntry))
            {
                var tex = new Texture2DArray(r.size, r.size, 6, TextureFormat.RHalf, false, true)
                {
                    name = "Matterport depth " + r.sweep,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                int n = r.size * r.size;
                for (int f = 0; f < 6; f++) tex.SetPixelData(r.depth, 0, f, f * n);
                tex.Apply(false, true);
                if (depthEntry.Depth != null) MpUtil.SafeDestroy(depthEntry.Depth);
                depthEntry.Depth = tex;
            }

            // Start queued loads by priority, one per sweep at a time.
            while (_activeLoads < MaxConcurrentLoads)
            {
                int best = -1;
                for (int i = 0; i < _queue.Count; i++)
                {
                    var e = Get(_queue[i].Sweep);
                    if (e != null && e.LoadingSize > 0) continue;
                    if (best < 0 || _queue[i].Priority < _queue[best].Priority) best = i;
                }
                if (best < 0) break;
                var req = _queue[best];
                _queue.RemoveAt(best);
                var entry = GetOrCreate(req.Sweep);
                if (entry.Size < req.Tier.Size) _space.StartCoroutine(Load(entry, req.Tier));
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
                if (e.Depth != null) MpUtil.SafeDestroy(e.Depth);
                e.Depth = null;
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
                    requests.Add((GpuTextures.Load(CacheLayout.Resolve(_space.SpaceDir, rel)), face, x, y));
                }
                foreach (var r in requests)
                    while (!r.req.isDone) yield return null;
                if (_disposed) yield break;
                foreach (var r in requests)
                {
                    if (GpuTextures.Succeeded(r.req)) continue;
                    Debug.LogWarning($"[Matterportable] Could not load panorama {tier.Directory} face {r.face}: {r.req.error}");
                    yield break;
                }

                RenderTexture rt = null;
                float inv = 1f / tiles;
                foreach (var r in requests)
                {
                    var tex = GpuTextures.Content(r.req);
                    if (rt == null) rt = GpuTextures.Create(tex.width * tiles, tex.width * tiles, 6, TextureWrapMode.Clamp, $"Matterport pano {e.Sweep}");
                    // Tile rows are numbered from the top of the face image.
                    _blit.SetVector("_Rect", new Vector4(r.x * inv, 1f - (r.y + 1) * inv, inv, inv));
                    Graphics.Blit(tex, rt, _blit, 0, r.face);
                    MpUtil.SafeDestroy(tex);
                }
                rt.GenerateMips();

                if (e.Faces != null && e.Size >= rt.width) MpUtil.Release(ref rt); // a larger tier won the race
                else
                {
                    MpUtil.Release(ref e.Faces);
                    e.Faces = rt;
                    e.Size = rt.width;
                }
                e.LoadSeconds = Time.realtimeSinceStartup - started;
                e.LastUsed = Time.unscaledTime;
            }
            finally
            {
                foreach (var r in requests) r.req.Dispose();
                e.LoadingSize = 0;
                _activeLoads--;
            }
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
