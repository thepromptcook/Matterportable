using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Matterportable.Core
{
    public enum PropKind { Movable, Door }

    /// <summary>A piece of the scan that Walk mode can move: furniture to carry, or a door to swing on its hinge.</summary>
    public sealed class Prop
    {
        public PropKind Kind;
        /// <summary>Its triangles, in the geometry's triangle numbering (parts in order, as the BVH), sorted.</summary>
        public int[] Triangles;
        /// <summary>Bounds as scanned.</summary>
        public Vec3 Min, Max;
        /// <summary>Movable: the middle of its base. Door: the foot of its vertical hinge axis.</summary>
        public Vec3 Pivot;
        Bvh _bvh;

        /// <summary>Its own BVH, in the scanned position (built on first use; props are small).</summary>
        public Bvh Bvh(SpaceGeometry g)
        {
            if (_bvh != null) return _bvh;
            var idx = new int[Triangles.Length * 3];
            for (int i = 0; i < Triangles.Length; i++) g.TriangleVertices(Triangles[i], out idx[3 * i], out idx[3 * i + 1], out idx[3 * i + 2]);
            return _bvh = new Bvh(g.Positions, idx);
        }
    }

    /// <summary>Where a prop is now: world = Rotation * scanned + Translation.</summary>
    public struct PropPose
    {
        public Quat Rotation;
        public Vec3 Translation;
        public static readonly PropPose Identity = new PropPose { Rotation = Quat.Identity };
        public Vec3 Apply(Vec3 p) => Rotation * p + Translation;
        public Vec3 Unapply(Vec3 p) => Rotation.Inverse * (p - Translation);
        public bool IsIdentity => Translation.SqrLength < 1e-10f && Math.Abs(Rotation.W) > 0.999999f;

        /// <summary>A turn by <paramref name="degrees"/> about the vertical axis through <paramref name="pivot"/>, after
        /// <paramref name="offset"/>.</summary>
        public static PropPose About(Vec3 pivot, float degrees, Vec3 offset)
        {
            var r = Quat.AxisAngle(Vec3.Up, degrees * Math.PI / 180);
            return new PropPose { Rotation = r, Translation = pivot + offset - r * pivot };
        }
    }

    /// <summary>
    /// Finds the props in a scan, floor by floor, in a 2.5D grid (5 cm cells, 10 cm height bins above the floor):
    /// <list type="bullet">
    /// <item>a column whose surfaces run from near the floor to near the ceiling is wall;</item>
    /// <item>one whose lowest surface is more than 1.2 m up is something hanging (lamps, wall cabinets, door
    /// headers);</item>
    /// <item>anything else stands on the floor.</item>
    /// </list>
    /// Each connected patch of standing columns is a candidate. Its triangles are those whose centre lies over it, above
    /// the floor and no higher than the column's lowest run of surfaces (so the lamp over a table stays). Thin,
    /// door-sized slabs next to a wall become doors hinged at that end. Patches too large or straggly to be one piece
    /// of furniture (counters, railings, furniture pushed together) stay part of the room.
    /// </summary>
    public static class PropFinder
    {
        public const int Version = 2;
        const float Cell = 0.05f, Bin = 0.1f;
        const byte Free = 0, Wall = 1, Standing = 2, Hanging = 3;

        public static List<Prop> Find(SpaceGeometry g, IReadOnlyList<float> floorHeights)
        {
            var props = new List<Prop>();
            int n = g?.TriangleCount ?? 0;
            if (n == 0) return props;
            var centre = new Vec3[n];
            var normalY = new float[n];
            for (int t = 0; t < n; t++)
            {
                g.TriangleVertices(t, out int a, out int b, out int c);
                var pa = g.Positions[a];
                var cross = Vec3.Cross(g.Positions[b] - pa, g.Positions[c] - pa);
                float len = cross.Length;
                normalY[t] = len > 1e-12f ? cross.Y / len : 0f;
                centre[t] = (pa + g.Positions[b] + g.Positions[c]) * (1f / 3f);
            }
            var levels = floorHeights != null && floorHeights.Count > 0 ? floorHeights : new[] { g.BoundsMin.Y };
            for (int f = 0; f < levels.Count; f++)
            {
                float y1 = f + 1 < levels.Count ? levels[f + 1] : g.BoundsMax.Y + 0.1f;
                new FloorGrid(g, centre, normalY, levels[f], y1).FindProps(props);
            }
            GrowAlongMesh(g, normalY, props);
            return props;
        }

        /// <summary>
        /// Columns at a wall are wall, so the parts of a piece touching it (a chair's back rail) stay with the room and
        /// keep showing the piece where it stood. Each prop takes in the connected triangles (sharing a vertex) that lie
        /// entirely within its bounds plus 8 cm and are not vertical: taking vertical ones took bits of the wall behind
        /// (a hole in the wall, and a patch of wall carried with the chair).
        /// </summary>
        static void GrowAlongMesh(SpaceGeometry g, float[] normalY, List<Prop> props)
        {
            if (props.Count == 0) return;
            var weld = new Dictionary<(int, int, int), int>();
            var vertexWeld = new int[g.Positions.Length];
            for (int i = 0; i < vertexWeld.Length; i++)
            {
                var p = g.Positions[i];
                var key = ((int)Math.Round(p.X * 500), (int)Math.Round(p.Y * 500), (int)Math.Round(p.Z * 500));
                if (!weld.TryGetValue(key, out int w)) weld[key] = w = weld.Count;
                vertexWeld[i] = w;
            }
            var around = new List<int>[weld.Count]; // triangles at each welded vertex
            int n = g.TriangleCount;
            for (int t = 0; t < n; t++)
            {
                g.TriangleVertices(t, out int a, out int b, out int c);
                foreach (int v in new[] { a, b, c }) (around[vertexWeld[v]] ?? (around[vertexWeld[v]] = new List<int>(6))).Add(t);
            }
            var owner = new int[n];
            for (int i = 0; i < props.Count; i++) foreach (int t in props[i].Triangles) owner[t] = i + 1;
            var queue = new Queue<int>();
            for (int i = 0; i < props.Count; i++)
            {
                var p = props[i];
                var lo = new Vec3(p.Min.X - 0.08f, p.Pivot.Y + 0.02f, p.Min.Z - 0.08f);
                var hi = new Vec3(p.Max.X + 0.08f, p.Max.Y + 0.05f, p.Max.Z + 0.08f);
                bool Inside(int v) { var q = g.Positions[v]; return q.X >= lo.X && q.Y >= lo.Y && q.Z >= lo.Z && q.X <= hi.X && q.Y <= hi.Y && q.Z <= hi.Z; }
                var added = new List<int>(p.Triangles);
                foreach (int t in p.Triangles) queue.Enqueue(t);
                while (queue.Count > 0)
                {
                    g.TriangleVertices(queue.Dequeue(), out int a, out int b, out int c);
                    foreach (int v in new[] { a, b, c })
                        foreach (int u in around[vertexWeld[v]])
                        {
                            if (owner[u] != 0 || Math.Abs(normalY[u]) < 0.3f) continue;
                            g.TriangleVertices(u, out int ua, out int ub, out int uc);
                            if (!Inside(ua) || !Inside(ub) || !Inside(uc)) continue;
                            if (normalY[u] > 0.8f && (g.Positions[ua].Y + g.Positions[ub].Y + g.Positions[uc].Y) / 3 - p.Pivot.Y < 0.08f) continue; // floor
                            owner[u] = i + 1;
                            added.Add(u);
                            queue.Enqueue(u);
                        }
                }
                if (added.Count == p.Triangles.Length) continue;
                added.Sort();
                p.Triangles = added.ToArray();
                SetBounds(g, p);
            }
        }

        static void SetBounds(SpaceGeometry g, Prop prop)
        {
            var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            foreach (int t in prop.Triangles)
            {
                g.TriangleVertices(t, out int a, out int b, out int c);
                min = Vec3.Min(Vec3.Min(Vec3.Min(min, g.Positions[a]), g.Positions[b]), g.Positions[c]);
                max = Vec3.Max(Vec3.Max(Vec3.Max(max, g.Positions[a]), g.Positions[b]), g.Positions[c]);
            }
            prop.Min = min;
            prop.Max = max;
        }

        /// <summary>One floor's columns.</summary>
        sealed class FloorGrid
        {
            readonly SpaceGeometry _g;
            readonly Vec3[] _centre;
            readonly float[] _normalY;
            readonly float _y0, _y1, _x0, _z0;
            readonly int _w, _h;
            readonly ulong[] _bins;     // bit i: a surface 0.1·i to 0.1·(i + 1) m above the floor
            readonly float[] _ceiling;  // height of the ceiling above the column (NaN: none seen)
            readonly byte[] _class;
            readonly float[] _runTop;   // standing columns: top of the lowest run of surfaces

            public FloorGrid(SpaceGeometry g, Vec3[] centre, float[] normalY, float y0, float y1)
            {
                _g = g;
                _centre = centre;
                _normalY = normalY;
                _y0 = y0;
                _y1 = y1;
                _x0 = g.BoundsMin.X;
                _z0 = g.BoundsMin.Z;
                _w = (int)((g.BoundsMax.X - _x0) / Cell) + 1;
                _h = (int)((g.BoundsMax.Z - _z0) / Cell) + 1;
                _bins = new ulong[_w * _h];
                _ceiling = new float[_w * _h];
                _class = new byte[_w * _h];
                _runTop = new float[_w * _h];
                for (int i = 0; i < _ceiling.Length; i++) _ceiling[i] = float.NaN;
                Rasterize();
                Classify();
            }

            int CellOf(Vec3 p)
            {
                int x = (int)((p.X - _x0) / Cell), z = (int)((p.Z - _z0) / Cell);
                return x < 0 || z < 0 || x >= _w || z >= _h ? -1 : z * _w + x;
            }

            Vec3 CellCentre(int k) => new Vec3(_x0 + (k % _w + 0.5f) * Cell, _y0, _z0 + (k / _w + 0.5f) * Cell);

            void Rasterize()
            {
                for (int t = 0; t < _centre.Length; t++)
                {
                    float cy = _centre[t].Y;
                    if (cy < _y0 - 0.3f || cy > _y1 - 0.05f) continue;
                    _g.TriangleVertices(t, out int ia, out int ib, out int ic);
                    Vec3 a = _g.Positions[ia], ab = _g.Positions[ib] - a, ac = _g.Positions[ic] - a;
                    float longest = Math.Max(ab.Length, Math.Max(ac.Length, (ac - ab).Length));
                    int steps = Math.Max(1, (int)Math.Ceiling(longest / (Cell * 0.5f)));
                    bool down = _normalY[t] < -0.7f, up = _normalY[t] > 0.7f;
                    for (int i = 0; i <= steps; i++)
                        for (int j = 0; i + j <= steps; j++)
                        {
                            var p = a + ab * ((float)i / steps) + ac * ((float)j / steps);
                            int k = CellOf(p);
                            if (k < 0) continue;
                            float h = p.Y - _y0;
                            if (down && h > 1.8f)
                            {
                                if (float.IsNaN(_ceiling[k]) || h > _ceiling[k]) _ceiling[k] = h;
                                continue;
                            }
                            if (h < 0.05f || (up && h < 0.08f) || h >= 64 * Bin) continue; // the floor itself
                            _bins[k] |= 1UL << (int)(h / Bin);
                        }
                }
            }

            void Classify()
            {
                var ceiling = RoomCeiling();
                for (int k = 0; k < _bins.Length; k++)
                {
                    ulong o = _bins[k];
                    if (o == 0) continue;
                    int lo = 0, hi = 63;
                    while (((o >> lo) & 1) == 0) lo++;
                    while (((o >> hi) & 1) == 0) hi--;
                    int filled = 0;
                    for (int i = lo; i <= hi; i++) filled += (int)((o >> i) & 1);
                    if (lo * Bin > 1.2f) _class[k] = Hanging;
                    else if ((hi + 1) * Bin > ceiling[k] - 0.3f && filled >= 0.6f * (hi - lo + 1)) _class[k] = Wall;
                    else
                    {
                        _class[k] = Standing;
                        int top = lo; // the lowest run, across single empty bins
                        while (top < hi && (((o >> (top + 1)) & 1) != 0 || (top + 2 <= hi && ((o >> (top + 2)) & 1) != 0))) top++;
                        _runTop[k] = (top + 1) * Bin;
                    }
                }
            }

            /// <summary>The highest ceiling within half a metre of each column (a door under a lower header still
            /// counts against the room's ceiling), or the floor's median where none was seen.</summary>
            float[] RoomCeiling()
            {
                var seen = new List<float>();
                foreach (var c in _ceiling) if (!float.IsNaN(c)) seen.Add(c);
                seen.Sort();
                float median = seen.Count > 0 ? seen[seen.Count / 2] : 2.5f;
                const int r = 10;
                var rows = new float[_ceiling.Length];
                for (int z = 0; z < _h; z++)
                    for (int x = 0; x < _w; x++)
                    {
                        float m = float.NaN;
                        for (int dx = Math.Max(0, x - r); dx <= Math.Min(_w - 1, x + r); dx++)
                        {
                            float c = _ceiling[z * _w + dx];
                            if (!float.IsNaN(c) && !(m >= c)) m = c;
                        }
                        rows[z * _w + x] = m;
                    }
                var result = new float[_ceiling.Length];
                for (int z = 0; z < _h; z++)
                    for (int x = 0; x < _w; x++)
                    {
                        float m = float.NaN;
                        for (int dz = Math.Max(0, z - r); dz <= Math.Min(_h - 1, z + r); dz++)
                        {
                            float c = rows[dz * _w + x];
                            if (!float.IsNaN(c) && !(m >= c)) m = c;
                        }
                        result[z * _w + x] = float.IsNaN(m) ? median : m;
                    }
                return result;
            }

            public void FindProps(List<Prop> props)
            {
                // Connected patches of standing columns (8-neighbours).
                var patch = new int[_class.Length];
                for (int i = 0; i < patch.Length; i++) patch[i] = -1;
                var patches = new List<List<int>>();
                var queue = new Queue<int>();
                for (int s = 0; s < _class.Length; s++)
                {
                    if (_class[s] != Standing || patch[s] >= 0) continue;
                    var cells = new List<int>();
                    patch[s] = patches.Count;
                    queue.Enqueue(s);
                    while (queue.Count > 0)
                    {
                        int k = queue.Dequeue();
                        cells.Add(k);
                        int cx = k % _w, cz = k / _w;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int x = cx + dx, z = cz + dz;
                                if (x < 0 || z < 0 || x >= _w || z >= _h) continue;
                                int m = z * _w + x;
                                if (_class[m] != Standing || patch[m] >= 0) continue;
                                patch[m] = patches.Count;
                                queue.Enqueue(m);
                            }
                    }
                    patches.Add(cells);
                }

                // Which patch each column belongs to or touches (patches grown by a cell), for triangle corners.
                var near = (int[])patch.Clone();
                for (int k = 0; k < patch.Length; k++)
                {
                    if (patch[k] < 0) continue;
                    int cx = k % _w, cz = k / _w;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = cx + dx, z = cz + dz;
                            if (x >= 0 && z >= 0 && x < _w && z < _h && near[z * _w + x] < 0) near[z * _w + x] = patch[k];
                        }
                }

                // A patch's triangles: centre over it, corners over it or next to it, above the floor and below the local
                // top of the lowest run (so a triangle reaching up a wall, or across the room, stays with the room).
                var members = new List<int>[patches.Count];
                for (int t = 0; t < _centre.Length; t++)
                {
                    int k = CellOf(_centre[t]);
                    if (k < 0 || patch[k] < 0) continue;
                    int id = patch[k];
                    float h = _centre[t].Y - _y0, limit = LocalTop(k) + 0.15f;
                    if (h < 0.04f || (_normalY[t] > 0.8f && h < 0.08f)) continue;
                    _g.TriangleVertices(t, out int a, out int b, out int c);
                    if (!Over(a, id, limit, near) || !Over(b, id, limit, near) || !Over(c, id, limit, near)) continue;
                    (members[id] ?? (members[id] = new List<int>())).Add(t);
                }

                for (int i = 0; i < patches.Count; i++)
                {
                    if (members[i] == null || members[i].Count < 4 || patches[i].Count * Cell * Cell < 0.04f) continue;
                    members[i].Sort();
                    var prop = new Prop { Triangles = members[i].ToArray() };
                    Bounds(prop);
                    if (prop.Max.Y - _y0 < 0.25f) continue; // rugs, cables, low clutter
                    if (Describe(prop, patches[i])) props.Add(prop);
                }
            }

            bool Over(int vertex, int id, float limit, int[] near)
            {
                var p = _g.Positions[vertex];
                int k = CellOf(p);
                float h = p.Y - _y0;
                return k >= 0 && near[k] == id && h >= -0.05f && h <= limit;
            }

            float LocalTop(int k)
            {
                int cx = k % _w, cz = k / _w;
                float top = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = cx + dx, z = cz + dz;
                        if (x >= 0 && z >= 0 && x < _w && z < _h) top = Math.Max(top, _runTop[z * _w + x]);
                    }
                return top;
            }

            /// <summary>Makes the prop a door (a thin, door-sized slab with a wall at one end) or a movable piece of
            /// furniture, and sets its pivot; false if it is neither (too big, too tall or too straggly).</summary>
            bool Describe(Prop prop, List<int> cells)
            {
                // Principal axes of the footprint, and the triangles' extent along them.
                double mx = 0, mz = 0;
                foreach (int k in cells) { var c = CellCentre(k); mx += c.X; mz += c.Z; }
                mx /= cells.Count;
                mz /= cells.Count;
                double sxx = 0, szz = 0, sxz = 0;
                foreach (int k in cells)
                {
                    var c = CellCentre(k);
                    sxx += (c.X - mx) * (c.X - mx);
                    szz += (c.Z - mz) * (c.Z - mz);
                    sxz += (c.X - mx) * (c.Z - mz);
                }
                double angle = 0.5 * Math.Atan2(2 * sxz, sxx - szz);
                float ax = (float)Math.Cos(angle), az = (float)Math.Sin(angle);
                float along0 = float.MaxValue, along1 = float.MinValue, across0 = float.MaxValue, across1 = float.MinValue;
                foreach (int t in prop.Triangles)
                {
                    _g.TriangleVertices(t, out int a, out int b, out int c);
                    foreach (int v in new[] { a, b, c })
                    {
                        float dx = _g.Positions[v].X - (float)mx, dz = _g.Positions[v].Z - (float)mz;
                        float u = dx * ax + dz * az, w = -dx * az + dz * ax;
                        along0 = Math.Min(along0, u);
                        along1 = Math.Max(along1, u);
                        across0 = Math.Min(across0, w);
                        across1 = Math.Max(across1, w);
                    }
                }
                float length = along1 - along0, width = across1 - across0, height = prop.Max.Y - _y0, area = cells.Count * Cell * Cell;

                if (length >= 0.55f && length <= 1.3f && width <= 0.2f && height >= 1.75f && height <= 2.45f)
                {
                    var end0 = new Vec3((float)mx + ax * along0, _y0, (float)mz + az * along0);
                    var end1 = new Vec3((float)mx + ax * along1, _y0, (float)mz + az * along1);
                    int walls0 = WallsNear(end0), walls1 = WallsNear(end1);
                    if (walls0 != walls1)
                    {
                        prop.Kind = PropKind.Door;
                        prop.Pivot = walls0 > walls1 ? end0 : end1;
                        return true;
                    }
                }
                // One piece of furniture: no longer than a sofa, no larger than a bed, no taller than a bookcase, and
                // filling its outline (not a railing or an L of things pushed together).
                prop.Kind = PropKind.Movable;
                prop.Pivot = new Vec3((prop.Min.X + prop.Max.X) / 2, _y0, (prop.Min.Z + prop.Max.Z) / 2);
                return length <= 3f && area <= 4.5f && height <= 2f && (area < 0.5f || area >= 0.35f * length * width);
            }

            int WallsNear(Vec3 p)
            {
                int count = 0, c = CellOf(p);
                if (c < 0) return 0;
                int cx = c % _w, cz = c / _w;
                for (int dz = -4; dz <= 4; dz++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int x = cx + dx, z = cz + dz;
                        if (x >= 0 && z >= 0 && x < _w && z < _h && _class[z * _w + x] == Wall) count++;
                    }
                return count;
            }

            void Bounds(Prop prop) => SetBounds(_g, prop);
        }

        // ------------------------------------------------------------------ objects.json

        public const string FileName = "objects.json";

        /// <summary>Which mesh the props were found on (its triangles and bounds: a new frame or mesh finds them again).</summary>
        static string MeshKey(SpaceGeometry g) =>
            string.Format(CultureInfo.InvariantCulture, "{0}|{1:0.000},{2:0.000},{3:0.000}|{4:0.000},{5:0.000},{6:0.000}", g.TriangleCount,
                g.BoundsMin.X, g.BoundsMin.Y, g.BoundsMin.Z, g.BoundsMax.X, g.BoundsMax.Y, g.BoundsMax.Z);

        /// <summary>{ version, mesh, props: [{ kind, pivot, triangles: [first, count, …] }] } (triangle runs).</summary>
        public static JsonValue ToJson(IReadOnlyList<Prop> props, SpaceGeometry g)
        {
            var list = JsonValue.NewArray();
            foreach (var p in props)
            {
                var runs = new List<double>();
                for (int i = 0; i < p.Triangles.Length;)
                {
                    int j = i + 1;
                    while (j < p.Triangles.Length && p.Triangles[j] == p.Triangles[j - 1] + 1) j++;
                    runs.Add(p.Triangles[i]);
                    runs.Add(j - i);
                    i = j;
                }
                list.Add(JsonValue.NewObject()
                    .Set("kind", p.Kind == PropKind.Door ? "door" : "movable")
                    .Set("pivot", JsonValue.FromArray(new double[] { p.Pivot.X, p.Pivot.Y, p.Pivot.Z }))
                    .Set("triangles", JsonValue.FromArray(runs)));
            }
            return JsonValue.NewObject().Set("version", Version).Set("mesh", MeshKey(g)).Set("props", list);
        }

        /// <summary>Props from <see cref="ToJson"/>, or null if they are from another version or another mesh.</summary>
        public static List<Prop> FromJson(JsonValue j, SpaceGeometry g)
        {
            if (j["version"].AsInt() != Version || j["mesh"].AsString() != MeshKey(g)) return null;
            var props = new List<Prop>();
            int n = g?.TriangleCount ?? 0;
            foreach (var item in j["props"].Items)
            {
                var runs = item["triangles"].AsDoubleArray();
                var tris = new List<int>();
                for (int i = 0; i + 1 < runs.Length; i += 2)
                    for (int t = (int)runs[i], end = t + (int)runs[i + 1]; t < end; t++)
                    {
                        if (t < 0 || t >= n) return null;
                        tris.Add(t);
                    }
                var pivot = item["pivot"].AsDoubleArray();
                if (tris.Count == 0 || pivot.Length != 3) return null;
                var prop = new Prop
                {
                    Kind = item["kind"].AsString() == "door" ? PropKind.Door : PropKind.Movable,
                    Triangles = tris.ToArray(),
                    Pivot = new Vec3((float)pivot[0], (float)pivot[1], (float)pivot[2]),
                };
                SetBounds(g, prop);
                props.Add(prop);
            }
            return props;
        }

        /// <summary>The space's props: objects.json when it is current, otherwise found now and saved there.</summary>
        public static List<Prop> LoadOrFind(string dir, SpaceGeometry g, IReadOnlyList<float> floorHeights, ILogSink log)
        {
            var path = Path.Combine(dir, FileName);
            try
            {
                if (File.Exists(path))
                {
                    var loaded = FromJson(JsonValue.Parse(File.ReadAllText(path)), g);
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException)
            {
                log?.Info($"{FileName} unreadable ({e.Message}); finding the props again.");
            }
            var props = Find(g, floorHeights);
            try { IoUtil.WriteAllTextAtomic(path, ToJson(props, g).ToJson(true)); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { log?.Info($"Couldn't save {FileName}: {e.Message}"); }
            int doors = props.FindAll(p => p.Kind == PropKind.Door).Count;
            log?.Info($"Props: {props.Count - doors} movable, {doors} doors.");
            return props;
        }
    }

    /// <summary>The props as they appear in the photos: for the reconstruction step that paints them out.</summary>
    public static class PropMasks
    {
        /// <summary>
        /// Which piece each pixel of one panorama face image shows (0 the room or nothing, i + 1 prop i), rows bottom-up
        /// like the Core's images, by ray-casting the scan from the sweep through the face's pixel centres.
        /// </summary>
        public static int[] PieceMask(SpaceModel model, int sweep, int slice, int size)
        {
            var cal = model.Calibration;
            var layout = CubeFaces.TryBuildLookup(cal.Faces, out _, out _, out _) ? cal.Faces : CubeFaces.DefaultMatterportLayout();
            var s = model.Sweeps[sweep];
            var pieces = model.PieceOfTriangle;
            var mask = new int[size * size];
            System.Threading.Tasks.Parallel.For(0, size, y =>
            {
                for (int x = 0; x < size; x++)
                {
                    var dir = (s.WorldFromPano * layout[slice].Direction((x + 0.5f) / size, (y + 0.5f) / size)).Normalized;
                    if (model.Geometry.Bvh.Raycast(s.Position, dir, 100f, out var hit)) mask[y * size + x] = pieces[hit.Triangle];
                }
            });
            return mask;
        }
    }

    /// <summary>
    /// Walk mode's world: the scan with its props where they are now, and what the hands do with them (carry, put
    /// down, turn, swing doors). The room is the space's BVH with the props' triangles skipped; each prop is its own
    /// BVH, queried in its scanned frame through its pose.
    /// </summary>
    public sealed class PropWorld : ICollider
    {
        /// <summary>How far away a prop can be reached, and how fast doors swing.</summary>
        public const float Reach = 3f, DoorSpeed = 160f;
        readonly SpaceGeometry _g;
        readonly bool[] _isProp;
        readonly State[] _state;
        float _carryDistance, _carryTurn;
        public IReadOnlyList<Prop> Props { get; }
        /// <summary>Where each prop is now (engines draw them so).</summary>
        public readonly PropPose[] Poses;
        /// <summary>The prop being carried, or -1. The body passes through it.</summary>
        public int Carried { get; private set; } = -1;
        /// <summary>True once anything has moved from where it was scanned.</summary>
        public bool AnyMoved { get; private set; }
        /// <summary>The last try to put the carried prop down was refused (it would have landed on the body).</summary>
        public bool DropRefused { get; private set; }

        struct State { public float Yaw, DoorTarget; public Vec3 Offset; }

        public PropWorld(SpaceGeometry g, IReadOnlyList<Prop> props)
        {
            _g = g;
            Props = props;
            Poses = new PropPose[props.Count];
            _state = new State[props.Count];
            for (int i = 0; i < Poses.Length; i++) Poses[i] = PropPose.Identity;
            _isProp = new bool[g.TriangleCount];
            foreach (var p in props) foreach (int t in p.Triangles) _isProp[t] = true;
        }

        /// <summary>Picks a piece of furniture up: it keeps its distance and its turn relative to the view.</summary>
        public void Carry(int prop, Vec3 eye, float viewYaw)
        {
            if (Props[prop].Kind != PropKind.Movable) return;
            Carried = prop;
            var pivot = Poses[prop].Apply(Props[prop].Pivot);
            float dx = pivot.X - eye.X, dz = pivot.Z - eye.Z;
            _carryDistance = Math.Max(Radius(prop) + 0.45f, Math.Min(Reach, (float)Math.Sqrt(dx * dx + dz * dz)));
            _carryTurn = _state[prop].Yaw - viewYaw;
        }

        /// <summary>Turns what is carried (degrees, clockwise from above).</summary>
        public void TurnCarried(float degrees) => _carryTurn += degrees;

        /// <summary>Puts the carried prop down on whatever is under it (the floor, or a table). With a body (feet, radius
        /// and height), false, still carrying, where the prop would land inside it.</summary>
        public bool Drop(Vec3 feet = default, float bodyRadius = 0, float bodyHeight = 0)
        {
            if (Carried < 0) return false;
            int i = Carried;
            var before = _state[i].Offset;
            var pivot = Props[i].Pivot + before;
            if (FloorUnder(pivot + Vec3.Up * 0.3f, out float floor)) _state[i].Offset.Y = floor - Props[i].Pivot.Y;
            Pose(i);
            if (bodyRadius > 0 && Touches(i, feet, bodyRadius, bodyHeight))
            {
                _state[i].Offset = before;
                Pose(i);
                DropRefused = true;
                return false;
            }
            Carried = -1;
            DropRefused = false;
            return true;
        }

        /// <summary>Whether prop i, where it is now, reaches into a body of spheres stacked from the feet up.</summary>
        bool Touches(int i, Vec3 feet, float radius, float height)
        {
            var bvh = Props[i].Bvh(_g);
            for (float h = radius; h < height + radius; h += radius)
                if (bvh.DeepestContact(Poses[i].Unapply(feet + Vec3.Up * Math.Min(h, height)), radius, out _, out _)) return true;
            return false;
        }

        /// <summary>The tint a prop is drawn with (alpha: how much): the one under the pointer lightens, and the "Props"
        /// render view gives every prop its own colour. Transparent for the rest.</summary>
        public Rgba Tint(int prop, int highlight, int debugView)
        {
            if (debugView == ViewerText.PropsView)
            {
                if (Props[prop].Kind == PropKind.Door) return new Rgba(1f, 0.85f, 0.1f, 0.6f);
                double h = (prop * 0.61803398875) % 1.0 * 6;
                int sector = (int)h;
                float f = (float)(h - sector), q = 1f - f;
                var c = sector == 0 ? (1f, f, 0f) : sector == 1 ? (q, 1f, 0f) : sector == 2 ? (0f, 1f, f) : sector == 3 ? (0f, q, 1f) : sector == 4 ? (f, 0f, 1f) : (1f, 0f, q);
                return new Rgba(c.Item1, c.Item2, c.Item3, 0.55f);
            }
            return prop == highlight ? new Rgba(1f, 1f, 1f, 0.3f) : new Rgba(0f, 0f, 0f, 0f);
        }

        /// <summary>The prop is no longer where it was scanned (shaders then refuse its photo pixels for anything else).</summary>
        public bool Moved(int prop) => !Poses[prop].IsIdentity;

        /// <summary>The door has been swung (or is swinging) away from where it was scanned.</summary>
        public bool DoorSwung(int prop) => _state[prop].DoorTarget != 0;

        /// <summary>Opens a door 90° away from the viewer, or swings it back to where it was scanned.</summary>
        public void ToggleDoor(int prop, Vec3 viewer)
        {
            var p = Props[prop];
            if (p.Kind != PropKind.Door) return;
            if (_state[prop].DoorTarget != 0) { _state[prop].DoorTarget = 0; return; }
            var middle = new Vec3((p.Min.X + p.Max.X) / 2, p.Pivot.Y, (p.Min.Z + p.Max.Z) / 2);
            var plus = PropPose.About(p.Pivot, 90, Vec3.Zero).Apply(middle);
            var minus = PropPose.About(p.Pivot, -90, Vec3.Zero).Apply(middle);
            _state[prop].DoorTarget = (plus - viewer).SqrLength > (minus - viewer).SqrLength ? 90 : -90;
        }

        /// <summary>Moves what is carried in front of the eyes (short of walls, on the surface below), swings the doors
        /// and updates <see cref="Poses"/>.</summary>
        public void Update(Vec3 eye, float viewYaw, float dt)
        {
            if (Carried >= 0)
            {
                int i = Carried;
                var forward = Quat.Euler(0, viewYaw) * Vec3.Forward;
                float distance = _carryDistance, radius = Radius(i);
                if (Raycast(eye, forward, distance + radius, out var wall)) distance = Math.Max(0.3f, wall.Distance - radius);
                var target = eye + forward * distance;
                float y = FloorUnder(target, out float floor) ? floor + 0.03f : Props[i].Pivot.Y + _state[i].Offset.Y;
                var offset = new Vec3(target.X, y, target.Z) - Props[i].Pivot;
                if (DropRefused && ((offset - _state[i].Offset).Length > 0.05f || Math.Abs(viewYaw + _carryTurn - _state[i].Yaw) > 2f)) DropRefused = false;
                _state[i].Offset = offset;
                _state[i].Yaw = viewYaw + _carryTurn;
            }
            for (int i = 0; i < Props.Count; i++)
            {
                if (Props[i].Kind == PropKind.Door)
                {
                    float diff = _state[i].DoorTarget - _state[i].Yaw, step = DoorSpeed * dt;
                    _state[i].Yaw += Math.Abs(diff) <= step ? diff : Math.Sign(diff) * step;
                }
                Pose(i);
            }
        }

        void Pose(int i)
        {
            Poses[i] = PropPose.About(Props[i].Pivot, _state[i].Yaw, _state[i].Offset);
            if (!Poses[i].IsIdentity) AnyMoved = true;
        }

        /// <summary>Half the prop's diagonal across the floor.</summary>
        float Radius(int i)
        {
            var size = Props[i].Max - Props[i].Min;
            return 0.5f * (float)Math.Sqrt(size.X * size.X + size.Z * size.Z);
        }

        bool FloorUnder(Vec3 from, out float y)
        {
            y = 0;
            if (!Raycast(from, Vec3.Down, 3f, out var hit)) return false;
            y = hit.Point.Y;
            return true;
        }

        public bool Raycast(Vec3 origin, Vec3 direction, float maxDistance, out RayHit hit) => Raycast(origin, direction, maxDistance, out hit, out _);

        /// <summary>The nearest hit and the prop it belongs to (-1: the room). The carried prop is left out.</summary>
        public bool Raycast(Vec3 origin, Vec3 direction, float maxDistance, out RayHit hit, out int prop)
        {
            prop = -1;
            bool any = _g.Bvh.Raycast(origin, direction, maxDistance, out hit, _isProp);
            float best = any ? hit.Distance : maxDistance;
            for (int i = 0; i < Props.Count; i++)
            {
                if (i == Carried) continue;
                var pose = Poses[i];
                var o = pose.Unapply(origin);
                var d = pose.Rotation.Inverse * direction;
                if (!RayHitsBox(o, d, Props[i].Min, Props[i].Max, best)) continue;
                if (!Props[i].Bvh(_g).Raycast(o, d, best, out var h)) continue;
                best = h.Distance;
                h.Point = pose.Apply(h.Point);
                h.Normal = pose.Rotation * h.Normal;
                hit = h;
                prop = i;
                any = true;
            }
            return any;
        }

        public bool DeepestContact(Vec3 centre, float radius, out Vec3 push, out float depth)
        {
            bool any = _g.Bvh.DeepestContact(centre, radius, out push, out depth, _isProp);
            for (int i = 0; i < Props.Count; i++)
            {
                if (i == Carried) continue;
                var pose = Poses[i];
                var c = pose.Unapply(centre);
                var p = Props[i];
                if (c.X < p.Min.X - radius || c.Y < p.Min.Y - radius || c.Z < p.Min.Z - radius ||
                    c.X > p.Max.X + radius || c.Y > p.Max.Y + radius || c.Z > p.Max.Z + radius) continue;
                if (!p.Bvh(_g).DeepestContact(c, radius, out var n, out float d) || d <= depth) continue;
                depth = d;
                push = pose.Rotation * n;
                any = true;
            }
            return any;
        }

        static bool RayHitsBox(Vec3 o, Vec3 d, Vec3 min, Vec3 max, float maxT)
        {
            float t0 = 0, t1 = maxT;
            for (int axis = 0; axis < 3; axis++)
            {
                float oa = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z;
                float da = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
                float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
                float hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
                if (Math.Abs(da) < 1e-12f)
                {
                    if (oa < lo || oa > hi) return false;
                    continue;
                }
                float a = (lo - oa) / da, b = (hi - oa) / da;
                if (a > b) (a, b) = (b, a);
                t0 = Math.Max(t0, a);
                t1 = Math.Min(t1, b);
                if (t0 > t1) return false;
            }
            return true;
        }
    }

    /// <summary>What the walking body collides with: the scan (<see cref="Bvh"/>) or the scan with movable props
    /// (<see cref="PropWorld"/>).</summary>
    public interface ICollider
    {
        bool Raycast(Vec3 origin, Vec3 direction, float maxDistance, out RayHit hit);
        bool DeepestContact(Vec3 centre, float radius, out Vec3 push, out float depth);
    }
}
