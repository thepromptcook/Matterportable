using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Matterportable.Core
{
    public enum MeasureUnits { Imperial, Metric }
    public enum MeasureView { Inside, Dollhouse, FloorPlan }

    /// <summary>What the measurement cursor attached to.</summary>
    public enum MeasureSnap { None, Surface, Edge, Corner, Point, Axis }

    /// <summary>Axis a segment was locked to with Shift: the space's two horizontal wall directions, or vertical.</summary>
    public enum MeasureAxis { None, X, Vertical, Z }

    /// <summary>A measurement: a polyline (continuous lines) or a single segment. Points are in the Core frame.</summary>
    public sealed class MeasureLine
    {
        public readonly List<Vec3> Points = new List<Vec3>();
        /// <summary>Axis lock per segment.</summary>
        public readonly List<MeasureAxis> Axes = new List<MeasureAxis>();
        /// <summary>Drawn in floor plan view: lengths are horizontal and the line only shows in floor plan.</summary>
        public bool FloorPlan;

        public int SegmentCount => Math.Max(0, Points.Count - 1);
        public float SegmentLength(int i) => Distance(Points[i], Points[i + 1], FloorPlan);

        public float TotalLength
        {
            get
            {
                float sum = 0;
                for (int i = 0; i < SegmentCount; i++) sum += SegmentLength(i);
                return sum;
            }
        }

        public static float Distance(Vec3 a, Vec3 b, bool horizontal) =>
            horizontal ? (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z)) : (b - a).Length;
    }

    /// <summary>One frame of pointer state, in the Core frame.</summary>
    public struct MeasureInput
    {
        public Vec3 RayOrigin, RayDirection;
        /// <summary>Radians per screen pixel (vertical field of view / screen height): scales snap distances.</summary>
        public float PixelAngle;
        /// <summary>Shift: lock the segment to the nearest axis.</summary>
        public bool AxisLock;
        /// <summary>Alt: no snapping at all.</summary>
        public bool NoSnap;
        /// <summary>The pointer is over UI or outside the view: no cursor.</summary>
        public bool PointerBlocked;
        public MeasureView View;
        /// <summary>Geometry above this height is hidden (dollhouse floor filter); +infinity for none.</summary>
        public float ClipHeight;
    }

    public struct MeasureCursor
    {
        public bool Valid;
        public Vec3 Point;
        public MeasureSnap Snap;
        public MeasureAxis Axis;
    }

    /// <summary>What to draw this frame, in the Core frame. Line -1 is the line being drawn.</summary>
    public sealed class MeasureDrawList
    {
        public struct Segment { public Vec3 A, B; public bool Occluded; public MeasureAxis Axis; public int Line; }
        public struct Label { public Vec3 At; public string Text; public int Line; }
        public struct Point { public Vec3 At; public int Line; }

        public readonly List<Segment> Segments = new List<Segment>();
        public readonly List<Label> Labels = new List<Label>();
        public readonly List<Point> Points = new List<Point>();
    }

    /// <summary>
    /// Matterport's measurement mode, shared by the Unity and Godot front ends: click to place points (continuous
    /// lines by default; Escape or Confirm ends a line, Escape again leaves the mode), snapping to corners, edges and
    /// existing points, Shift to lock to the nearest axis, Alt to disable snapping, imperial or metric labels.
    /// Lines drawn in floor plan are 2D and only shown there; the others show in Inside and Dollhouse. Measurements
    /// are temporary, as for Matterport visitors.
    /// </summary>
    public sealed class MeasureTool
    {
        public const float SnapPixels = 14f, PickPixels = 8f;

        public readonly List<MeasureLine> Lines = new List<MeasureLine>();
        public MeasureLine Draft { get; private set; }
        public MeasureCursor Cursor { get; private set; }
        public int Selected = -1;
        public int Hovered { get; private set; } = -1;
        public bool Snapping = true, Continuous = true;
        public MeasureUnits Units = DefaultUnits();
        /// <summary>The space's horizontal wall directions (Shift locks to these or to vertical).</summary>
        public Vec3 AxisX { get; }
        public Vec3 AxisZ { get; }
        public bool FeaturesReady => _features.IsCompleted;

        readonly Bvh _bvh;
        readonly Task<MeshFeatures> _features;
        MeasureView _view;
        float _clip = float.PositiveInfinity;

        /// <param name="features">Precomputed snapping features (the web viewer gets them from the server); null
        /// computes them on a worker.</param>
        public MeasureTool(SpaceGeometry geo, MeshFeatures features = null)
        {
            _bvh = geo?.Bvh;
            double a = geo != null ? MeshFeatures.DominantWallAngle(geo) : 0;
            AxisX = new Vec3((float)Math.Cos(a), 0, (float)Math.Sin(a));
            AxisZ = new Vec3(-(float)Math.Sin(a), 0, (float)Math.Cos(a));
            _features = features != null || geo == null ? Task.FromResult(features) : Task.Run(() => MeshFeatures.Build(geo));
        }

        public static MeasureUnits DefaultUnits()
        {
            try { return RegionInfo.CurrentRegion.IsMetric ? MeasureUnits.Metric : MeasureUnits.Imperial; }
            catch (Exception) { return MeasureUnits.Metric; }
        }

        /// <summary>Feet and inches (nearest inch) or metres with two decimals, like Matterport's labels.</summary>
        public static string Format(float meters, MeasureUnits units)
        {
            if (units == MeasureUnits.Metric) return meters.ToString("0.00", CultureInfo.InvariantCulture) + " m";
            int inches = (int)Math.Round(meters / 0.0254, MidpointRounding.AwayFromZero);
            return inches >= 12 ? $"{inches / 12}' {inches % 12}\"" : $"{inches}\"";
        }

        public string Format(float meters) => Format(meters, Units);

        // ------------------------------------------------------------------ per frame

        public void Update(in MeasureInput input)
        {
            _view = input.View;
            _clip = input.ClipHeight;
            if (Draft != null && Draft.FloorPlan != (_view == MeasureView.FloorPlan)) Finish();
            Hovered = -1;
            var c = default(MeasureCursor);
            if (input.PointerBlocked || _bvh == null)
            {
                Cursor = c;
                return;
            }

            bool hit = Raycast(input.RayOrigin, input.RayDirection, 1000f, out var hitPoint, out float hitDistance);
            bool snapping = Snapping && !input.NoSnap;
            if (hit)
            {
                c = new MeasureCursor { Valid = true, Point = hitPoint, Snap = MeasureSnap.Surface };
                if (snapping) SnapPoint(ref c, Math.Max(0.005f, hitDistance * input.PixelAngle * SnapPixels));
            }
            if (Draft != null)
            {
                var start = Draft.Points[Draft.Points.Count - 1];
                if (snapping && input.AxisLock) LockToAxis(ref c, start, input);
                if (c.Valid && Draft.FloorPlan) c.Point = new Vec3(c.Point.X, start.Y, c.Point.Z);
            }
            Cursor = c;
            if (Draft == null && c.Snap != MeasureSnap.Point) Hovered = PickLine(input);
        }

        /// <summary>A click in measure mode: select a hovered measurement, start a line, or add a point.</summary>
        public void Click()
        {
            if (Draft == null)
            {
                if (Hovered >= 0)
                {
                    Selected = Hovered;
                    return;
                }
                Selected = -1;
                if (!Cursor.Valid) return;
                Draft = new MeasureLine { FloorPlan = _view == MeasureView.FloorPlan };
                Draft.Points.Add(Cursor.Point);
                return;
            }
            if (!Cursor.Valid) return;
            // Clicking the last point again (a double-click) ends the line.
            if (MeasureLine.Distance(Draft.Points[Draft.Points.Count - 1], Cursor.Point, Draft.FloorPlan) < 0.01f)
            {
                Finish();
                return;
            }
            Draft.Points.Add(Cursor.Point);
            Draft.Axes.Add(Cursor.Axis);
            if (!Continuous) Finish();
        }

        /// <summary>Ends the line being drawn (Confirm button, Enter).</summary>
        public void Finish()
        {
            if (Draft != null && Draft.Points.Count >= 2) Lines.Add(Draft);
            Draft = null;
        }

        /// <summary>Escape: ends the line being drawn, else clears the selection. False when there was nothing to do
        /// (the caller then leaves measure mode).</summary>
        public bool Escape()
        {
            if (Draft != null) Finish();
            else if (Selected >= 0) Selected = -1;
            else return false;
            return true;
        }

        public void Delete(int line)
        {
            if (line < 0 || line >= Lines.Count) return;
            Lines.RemoveAt(line);
            if (Selected == line) Selected = -1;
            else if (Selected > line) Selected--;
        }

        public void DeleteSelected() => Delete(Selected);

        public void Clear()
        {
            Lines.Clear();
            Draft = null;
            Selected = -1;
        }

        /// <summary>Adds a finished measurement directly (scripts and automated checks).</summary>
        public void AddLine(IEnumerable<Vec3> points, bool floorPlan = false)
        {
            var line = new MeasureLine { FloorPlan = floorPlan };
            foreach (var p in points)
            {
                if (line.Points.Count > 0) line.Axes.Add(MeasureAxis.None);
                line.Points.Add(p);
            }
            if (line.Points.Count >= 2) Lines.Add(line);
        }

        // ------------------------------------------------------------------ drawing

        /// <summary>
        /// Fills <paramref name="list"/> with the visible measurements, the line being drawn (ending at the cursor)
        /// and their labels. Segment pieces hidden behind geometry, as seen from <paramref name="eye"/>, are marked
        /// occluded (draw them faded).
        /// </summary>
        public void BuildDrawList(Vec3 eye, MeasureDrawList list)
        {
            list.Segments.Clear();
            list.Labels.Clear();
            list.Points.Clear();
            for (int i = 0; i < Lines.Count; i++)
                if (Shown(Lines[i])) AddLine(Lines[i], i, eye, list);
            if (Draft == null) return;
            AddLine(Draft, -1, eye, list);
            if (!Cursor.Valid) return;
            var last = Draft.Points[Draft.Points.Count - 1];
            float length = MeasureLine.Distance(last, Cursor.Point, Draft.FloorPlan);
            if (length < 0.005f) return;
            AddSegment(last, Cursor.Point, Cursor.Axis, -1, eye, list);
            list.Labels.Add(new MeasureDrawList.Label { At = (last + Cursor.Point) * 0.5f, Text = Format(length), Line = -1 });
        }

        void AddLine(MeasureLine line, int index, Vec3 eye, MeasureDrawList list)
        {
            foreach (var p in line.Points) list.Points.Add(new MeasureDrawList.Point { At = p, Line = index });
            for (int s = 0; s < line.SegmentCount; s++)
            {
                AddSegment(line.Points[s], line.Points[s + 1], line.Axes[s], index, eye, list);
                list.Labels.Add(new MeasureDrawList.Label { At = (line.Points[s] + line.Points[s + 1]) * 0.5f, Text = Format(line.SegmentLength(s)), Line = index });
            }
        }

        void AddSegment(Vec3 a, Vec3 b, MeasureAxis axis, int line, Vec3 eye, MeasureDrawList list)
        {
            int n = Math.Max(1, Math.Min(24, (int)Math.Ceiling((b - a).Length / 0.2f)));
            int runStart = 0;
            bool runOccluded = Occluded(eye, Vec3.Lerp(a, b, 0.5f / n));
            for (int k = 1; k <= n; k++)
            {
                bool occluded = k < n && Occluded(eye, Vec3.Lerp(a, b, (k + 0.5f) / n));
                if (k < n && occluded == runOccluded) continue;
                list.Segments.Add(new MeasureDrawList.Segment
                {
                    A = Vec3.Lerp(a, b, (float)runStart / n), B = Vec3.Lerp(a, b, (float)k / n), Occluded = runOccluded, Axis = axis, Line = line,
                });
                runStart = k;
                runOccluded = occluded;
            }
        }

        bool Shown(MeasureLine line)
        {
            if (line.FloorPlan != (_view == MeasureView.FloorPlan)) return false;
            foreach (var p in line.Points) if (p.Y <= _clip) return true;
            return false;
        }

        /// <summary>Clips a segment to the part in front of the camera's near plane. False if it is all behind.</summary>
        public static bool ClipToFront(ref Vec3 a, ref Vec3 b, Vec3 eye, Vec3 forward, float near)
        {
            float da = Vec3.Dot(a - eye, forward) - near, db = Vec3.Dot(b - eye, forward) - near;
            if (da < 0 && db < 0) return false;
            if (da < 0) a = a + (b - a) * (da / (da - db));
            else if (db < 0) b = b + (a - b) * (db / (db - da));
            return true;
        }

        // ------------------------------------------------------------------ geometry queries

        /// <summary>
        /// First surface the user can see along the ray: outside Inside view back faces are culled (you look into
        /// rooms through their walls), and geometry above the clip height is hidden.
        /// </summary>
        bool Raycast(Vec3 origin, Vec3 dir, float maxDistance, out Vec3 point, out float distance)
        {
            point = default;
            distance = 0;
            bool cullBack = _view != MeasureView.Inside;
            var o = origin;
            for (int i = 0; i < 16 && distance < maxDistance; i++)
            {
                if (!_bvh.Raycast(o, dir, maxDistance - distance, out var hit)) return false;
                distance += hit.Distance;
                if ((!cullBack || Vec3.Dot(hit.Normal, dir) < 0) && hit.Point.Y <= _clip)
                {
                    point = hit.Point;
                    return true;
                }
                o = hit.Point + dir * 0.01f;
                distance += 0.01f;
            }
            return false;
        }

        bool Occluded(Vec3 eye, Vec3 p)
        {
            if (_bvh == null) return false;
            var d = p - eye;
            float len = d.Length;
            if (len < 0.1f) return false;
            return Raycast(eye, d / len, len - 0.05f, out _, out _);
        }

        void SnapPoint(ref MeasureCursor c, float tolerance)
        {
            // 1) Points of the measurements on screen (and earlier points of the line being drawn, to close shapes).
            float best = tolerance;
            bool found = false;
            Vec3 snapped = default, at = c.Point;
            void Consider(Vec3 p)
            {
                float d = (p - at).Length;
                if (d >= best) return;
                best = d;
                snapped = p;
                found = true;
            }
            foreach (var line in Lines)
                if (Shown(line)) foreach (var p in line.Points) Consider(p);
            if (Draft != null)
                for (int i = 0; i < Draft.Points.Count - 1; i++) Consider(Draft.Points[i]);
            if (found)
            {
                c.Point = snapped;
                c.Snap = MeasureSnap.Point;
                return;
            }

            // 2) Mesh corners, then edges (computed on a worker; plain surface points until they are ready).
            var features = _features.Status == TaskStatus.RanToCompletion ? _features.Result : null;
            if (features == null) return;
            if (features.NearestCorner(c.Point, tolerance, out var corner))
            {
                c.Point = corner;
                c.Snap = MeasureSnap.Corner;
            }
            else if (features.NearestEdgePoint(c.Point, tolerance * 0.6f, out var onEdge))
            {
                c.Point = onEdge;
                c.Snap = MeasureSnap.Edge;
            }
        }

        void LockToAxis(ref MeasureCursor c, Vec3 start, in MeasureInput input)
        {
            var target = c.Valid ? c.Point : input.RayOrigin + input.RayDirection * (start - input.RayOrigin).Length;
            var d = target - start;
            if (d.Length < 1e-4f) return;
            d = d.Normalized;
            var axis = MeasureAxis.X;
            var dir = AxisX;
            float best = Math.Abs(Vec3.Dot(d, AxisX));
            if (Math.Abs(Vec3.Dot(d, AxisZ)) > best) { best = Math.Abs(Vec3.Dot(d, AxisZ)); axis = MeasureAxis.Z; dir = AxisZ; }
            if (input.View != MeasureView.FloorPlan && Math.Abs(d.Y) > best) { axis = MeasureAxis.Vertical; dir = Vec3.Up; }
            // The point on the axis line nearest the pointer ray (falls back to projecting the cursor point).
            if (!ClosestOnLineToRay(start, dir, input.RayOrigin, input.RayDirection, out float t)) t = Vec3.Dot(target - start, dir);
            c = new MeasureCursor { Valid = true, Point = start + dir * Math.Max(-200f, Math.Min(200f, t)), Snap = MeasureSnap.Axis, Axis = axis };
        }

        static bool ClosestOnLineToRay(Vec3 s, Vec3 a, Vec3 o, Vec3 r, out float t)
        {
            var w = s - o;
            float b = Vec3.Dot(a, r), dd = Vec3.Dot(a, w), e = Vec3.Dot(r, w);
            float denom = 1f - b * b;
            t = 0;
            if (denom < 1e-4f) return false;
            t = (b * e - dd) / denom;
            return e + b * t >= 0; // the closest point must be in front of the camera
        }

        int PickLine(in MeasureInput input)
        {
            int best = -1;
            float bestDistance = float.MaxValue;
            var far = input.RayOrigin + input.RayDirection * 1000f;
            for (int i = 0; i < Lines.Count; i++)
            {
                if (!Shown(Lines[i])) continue;
                var pts = Lines[i].Points;
                for (int s = 0; s + 1 < pts.Count; s++)
                {
                    float d = SegmentDistance(pts[s], pts[s + 1], input.RayOrigin, far, out float along);
                    float tolerance = Math.Max(0.005f, along * 1000f * input.PixelAngle * PickPixels);
                    if (d < tolerance && d < bestDistance)
                    {
                        bestDistance = d;
                        best = i;
                    }
                }
            }
            return best;
        }

        /// <summary>Distance between segments p1-q1 and p2-q2; <paramref name="t"/> is the parameter on the second.</summary>
        static float SegmentDistance(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2, out float t)
        {
            var d1 = q1 - p1;
            var d2 = q2 - p2;
            var r = p1 - p2;
            float a = Vec3.Dot(d1, d1), e = Vec3.Dot(d2, d2), f = Vec3.Dot(d2, r);
            float s;
            if (a < 1e-9f)
            {
                s = 0;
                t = Clamp01(f / e);
            }
            else
            {
                float c = Vec3.Dot(d1, r), b = Vec3.Dot(d1, d2), denom = a * e - b * b;
                s = denom > 1e-12f ? Clamp01((b * f - c * e) / denom) : 0f;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Clamp01(-c / a); }
                else if (t > 1) { t = 1; s = Clamp01((b - c) / a); }
            }
            return ((p1 + d1 * s) - (p2 + d2 * t)).Length;
        }

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }

    /// <summary>Crease and boundary edges of the mesh and the corners where they meet: what measurements snap to.</summary>
    public sealed class MeshFeatures
    {
        const float Weld = 0.005f, Cell = 0.5f;

        public readonly List<Vec3> EdgeStart = new List<Vec3>(), EdgeEnd = new List<Vec3>();
        public readonly List<Vec3> Corners = new List<Vec3>();
        readonly Dictionary<(int, int, int), List<int>> _edgeCells = new Dictionary<(int, int, int), List<int>>();
        readonly Dictionary<(int, int, int), List<int>> _cornerCells = new Dictionary<(int, int, int), List<int>>();

        /// <param name="creaseDegrees">Edges whose two faces differ by more than this are features.</param>
        public static MeshFeatures Build(SpaceGeometry geo, float creaseDegrees = 40f)
        {
            var f = new MeshFeatures();
            // Weld vertices by position (chunks and materials duplicate them).
            var weldIds = new Dictionary<(long, long, long), int>();
            var welded = new List<Vec3>();
            var id = new int[geo.Positions.Length];
            for (int i = 0; i < id.Length; i++)
            {
                var p = geo.Positions[i];
                var key = ((long)Math.Round(p.X / Weld), (long)Math.Round(p.Y / Weld), (long)Math.Round(p.Z / Weld));
                if (!weldIds.TryGetValue(key, out id[i]))
                {
                    weldIds[key] = id[i] = welded.Count;
                    welded.Add(p);
                }
            }

            // Edge → first face normal, face count, crease flag. Normals are compared unsigned, so inconsistent
            // winding between chunks doesn't create false creases.
            float cosCrease = (float)Math.Cos(creaseDegrees * Math.PI / 180);
            var edges = new Dictionary<long, (Vec3 normal, int faces, bool crease)>();
            void AddEdge(int a, int b, Vec3 n)
            {
                long key = a < b ? (long)a * int.MaxValue + b : (long)b * int.MaxValue + a;
                if (!edges.TryGetValue(key, out var e)) edges[key] = (n, 1, false);
                else edges[key] = (e.normal, e.faces + 1, e.crease || Math.Abs(Vec3.Dot(e.normal, n)) < cosCrease);
            }
            foreach (var part in geo.Parts)
            {
                var idx = part.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = id[idx[t]], b = id[idx[t + 1]], c = id[idx[t + 2]];
                    if (a == b || b == c || a == c) continue;
                    var n = Vec3.Cross(welded[b] - welded[a], welded[c] - welded[a]);
                    if (n.Length < 1e-8f) continue;
                    n = n.Normalized;
                    AddEdge(a, b, n);
                    AddEdge(b, c, n);
                    AddEdge(c, a, n);
                }
            }

            var incident = new Dictionary<int, List<Vec3>>();
            foreach (var kv in edges)
            {
                if (kv.Value.faces != 1 && !kv.Value.crease) continue;
                int a = (int)(kv.Key / int.MaxValue), b = (int)(kv.Key % int.MaxValue);
                f.AddEdge(welded[a], welded[b]);
                var dir = (welded[b] - welded[a]).Normalized;
                foreach (var v in new[] { a, b })
                {
                    if (!incident.TryGetValue(v, out var list)) incident[v] = list = new List<Vec3>();
                    list.Add(dir);
                }
            }

            // Corners: three or more feature edges meet, or two meet at an angle.
            float cosCorner = (float)Math.Cos(35 * Math.PI / 180);
            foreach (var kv in incident)
            {
                var dirs = kv.Value;
                if (dirs.Count >= 3 || (dirs.Count == 2 && Math.Abs(Vec3.Dot(dirs[0], dirs[1])) < cosCorner))
                    f.AddCorner(welded[kv.Key]);
            }
            return f;
        }

        /// <summary>Edges and corners as bytes (little-endian floats), for <see cref="Deserialize"/>.</summary>
        public byte[] Serialize()
        {
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                void Point(Vec3 p) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
                w.Write(EdgeStart.Count);
                w.Write(Corners.Count);
                for (int i = 0; i < EdgeStart.Count; i++) { Point(EdgeStart[i]); Point(EdgeEnd[i]); }
                foreach (var c in Corners) Point(c);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static MeshFeatures Deserialize(byte[] data)
        {
            var f = new MeshFeatures();
            using (var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data)))
            {
                Vec3 Point() => new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                int edges = r.ReadInt32(), corners = r.ReadInt32();
                for (int i = 0; i < edges; i++) f.AddEdge(Point(), Point());
                for (int i = 0; i < corners; i++) f.AddCorner(Point());
            }
            return f;
        }

        static (int, int, int) CellOf(Vec3 p) => ((int)Math.Floor(p.X / Cell), (int)Math.Floor(p.Y / Cell), (int)Math.Floor(p.Z / Cell));

        static void Insert(Dictionary<(int, int, int), List<int>> cells, (int, int, int) key, int index)
        {
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
            if (list.Count == 0 || list[list.Count - 1] != index) list.Add(index);
        }

        void AddEdge(Vec3 a, Vec3 b)
        {
            int index = EdgeStart.Count;
            EdgeStart.Add(a);
            EdgeEnd.Add(b);
            int steps = 1 + (int)Math.Ceiling((b - a).Length / (Cell * 0.5f));
            for (int s = 0; s <= steps; s++) Insert(_edgeCells, CellOf(Vec3.Lerp(a, b, (float)s / steps)), index);
        }

        void AddCorner(Vec3 p)
        {
            Insert(_cornerCells, CellOf(p), Corners.Count);
            Corners.Add(p);
        }

        IEnumerable<int> Near(Dictionary<(int, int, int), List<int>> cells, Vec3 p, float radius)
        {
            var (x0, y0, z0) = CellOf(p - Vec3.One * radius);
            var (x1, y1, z1) = CellOf(p + Vec3.One * radius);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
                if (cells.TryGetValue((x, y, z), out var list))
                    foreach (var i in list) yield return i;
        }

        public bool NearestCorner(Vec3 p, float radius, out Vec3 corner)
        {
            corner = default;
            float best = radius;
            bool found = false;
            foreach (int i in Near(_cornerCells, p, radius))
            {
                float d = (Corners[i] - p).Length;
                if (d >= best) continue;
                best = d;
                corner = Corners[i];
                found = true;
            }
            return found;
        }

        public bool NearestEdgePoint(Vec3 p, float radius, out Vec3 point)
        {
            point = default;
            float best = radius;
            bool found = false;
            foreach (int i in Near(_edgeCells, p, radius))
            {
                var a = EdgeStart[i];
                var ab = EdgeEnd[i] - a;
                float len2 = Vec3.Dot(ab, ab);
                float t = len2 > 1e-12f ? Math.Max(0, Math.Min(1, Vec3.Dot(p - a, ab) / len2)) : 0;
                var q = a + ab * t;
                float d = (q - p).Length;
                if (d >= best) continue;
                best = d;
                point = q;
                found = true;
            }
            return found;
        }

        /// <summary>
        /// Direction (radians from +X towards +Z) of the walls, modulo 90°: the area-weighted circular mean of wall
        /// normal angles times four. Shift-locked measurements follow these axes.
        /// </summary>
        public static double DominantWallAngle(SpaceGeometry geo)
        {
            double c = 0, s = 0;
            foreach (var part in geo.Parts)
            {
                var idx = part.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    var p0 = geo.Positions[idx[t]];
                    var n = Vec3.Cross(geo.Positions[idx[t + 1]] - p0, geo.Positions[idx[t + 2]] - p0);
                    float area = n.Length;
                    if (area < 1e-8f || Math.Abs(n.Y) > 0.25f * area) continue; // walls only
                    double theta = Math.Atan2(n.Z, n.X);
                    c += area * Math.Cos(4 * theta);
                    s += area * Math.Sin(4 * theta);
                }
            }
            return c * c + s * s < 1e-12 ? 0 : Math.Atan2(s, c) / 4;
        }
    }
}
