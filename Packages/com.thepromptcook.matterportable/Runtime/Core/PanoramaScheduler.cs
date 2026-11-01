using System;
using System.Collections.Generic;

namespace Matterportable.Core
{
    /// <summary>What an engine does for the <see cref="PanoramaScheduler"/>: load, depth, unload. Report back with
    /// <see cref="PanoramaScheduler.Loaded"/>, <see cref="PanoramaScheduler.Failed"/> and
    /// <see cref="PanoramaScheduler.DepthReady"/>.</summary>
    public interface IPanoramaLoader
    {
        /// <summary>Load the sweep's six faces at this tier (decode off the main thread, upload to the GPU).</summary>
        void Load(int sweep, PanoTier tier);
        /// <summary>Build the sweep's occlusion depth cube.</summary>
        void ComputeDepth(int sweep);
        /// <summary>Free the sweep's photos and depth cube.</summary>
        void Unload(int sweep);
    }

    /// <summary>
    /// Which panoramas to load, in which order, and which to drop: the smallest tier first and then the large one
    /// (one load per sweep at a time, so the preview really arrives first), lower priority first, within an LRU
    /// memory budget that never drops the sweeps on screen. Shared by Unity, Godot and the web.
    /// </summary>
    public sealed class PanoramaScheduler
    {
        sealed class Entry
        {
            public int Size, LoadingSize;
            public bool Depth, DepthRequested;
            public double LastUsed, LoadStarted, LoadSeconds;
        }

        struct Pending
        {
            public int Sweep;
            public PanoTier Tier;
            public float Priority;
        }

        readonly IReadOnlyList<SweepInfo> _sweeps;
        readonly ViewerSettings _settings;
        readonly IPanoramaLoader _loader;
        readonly bool _depthEnabled;
        readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        readonly List<Pending> _queue = new List<Pending>();
        double _now;

        public int MaxConcurrentLoads = 2;
        /// <summary>Sweeps that must not be dropped (the ones on screen).</summary>
        public Func<int, bool> IsPinned = _ => false;
        public int ActiveLoads { get; private set; }
        public int Queued => _queue.Count;

        /// <param name="hasMesh">Depth cubes need the mesh.</param>
        public PanoramaScheduler(IReadOnlyList<SweepInfo> sweeps, ViewerSettings settings, IPanoramaLoader loader, bool hasMesh)
        {
            _sweeps = sweeps;
            _settings = settings;
            _loader = loader;
            _depthEnabled = hasMesh;
        }

        Entry Get(int sweep) => _entries.TryGetValue(sweep, out var e) ? e : null;

        Entry GetOrCreate(int sweep)
        {
            if (!_entries.TryGetValue(sweep, out var e)) _entries[sweep] = e = new Entry { LastUsed = _now };
            return e;
        }

        public bool IsReady(int sweep) => Get(sweep)?.Size > 0;
        public int Size(int sweep) => Get(sweep)?.Size ?? 0;
        public bool HasDepth(int sweep) => Get(sweep)?.Depth ?? false;
        public double LoadSeconds(int sweep) => Get(sweep)?.LoadSeconds ?? 0;

        public int LoadedCount
        {
            get { int n = 0; foreach (var e in _entries.Values) if (e.Size > 0) n++; return n; }
        }

        public int DepthCount
        {
            get { int n = 0; foreach (var e in _entries.Values) if (e.Depth) n++; return n; }
        }

        /// <summary>Estimated GPU memory: mipmapped RGBA faces plus half-float depth cubes.</summary>
        public long BytesUsed
        {
            get { long b = 0; foreach (var e in _entries.Values) b += Bytes(e); return b; }
        }

        long Bytes(Entry e) => (long)e.Size * e.Size * 4 * 6 * 4 / 3 + (e.Depth ? (long)_settings.DepthMapSize * _settings.DepthMapSize * 4 * 6 : 0); // depth: two half floats

        /// <summary>Ask for a sweep at up to maxSize pixels per face. Lower priority loads first.</summary>
        public void Request(int sweep, int maxSize, float priority)
        {
            if (sweep < 0 || sweep >= _sweeps.Count) return;
            var tier = _sweeps[sweep].Entry.TierAtMost(maxSize);
            if (tier == null) return;
            var e = GetOrCreate(sweep);
            e.LastUsed = _now;
            if (e.Size >= tier.Size || e.LoadingSize >= tier.Size) return;
            var load = new Pending { Sweep = sweep, Tier = tier, Priority = priority };
            int i = _queue.FindIndex(q => q.Sweep == sweep && q.Tier.Size == tier.Size);
            if (i < 0) _queue.Add(load);
            else if (priority < _queue[i].Priority) _queue[i] = load;
        }

        /// <summary>The smallest tier first (a quick preview), then up to maxSize.</summary>
        public void RequestProgressive(int sweep, int maxSize, float priority)
        {
            if (sweep < 0 || sweep >= _sweeps.Count) return;
            var smallest = _sweeps[sweep].Entry.SmallestTier;
            if (!IsReady(sweep) && smallest != null) Request(sweep, smallest.Size, priority - 0.5f);
            Request(sweep, maxSize, priority);
        }

        public void Touch(int sweep)
        {
            var e = Get(sweep);
            if (e != null) e.LastUsed = _now;
        }

        public void RequestDepth(int sweep)
        {
            if (!_depthEnabled || _settings.DepthMapSize <= 0 || sweep < 0 || sweep >= _sweeps.Count) return;
            var e = GetOrCreate(sweep);
            if (e.DepthRequested) return;
            e.DepthRequested = true;
            _loader.ComputeDepth(sweep);
        }

        /// <summary>Once per frame: starts queued loads (best priority first, one per sweep at a time) and drops the
        /// least recently used panoramas while over budget.</summary>
        public void Update(float deltaTime)
        {
            _now += deltaTime;
            while (ActiveLoads < MaxConcurrentLoads)
            {
                int best = -1;
                for (int i = 0; i < _queue.Count; i++)
                {
                    if (Get(_queue[i].Sweep)?.LoadingSize > 0) continue;
                    if (best < 0 || _queue[i].Priority < _queue[best].Priority) best = i;
                }
                if (best < 0) break;
                var req = _queue[best];
                _queue.RemoveAt(best);
                var e = GetOrCreate(req.Sweep);
                if (e.Size >= req.Tier.Size) continue;
                e.LoadingSize = req.Tier.Size;
                e.LoadStarted = _now;
                ActiveLoads++;
                _loader.Load(req.Sweep, req.Tier);
            }

            long budget = (long)Math.Max(64, _settings.PanoMemoryBudgetMB) * 1024 * 1024, used = BytesUsed;
            if (used <= budget) return;
            var lru = new List<KeyValuePair<int, Entry>>(_entries);
            lru.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));
            foreach (var kv in lru)
            {
                if (used <= budget) break;
                var e = kv.Value;
                if (IsPinned(kv.Key) || e.LoadingSize > 0 || (e.Size == 0 && !e.Depth)) continue;
                used -= Bytes(e);
                _loader.Unload(kv.Key);
                e.Size = 0;
                e.Depth = e.DepthRequested = false;
            }
        }

        /// <summary>A load finished. Returns false if a larger tier is already there (discard the new faces).</summary>
        public bool Loaded(int sweep, int size)
        {
            var e = GetOrCreate(sweep);
            e.LoadingSize = 0;
            ActiveLoads = Math.Max(0, ActiveLoads - 1);
            e.LastUsed = _now;
            if (size <= e.Size) return false;
            e.Size = size;
            e.LoadSeconds = _now - e.LoadStarted;
            return true;
        }

        public void Failed(int sweep)
        {
            var e = GetOrCreate(sweep);
            e.LoadingSize = 0;
            ActiveLoads = Math.Max(0, ActiveLoads - 1);
        }

        public void DepthReady(int sweep) => GetOrCreate(sweep).Depth = true;
    }
}
