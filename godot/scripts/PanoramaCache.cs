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
    /// Loads what the Core <see cref="PanoramaScheduler"/> asks for (mirrors the Unity PanoramaCache): six faces per
    /// sweep into a mipmapped Texture2DArray (decode and mipmaps on worker threads, upload on the main thread) and the
    /// occlusion depth cube (<see cref="CubeFaces.DepthCube"/> on a worker, uploaded one per frame).
    /// </summary>
    public sealed class PanoramaCache : IPanoramaLoader
    {
        readonly MatterportSpace _space;
        readonly Dictionary<int, Texture2DArray> _faces = new Dictionary<int, Texture2DArray>(), _depth = new Dictionary<int, Texture2DArray>();
        readonly ConcurrentQueue<(int sweep, ushort[] depth, int size)> _depthResults = new ConcurrentQueue<(int, ushort[], int)>();
        bool _disposed;

        public PanoramaScheduler Scheduler;

        public PanoramaCache(MatterportSpace space) { _space = space; }

        public Texture2DArray Faces(int sweep) => sweep >= 0 && _faces.TryGetValue(sweep, out var t) ? t : null;
        public Texture2DArray Depth(int sweep) => sweep >= 0 && _depth.TryGetValue(sweep, out var t) ? t : null;

        public async void Load(int sweep, PanoTier tier)
        {
            try
            {
                var dir = _space.SpaceDir;
                var faces = await Task.Run(() => DecodeFaces(dir, tier));
                if (_disposed || !Scheduler.Loaded(sweep, faces[0].GetWidth())) return;
                var tex = new Texture2DArray();
                tex.CreateFromImages(new Godot.Collections.Array<Image>(faces));
                _faces[sweep] = tex;
            }
            catch (Exception e)
            {
                GD.PushWarning($"[Matterportable] Could not load panorama {tier.Directory}: {GodotUtil.Unwrap(e)}");
                Scheduler.Failed(sweep);
            }
        }

        public void ComputeDepth(int sweep)
        {
            var bvh = _space.Model.Geometry.Bvh;
            var pieces = _space.Model.PieceOfTriangle;
            var origin = _space.Model.Sweeps[sweep].Position;
            int size = _space.Settings.DepthMapSize;
            Task.Run(() => _depthResults.Enqueue((sweep, CubeFaces.DepthCube(bvh, origin, size, pieces), size)));
        }

        public void Unload(int sweep)
        {
            _faces.Remove(sweep);
            _depth.Remove(sweep);
        }

        /// <summary>Uploads one finished depth cube per frame.</summary>
        public void Update()
        {
            if (_disposed || !_depthResults.TryDequeue(out var r)) return;
            int n = r.size * r.size;
            var layers = new Godot.Collections.Array<Image>();
            for (int f = 0; f < 6; f++)
            {
                var bytes = new byte[n * 4]; // distance, piece seen: two half floats
                Buffer.BlockCopy(r.depth, f * n * 4, bytes, 0, n * 4);
                layers.Add(Image.CreateFromData(r.size, r.size, false, Image.Format.Rgh, bytes));
            }
            var tex = new Texture2DArray();
            tex.CreateFromImages(layers);
            _depth[r.sweep] = tex;
            Scheduler.DepthReady(r.sweep);
        }

        /// <summary>Worker thread: decodes (and assembles tiled) faces in one format, with mipmaps.</summary>
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
            _faces.Clear();
            _depth.Clear();
        }
    }
}
