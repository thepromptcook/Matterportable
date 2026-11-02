using System;
using System.Collections.Generic;
using System.IO;

namespace Matterportable.Core
{
    /// <summary>The props as they appear in the photos: for the reconstruction step that paints them out.</summary>
    public static class PropMasks
    {
        /// <summary>How far from a prop's scanned surface the room's surfaces count as the prop's.</summary>
        public const float Halo = 0.12f;

        /// <summary>
        /// Which piece each pixel of one panorama face image shows (0 the room or nothing, i + 1 prop i), rows bottom-up
        /// like the Core's images, by ray-casting the scan from the sweep through the face's pixel centres. Room surfaces
        /// within <see cref="Halo"/> of a prop count as the prop: the scan misses thin parts (chair legs and backs) that
        /// the photo shows there, and its shadow on the floor and the wall right behind it go with it.
        /// </summary>
        public static int[] PieceMask(SpaceModel model, int sweep, int slice, int size)
        {
            var cal = model.Calibration;
            var layout = CubeFaces.TryBuildLookup(cal.Faces, out _, out _, out _) ? cal.Faces : CubeFaces.DefaultMatterportLayout();
            var s = model.Sweeps[sweep];
            var bvh = model.Geometry.Bvh;
            var pieces = model.PieceOfTriangle;
            var room = new bool[pieces.Length];
            for (int t = 0; t < pieces.Length; t++) room[t] = pieces[t] == 0;
            var mask = new int[size * size];
            System.Threading.Tasks.Parallel.For(0, size, y =>
            {
                for (int x = 0; x < size; x++)
                {
                    var dir = (s.WorldFromPano * layout[slice].Direction((x + 0.5f) / size, (y + 0.5f) / size)).Normalized;
                    if (!bvh.Raycast(s.Position, dir, 100f, out var hit)) continue;
                    int piece = pieces[hit.Triangle];
                    if (piece == 0 && model.Props.Count > 0)
                    {
                        int near = bvh.Nearest(s.Position + dir * hit.Distance, Halo, room);
                        if (near >= 0) piece = pieces[near];
                    }
                    mask[y * size + x] = piece;
                }
            });
            return mask;
        }
    }

    /// <summary>
    /// The panoramas with the furniture painted out (tools/reconstruct/clean_panos.py writes recon/clean/ and
    /// clean.json): the room behind a moved prop, and everything in "empty rooms", is drawn from them.
    /// </summary>
    public static class CleanPanoramas
    {
        public const string Folder = "recon/clean";

        /// <summary>Gives each sweep with painted faces its <see cref="SweepInfo.CleanFaces"/>: six paths in the space
        /// folder (the painted face, or the photo's where nothing was painted), and <see cref="SweepInfo.PaintedProps"/>
        /// (8-bit images: prop + 1 where it was painted out, 0 elsewhere). Returns how many sweeps have them.</summary>
        public static int Apply(string dir, IReadOnlyList<SweepInfo> sweeps)
        {
            var path = Path.Combine(dir, Folder, "clean.json");
            if (!File.Exists(path)) return 0;
            JsonValue json;
            try { json = JsonValue.Parse(File.ReadAllText(path)); }
            catch (Exception e) when (e is IOException || e is FormatException) { return 0; }
            int count = 0;
            foreach (var f in json["faces"].Items)
            {
                int s = f["sweep"].AsInt(-1), face = f["face"].AsInt(-1);
                var file = f["file"].AsString();
                if (s < 0 || s >= sweeps.Count || face < 0 || face > 5 || file == null) continue;
                var sweep = sweeps[s];
                if (sweep.CleanFaces == null)
                {
                    PanoTier photo = null; // the faces were painted at the largest single-file tier
                    foreach (var t in sweep.Entry.Tiers) if (t.Tiles <= 1 && (photo == null || t.Size > photo.Size)) photo = t;
                    if (photo == null) continue;
                    sweep.CleanFaces = new string[6];
                    sweep.PaintedProps = new string[6];
                    for (int i = 0; i < 6; i++) sweep.CleanFaces[i] = photo.FacePath(i);
                    count++;
                }
                sweep.CleanFaces[face] = Folder + "/" + file;
                var painted = f["painted"].AsString();
                if (painted != null) sweep.PaintedProps[face] = Folder + "/" + painted;
            }
            return count;
        }

        /// <summary>A sweep's six painted-prop maps (<see cref="SweepInfo.PaintedProps"/>) as one block of bytes: face
        /// after face, rows bottom-up like the Core's images, 0 for a face without one. For engines that upload raw
        /// bytes.</summary>
        public static byte[] LoadPainted(string dir, IReadOnlyList<string> painted, out int size)
        {
            var images = new RgbImage[6];
            size = 0;
            for (int i = 0; i < 6; i++)
            {
                if (painted?[i] == null) continue;
                images[i] = Png.Decode(File.ReadAllBytes(CacheLayout.Resolve(dir, painted[i])));
                if (size == 0) size = images[i].Width;
            }
            size = Math.Max(size, 1);
            int n = size * size;
            var data = new byte[6 * n];
            for (int i = 0; i < 6; i++)
            {
                var img = images[i];
                if (img == null || img.Width != size || img.Height != size) continue;
                for (int p = 0; p < n; p++) data[i * n + p] = img.Pixels[3 * p];
            }
            return data;
        }
    }
}
