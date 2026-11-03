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
    /// Where other sweeps saw what a prop hides in one panorama face: for each pixel that shows a prop (or lies within
    /// <c>band</c> pixels of one, for colour matching), the floor behind it (a ray through the scan with the
    /// props skipped), and the nearest other sweep that saw that surface unblocked, as (sweep, face, s, t) in that
    /// sweep's photo. Rows top-down (as image files), so the painting script reads it without flipping.
    /// </summary>
    public sealed class Reprojection
    {
        public int Size;
        public short[] Sweep;   // -1: nothing to take, or no other sweep saw it
        public byte[] Face, Hidden; // Hidden: 1 where the photo shows a prop (0 in the band around it)
        public ushort[] S, T;   // in the source face, 0..65535 (t from the bottom, like CubeFaces)

        /// <summary>Surfaces copied from other sweeps: |normal.y| at least this (floors, steps, table tops).</summary>
        public const float FloorNormal = 0.7f;

        /// <summary>How far a source sweep may be from the surface point.</summary>
        public const float Reach = 8f;

        /// <summary>The sweeps tried as sources: the nearest to the photo's own.</summary>
        public const int Candidates = 16;

        public static Reprojection Build(SpaceModel model, int sweep, int slice, int size, int band = 3)
        {
            var ctx = new Context(model, sweep, slice);
            // A first pass at a coarse size counts which source sees most of what is hidden; the full pass then tries the
            // sources in that order, so each area comes from one photo (a mix of many reads as patchwork).
            var votes = new int[model.Sweeps.Count];
            var coarse = Map(ctx, Math.Min(size, 64), 0, ctx.ByDistance);
            for (int i = 0; i < coarse.Sweep.Length; i++) if (coarse.Hidden[i] == 1 && coarse.Sweep[i] >= 0) votes[coarse.Sweep[i]]++;
            var order = new List<int>(ctx.ByDistance);
            var rank = new Dictionary<int, int>();
            for (int i = 0; i < order.Count; i++) rank[order[i]] = i;
            order.Sort((x, y) => votes[x] != votes[y] ? votes[y].CompareTo(votes[x]) : rank[x].CompareTo(rank[y]));
            return Map(ctx, size, band, order);
        }

        sealed class Context
        {
            public readonly SpaceModel Model;
            public readonly int Sweep, Slice;
            public readonly FaceBasis[] Layout;
            public readonly bool[] IsProp, Room;
            public readonly List<int> ByDistance = new List<int>();

            public Context(SpaceModel model, int sweep, int slice)
            {
                Model = model;
                Sweep = sweep;
                Slice = slice;
                var cal = model.Calibration;
                Layout = CubeFaces.TryBuildLookup(cal.Faces, out _, out _, out _) ? cal.Faces : CubeFaces.DefaultMatterportLayout();
                var pieces = model.PieceOfTriangle;
                IsProp = new bool[pieces.Length];
                Room = new bool[pieces.Length];
                for (int t = 0; t < pieces.Length; t++) { IsProp[t] = pieces[t] > 0; Room[t] = !IsProp[t]; }
                var here = model.Sweeps[sweep].Position;
                for (int i = 0; i < model.Sweeps.Count; i++) if (i != sweep) ByDistance.Add(i);
                ByDistance.Sort((x, y) => (model.Sweeps[x].Position - here).Length.CompareTo((model.Sweeps[y].Position - here).Length));
                if (ByDistance.Count > Candidates) ByDistance.RemoveRange(Candidates, ByDistance.Count - Candidates);
            }
        }

        static Reprojection Map(Context c, int size, int band, List<int> order)
        {
            var model = c.Model;
            var bvh = model.Geometry.Bvh;
            var mask = PropMasks.PieceMask(model, c.Sweep, c.Slice, size); // bottom-up
            var r = new Reprojection { Size = size, Sweep = new short[size * size], Face = new byte[size * size], Hidden = new byte[size * size],
                                       S = new ushort[size * size], T = new ushort[size * size] };
            var s = model.Sweeps[c.Sweep];
            System.Threading.Tasks.Parallel.For(0, size, y =>
            {
                for (int x = 0; x < size; x++)
                {
                    int o = (size - 1 - y) * size + x; // top-down
                    r.Sweep[o] = -1;
                    bool hidden = mask[y * size + x] > 0, near = hidden;
                    for (int dy = -band; dy <= band && !near; dy++)
                        for (int dx = -band; dx <= band && !near; dx++)
                        {
                            int yy = y + dy, xx = x + dx;
                            near = yy >= 0 && yy < size && xx >= 0 && xx < size && mask[yy * size + xx] > 0;
                        }
                    if (!near) continue;
                    r.Hidden[o] = (byte)(hidden ? 1 : 0);
                    var dir = (s.WorldFromPano * c.Layout[c.Slice].Direction((x + 0.5f) / size, (y + 0.5f) / size)).Normalized;
                    if (!bvh.Raycast(s.Position, dir, 100f, out var behind, c.IsProp)) continue; // the room behind the props
                    // Floors only: their texture (wood, tiles, rugs) is what painting can't invent, while a copied patch of
                    // plain wall shows its slightly different exposure as a rectangle (the painting does walls well).
                    if (Math.Abs(behind.Normal.Y) < FloorNormal) continue;
                    var p = s.Position + dir * behind.Distance;
                    int best = Source(model, order, p, behind.Normal, c.Room);
                    if (best < 0) continue;
                    var src = model.Sweeps[best];
                    int face = CubeFaces.Lookup(c.Layout, src.WorldFromPano.Transposed * (p - src.Position), out float fs, out float ft);
                    if (face < 0) continue;
                    r.Sweep[o] = (short)best;
                    r.Face[o] = (byte)face;
                    r.S[o] = (ushort)Math.Round(Math.Min(1f, Math.Max(0f, fs)) * 65535);
                    r.T[o] = (ushort)Math.Round(Math.Min(1f, Math.Max(0f, ft)) * 65535);
                }
            });
            return r;
        }

        /// <summary>The first sweep in <paramref name="order"/> that sees p as the first surface, within reach, not too
        /// obliquely, with no prop near p and none along the way: the photos show props wider than the scan (chair
        /// legs), so the line of sight must miss every prop's box grown by <see cref="Margin"/>. Or -1.</summary>
        static int Source(SpaceModel model, List<int> order, Vec3 p, Vec3 normal, bool[] room)
        {
            var bvh = model.Geometry.Bvh;
            if (bvh.Nearest(p, PropMasks.Halo, room) >= 0) return -1;
            foreach (int i in order)
            {
                var v = p - model.Sweeps[i].Position;
                float d = v.Length;
                if (d >= Reach || d < 0.3f) continue;
                var dir = v / d;
                if (Math.Abs(Vec3.Dot(dir, normal)) < 0.25f) continue; // grazing: smeared in that photo
                if (!bvh.Raycast(model.Sweeps[i].Position, dir, d + 0.2f, out var hit)) continue;
                if (Math.Abs(hit.Distance - d) > 0.03f + 0.01f * d || !room[hit.Triangle]) continue;
                if (CrossesProp(model.Props, model.Sweeps[i].Position, dir, d - 0.05f)) continue;
                return i;
            }
            return -1;
        }

        /// <summary>How far around a prop's box a line of sight must pass.</summary>
        public const float Margin = 0.1f;

        static bool CrossesProp(IReadOnlyList<Prop> props, Vec3 o, Vec3 dir, float length)
        {
            foreach (var prop in props)
            {
                float near = 0, far = length;
                bool hit = true;
                for (int a = 0; a < 3 && hit; a++)
                {
                    float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z, da = a == 0 ? dir.X : a == 1 ? dir.Y : dir.Z;
                    float lo = (a == 0 ? prop.Min.X : a == 1 ? prop.Min.Y : prop.Min.Z) - Margin;
                    float hi = (a == 0 ? prop.Max.X : a == 1 ? prop.Max.Y : prop.Max.Z) + Margin;
                    if (Math.Abs(da) < 1e-8f) { hit = oa >= lo && oa <= hi; continue; }
                    float t0 = (lo - oa) / da, t1 = (hi - oa) / da;
                    if (t0 > t1) { var x = t0; t0 = t1; t1 = x; }
                    near = Math.Max(near, t0);
                    far = Math.Min(far, t1);
                    hit = near <= far;
                }
                if (hit) return true;
            }
            return false;
        }

        /// <summary>"MPR1", size, then per pixel (top-down): sweep int16, face uint8, hidden uint8, s and t uint16.</summary>
        public byte[] Serialize()
        {
            int n = Size * Size;
            var bytes = new byte[8 + n * 8];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'P'; bytes[2] = (byte)'R'; bytes[3] = (byte)'1';
            BitConverter.GetBytes(Size).CopyTo(bytes, 4);
            Buffer.BlockCopy(Sweep, 0, bytes, 8, n * 2);
            Buffer.BlockCopy(Face, 0, bytes, 8 + n * 2, n);
            Buffer.BlockCopy(Hidden, 0, bytes, 8 + n * 3, n);
            Buffer.BlockCopy(S, 0, bytes, 8 + n * 4, n * 2);
            Buffer.BlockCopy(T, 0, bytes, 8 + n * 6, n * 2);
            return bytes;
        }
    }

    /// <summary>
    /// The panoramas with the furniture painted out (tools/reconstruct/clean_panos.py writes recon/clean/ and
    /// clean.json): the room behind a moved prop, and everything in "empty rooms", is drawn from them.
    /// </summary>
    public static class CleanPanoramas
    {
        public const string Folder = "recon/clean";

        /// <summary>The space's clean.json (the painted faces); the viewers reload when it changes.</summary>
        public static string IndexPath(string dir) => Path.Combine(dir, Folder, "clean.json");

        /// <summary>Gives each sweep with painted faces its <see cref="SweepInfo.CleanFaces"/>: six paths in the space
        /// folder (the painted face, or the photo's where nothing was painted), and <see cref="SweepInfo.PaintedProps"/>
        /// (8-bit images: prop + 1 where it was painted out, 0 elsewhere). Returns how many sweeps have them.</summary>
        public static int Apply(string dir, IReadOnlyList<SweepInfo> sweeps)
        {
            var path = IndexPath(dir);
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
