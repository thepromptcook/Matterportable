using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Matterportable.Core;

namespace Matterportable
{
    /// <summary>
    /// Mirrors the Unity PanoramaCache: streams sweep panoramas into 6-layer mipmapped Texture2DArrays (small tier
    /// first, then the large one, one load per sweep at a time) within an LRU memory budget. Images decode and build
    /// mipmaps on worker threads; the main thread only uploads. Depth cubes come from <see cref="CubeFaces.DepthCube"/>.
    /// </summary>
    public sealed class PanoramaCache
    {
        public sealed class Entry
        {
            public int Sweep, Size, LoadingSize;
            public Texture2DArray Faces, Depth;
            public bool DepthRequested;
            public float LastUsed, LoadSeconds;

            public long Bytes => (Faces == null ? 0 : (long)Size * Size * 3 * 6 * 4 / 3) + (Depth == null ? 0 : (long)Depth.GetWidth() * Depth.GetHeight() * 2 * 6);
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
        bool _disposed;

        public int MaxConcurrentLoads = 2;
        public Func<int, bool> IsPinned = _ => false;
        public int ActiveLoads { get; private set; }
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

        public PanoramaCache(MatterportSpace space) { _space = space; }

        static float Now => Time.GetTicksMsec() / 1000f;

        public Entry Get(int sweep) => _entries.TryGetValue(sweep, out var e) ? e : null;
        public bool IsReady(int sweep) => Get(sweep)?.Faces != null;

        Entry GetOrCreate(int sweep)
        {
            if (!_entries.TryGetValue(sweep, out var e)) _entries[sweep] = e = new Entry { Sweep = sweep };
            return e;
        }

        public void Request(int sweep, int maxSize, float priority)
        {
            if (_disposed || sweep < 0 || sweep >= _space.Manifest.Sweeps.Count) return;
            var tier = _space.Manifest.Sweeps[sweep].TierAtMost(maxSize);
            if (tier == null) return;
            var e = GetOrCreate(sweep);
            e.LastUsed = Now;
            if (e.Size >= tier.Size || e.LoadingSize >= tier.Size) return;
            int existing = _queue.FindIndex(r => r.Sweep == sweep && r.Tier.Size == tier.Size);
            var load = new PendingLoad { Sweep = sweep, Tier = tier, Priority = priority };
            if (existing < 0) _queue.Add(load);
            else if (priority < _queue[existing].Priority) _queue[existing] = load;
        }

        /// <summary>The smallest tier first (quick preview), then up to <paramref name="maxSize"/>.</summary>
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
            if (e != null) e.LastUsed = Now;
        }

        public void RequestDepth(int sweep)
        {
            int size = _space.Settings.DepthMapSize;
            var geo = _space.Geometry;
            if (_disposed || size <= 0 || geo == null || sweep < 0 || sweep >= _space.Sweeps.Count) return;
            var e = GetOrCreate(sweep);
            if (e.DepthRequested) return;
            e.DepthRequested = true;
            var origin = _space.Sweeps[sweep].Position;
            Task.Run(() =>
            {
                try { _depthResults.Enqueue((sweep, CubeFaces.DepthCube(geo.Bvh, origin, size), size)); }
                catch (Exception ex) { GD.PushError("[Matterportable] Depth map failed: " + ex.Message); }
            });
        }

        public void Update()
        {
            if (_disposed) return;

            // Upload one finished depth cube per frame.
            if (_depthResults.TryDequeue(out var r) && _entries.TryGetValue(r.sweep, out var depthEntry))
            {
                int n = r.size * r.size;
                var layers = new Godot.Collections.Array<Image>();
                for (int f = 0; f < 6; f++)
                {
                    var bytes = new byte[n * 2];
                    Buffer.BlockCopy(r.depth, f * n * 2, bytes, 0, n * 2);
                    layers.Add(Image.CreateFromData(r.size, r.size, false, Image.Format.Rh, bytes));
                }
                var tex = new Texture2DArray();
                tex.CreateFromImages(layers);
                depthEntry.Depth = tex;
            }

            while (ActiveLoads < MaxConcurrentLoads)
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
                if (entry.Size < req.Tier.Size) Load(entry, req.Tier);
            }

            EvictOverBudget();
        }

        void EvictOverBudget()
        {
            long budget = (long)Math.Max(64, _space.Settings.PanoMemoryBudgetMB) * 1024 * 1024;
            long used = BytesUsed;
            if (used <= budget) return;
            var candidates = new List<Entry>(_entries.Values);
            candidates.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
            foreach (var e in candidates)
            {
                if (used <= budget) break;
                if (IsPinned(e.Sweep) || e.LoadingSize > 0 || e.Faces == null) continue;
                used -= e.Bytes;
                e.Faces = null;
                e.Size = 0;
                e.Depth = null;
                e.DepthRequested = false;
            }
        }

        async void Load(Entry e, PanoTier tier)
        {
            ActiveLoads++;
            e.LoadingSize = tier.Size;
            float started = Now;
            var dir = _space.SpaceDir;
            try
            {
                var faces = await Task.Run(() => DecodeFaces(dir, tier));
                if (_disposed) return;
                var tex = new Texture2DArray();
                tex.CreateFromImages(new Godot.Collections.Array<Image>(faces));
                if (e.Faces == null || e.Size < faces[0].GetWidth())
                {
                    e.Faces = tex;
                    e.Size = faces[0].GetWidth();
                }
                e.LoadSeconds = Now - started;
                e.LastUsed = Now;
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[Matterportable] Could not load panorama {tier.Directory}: {GodotUtil.Unwrap(ex)}");
            }
            finally
            {
                e.LoadingSize = 0;
                ActiveLoads--;
            }
        }

        /// <summary>Worker thread: decodes (and assembles tiled) faces, converts to one format, builds mipmaps.</summary>
        static Image[] DecodeFaces(string spaceDir, PanoTier tier)
        {
            int tiles = Math.Max(1, tier.Tiles);
            var faces = new Image[6];
            for (int face = 0; face < 6; face++)
            {
                Image img = null;
                for (int y = 0; y < tiles; y++)
                for (int x = 0; x < tiles; x++)
                {
                    var tile = GodotUtil.LoadImage(CacheLayout.Resolve(spaceDir, tiles == 1 ? tier.FacePath(face) : tier.TilePath(face, x, y)));
                    tile.Convert(Image.Format.Rgb8);
                    if (tiles == 1) { img = tile; continue; }
                    int tw = tile.GetWidth();
                    img ??= Image.CreateEmpty(tw * tiles, tw * tiles, false, Image.Format.Rgb8);
                    img.BlitRect(tile, new Rect2I(0, 0, tw, tw), new Vector2I(x * tw, y * tw)); // tile rows count from the top
                }
                if (img == null) throw new FileNotFoundException("missing face " + face);
                img.GenerateMipmaps();
                faces[face] = img;
            }
            return faces;
        }

        public void Dispose()
        {
            _disposed = true;
            _entries.Clear();
            _queue.Clear();
        }
    }
}
