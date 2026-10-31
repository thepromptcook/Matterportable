using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Matterportable.Core
{
    public sealed class GeometryPart
    {
        public string Material;
        /// <summary>Triangle list into <see cref="SpaceGeometry.Positions"/>.</summary>
        public int[] Indices;
        /// <summary>Offset of this part's first triangle in the BVH triangle numbering.</summary>
        public int FirstTriangle;
    }

    /// <summary>The mesh converted to Unity's frame, merged per material, with a BVH for queries.</summary>
    public sealed class SpaceGeometry
    {
        public Vec3[] Positions;
        public Vec2[] Uvs;
        public List<GeometryPart> Parts = new List<GeometryPart>();
        public Bvh Bvh;
        public Vec3 BoundsMin, BoundsMax;

        public bool HasUvs => Uvs != null && Uvs.Length == Positions.Length;
        public int TriangleCount => Bvh?.TriangleCount ?? 0;

        public static SpaceGeometry Build(MeshData mesh, SpaceCalibration cal)
        {
            var m = Frames.ToUnity(cal.MeshFrame);
            int n = mesh.VertexCount;
            var g = new SpaceGeometry { Positions = new Vec3[n] };
            var min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < n; i++)
            {
                var p = m * mesh.Vertex(i);
                g.Positions[i] = p;
                min = Vec3.Min(min, p);
                max = Vec3.Max(max, p);
            }
            g.BoundsMin = n > 0 ? min : Vec3.Zero;
            g.BoundsMax = n > 0 ? max : Vec3.Zero;

            if (mesh.HasUvs)
            {
                g.Uvs = new Vec2[n];
                for (int i = 0; i < n; i++)
                {
                    float u = mesh.Uvs[2 * i], v = mesh.Uvs[2 * i + 1];
                    g.Uvs[i] = new Vec2(u, cal.FlipTextureV ? 1f - v : v);
                }
            }

            // Converting between handedness mirrors the mesh, which flips apparent winding. A right-handed
            // CCW-front mesh therefore ends up CW-front in Unity automatically; FlipWinding corrects sources
            // that do not follow that convention.
            bool flip = cal.FlipWinding;
            var byMaterial = new Dictionary<string, List<int>>();
            var materialOrder = new List<string>();
            foreach (var part in mesh.Parts)
            {
                var key = part.Material ?? "";
                if (!byMaterial.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    byMaterial[key] = list;
                    materialOrder.Add(key);
                }
                var idx = part.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                    if (a == b || b == c || a == c) continue; // degenerate
                    list.Add(a);
                    list.Add(flip ? c : b);
                    list.Add(flip ? b : c);
                }
            }

            var all = new List<int>();
            foreach (var key in materialOrder)
            {
                var list = byMaterial[key];
                g.Parts.Add(new GeometryPart { Material = key, Indices = list.ToArray(), FirstTriangle = all.Count / 3 });
                all.AddRange(list);
            }
            g.Bvh = new Bvh(g.Positions, all.ToArray());
            return g;
        }

        /// <summary>Part index for a BVH triangle number.</summary>
        public int PartOfTriangle(int triangle)
        {
            int lo = 0, hi = Parts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (Parts[mid].FirstTriangle <= triangle) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>Interpolated UV at a ray hit (requires UVs).</summary>
        public Vec2 UvAt(in RayHit hit, out int part)
        {
            part = PartOfTriangle(hit.Triangle);
            var p = Parts[part];
            int local = (hit.Triangle - p.FirstTriangle) * 3;
            var a = Uvs[p.Indices[local]];
            var b = Uvs[p.Indices[local + 1]];
            var c = Uvs[p.Indices[local + 2]];
            float w0 = 1f - hit.U - hit.V;
            return new Vec2(a.X * w0 + b.X * hit.U + c.X * hit.V, a.Y * w0 + b.Y * hit.U + c.Y * hit.V);
        }
    }

    public sealed class SweepInfo
    {
        public int Index;
        public string Id;
        public string Label;
        public Vec3 Position;
        public Mat3 WorldFromPano;
        /// <summary>Point on the floor below the camera (marker position).</summary>
        public Vec3 FloorPoint;
        public bool FloorHit;
        public int Floor;
        public List<int> Neighbors = new List<int>();
        public SweepEntry Entry;
    }

    public static class SweepBuilder
    {
        public const float DefaultCameraHeight = 1.5f;

        public static List<SweepInfo> Build(SpaceManifest manifest, SpaceCalibration cal, SpaceGeometry geo, ILogSink log = null)
        {
            var m = Frames.ToUnity(cal.SweepFrame);
            var sweeps = new List<SweepInfo>(manifest.Sweeps.Count);
            for (int i = 0; i < manifest.Sweeps.Count; i++)
            {
                var e = manifest.Sweeps[i];
                sweeps.Add(new SweepInfo
                {
                    Index = i,
                    Id = e.Id,
                    Label = e.Label,
                    Position = m * new Vec3(e.Position[0], e.Position[1], e.Position[2]),
                    WorldFromPano = cal.WorldFromPano(e.Rotation),
                    Entry = e,
                });
            }

            // Floor markers: cast down to the mesh.
            var heights = new List<float>();
            foreach (var s in sweeps)
            {
                if (geo != null && geo.Bvh.Raycast(s.Position + Vec3.Up * 0.05f, Vec3.Down, 3.5f, out var hit))
                {
                    s.FloorPoint = hit.Point + Vec3.Up * 0.01f;
                    s.FloorHit = true;
                    heights.Add(s.Position.Y - hit.Point.Y);
                }
            }
            heights.Sort();
            float camHeight = heights.Count > 0 ? heights[heights.Count / 2] : DefaultCameraHeight;
            foreach (var s in sweeps)
                if (!s.FloorHit) s.FloorPoint = s.Position - Vec3.Up * camHeight;

            AssignFloors(manifest, sweeps);
            BuildNeighbors(manifest, sweeps, geo);
            log?.Info($"Sweeps: {sweeps.Count}, camera height ≈ {camHeight:0.00} m, " +
                      $"{sweeps.FindAll(s => s.FloorHit).Count} with floor hits.");
            return sweeps;
        }

        static void AssignFloors(SpaceManifest manifest, List<SweepInfo> sweeps)
        {
            if (manifest.Floors.Count > 0)
            {
                foreach (var s in sweeps)
                {
                    int f = manifest.FindFloor(s.Entry.FloorId);
                    s.Floor = Math.Max(0, f);
                }
                return;
            }
            // No floor data: cluster by marker height (gaps over 1.8 m start a new floor).
            var order = new List<SweepInfo>(sweeps);
            order.Sort((a, b) => a.FloorPoint.Y.CompareTo(b.FloorPoint.Y));
            int floor = 0;
            for (int i = 0; i < order.Count; i++)
            {
                if (i > 0 && order[i].FloorPoint.Y - order[i - 1].FloorPoint.Y > 1.8f) floor++;
                order[i].Floor = floor;
            }
        }

        static void BuildNeighbors(SpaceManifest manifest, List<SweepInfo> sweeps, SpaceGeometry geo)
        {
            var byId = new Dictionary<string, int>();
            foreach (var s in sweeps) if (s.Id != null) byId[s.Id] = s.Index;
            bool any = false;
            foreach (var s in sweeps)
            {
                foreach (var id in s.Entry.Neighbors)
                {
                    if (byId.TryGetValue(id, out int j) && j != s.Index && !s.Neighbors.Contains(j))
                    {
                        s.Neighbors.Add(j);
                        any = true;
                    }
                }
            }
            if (any) return;

            // No neighbour data: connect sweeps that can see each other within 8 m.
            int n = sweeps.Count;
            var adjacency = new bool[n, n];
            Parallel.For(0, n, i =>
            {
                for (int j = i + 1; j < n; j++)
                {
                    var a = sweeps[i].Position;
                    var b = sweeps[j].Position;
                    if (Vec3.Distance(a, b) > 8f || Math.Abs(a.Y - b.Y) > 2.5f) continue;
                    if (geo != null && geo.Bvh.Occluded(a, b, 0.1f)) continue;
                    adjacency[i, j] = true;
                }
            });
            for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (!adjacency[i, j]) continue;
                sweeps[i].Neighbors.Add(j);
                sweeps[j].Neighbors.Add(i);
            }
        }
    }

    /// <summary>Sanity checks on coordinate frames and winding, run before the panorama solver.</summary>
    public static class GeometryChecks
    {
        /// <summary>
        /// Index (0=x,1=y,2=z) of the raw axis most surfaces face along - floors and ceilings dominate in
        /// buildings, so this is the up axis of the source data.
        /// </summary>
        public static int DominantNormalAxis(MeshData mesh)
        {
            var area = new double[3];
            foreach (var p in mesh.Parts)
            {
                var idx = p.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    var a = mesh.Vertex(idx[t]);
                    var n = Vec3.Cross(mesh.Vertex(idx[t + 1]) - a, mesh.Vertex(idx[t + 2]) - a);
                    double len = n.Length;
                    if (len < 1e-12) continue;
                    area[0] += Math.Abs(n.X);
                    area[1] += Math.Abs(n.Y);
                    area[2] += Math.Abs(n.Z);
                }
            }
            return area[0] >= area[1] && area[0] >= area[2] ? 0 : area[1] >= area[2] ? 1 : 2;
        }

        /// <summary>
        /// Picks the sweep frame that puts cameras inside the mesh with a floor 0.3–2.5 m below them.
        /// Returns the fraction of sweeps that satisfy this for the chosen frame.
        /// </summary>
        public static CoordinateFrame BestSweepFrame(SpaceManifest manifest, SpaceGeometry geo, CoordinateFrame current, out double score)
        {
            var best = current;
            score = SweepFrameScore(manifest, geo, current);
            foreach (CoordinateFrame f in Enum.GetValues(typeof(CoordinateFrame)))
            {
                if (f == current) continue;
                double s = SweepFrameScore(manifest, geo, f);
                if (s > score + 0.2) { score = s; best = f; }
            }
            return best;
        }

        public static double SweepFrameScore(SpaceManifest manifest, SpaceGeometry geo, CoordinateFrame frame)
        {
            if (geo == null || manifest.Sweeps.Count == 0) return 0;
            var m = Frames.ToUnity(frame);
            int good = 0, total = 0;
            var lo = geo.BoundsMin - Vec3.One * 0.5f;
            var hi = geo.BoundsMax + Vec3.One * 0.5f;
            int step = Math.Max(1, manifest.Sweeps.Count / 40);
            for (int i = 0; i < manifest.Sweeps.Count; i += step)
            {
                var e = manifest.Sweeps[i];
                var p = m * new Vec3(e.Position[0], e.Position[1], e.Position[2]);
                total++;
                bool inside = p.X >= lo.X && p.Y >= lo.Y && p.Z >= lo.Z && p.X <= hi.X && p.Y <= hi.Y && p.Z <= hi.Z;
                if (!inside) continue;
                if (geo.Bvh.Raycast(p, Vec3.Down, 2.5f, out var hit) && hit.Distance > 0.3f) good++;
            }
            return total == 0 ? 0 : good / (double)total;
        }

        /// <summary>
        /// Fraction of surfaces seen from the sweeps whose front side (Unity winding) faces the camera.
        /// Close to 1 = correct winding, close to 0 = inverted.
        /// </summary>
        public static double FrontFacingFraction(SpaceGeometry geo, IList<SweepInfo> sweeps, int raysPerSweep = 200)
        {
            if (geo == null || sweeps.Count == 0) return 1;
            var rng = new Random(1234);
            int front = 0, total = 0;
            int step = Math.Max(1, sweeps.Count / 20);
            for (int i = 0; i < sweeps.Count; i += step)
            {
                var o = sweeps[i].Position;
                for (int k = 0; k < raysPerSweep; k++)
                {
                    var d = RandomDirection(rng);
                    if (!geo.Bvh.Raycast(o, d, 30f, out var hit)) continue;
                    total++;
                    if (Vec3.Dot(hit.Normal, d) < 0) front++;
                }
            }
            return total == 0 ? 1 : front / (double)total;
        }

        public static Vec3 RandomDirection(Random rng)
        {
            double z = rng.NextDouble() * 2 - 1;
            double phi = rng.NextDouble() * Math.PI * 2;
            double r = Math.Sqrt(Math.Max(0, 1 - z * z));
            return new Vec3(r * Math.Cos(phi), z, r * Math.Sin(phi));
        }
    }
}
