using System;

namespace Matterportable.Core
{
    public struct RayHit
    {
        public float Distance;
        /// <summary>Index of the triangle in the index buffer the BVH was built from.</summary>
        public int Triangle;
        /// <summary>Barycentric weights of vertex 1 and vertex 2 (vertex 0 weight = 1 - U - V).</summary>
        public float U, V;
        public Vec3 Point;
        /// <summary>Geometric normal of the hit triangle (front side by Unity's winding rule), unit length.</summary>
        public Vec3 Normal;
    }

    /// <summary>
    /// Read-only bounding volume hierarchy over a triangle mesh (binned SAH). Thread-safe after construction;
    /// used for click picking, visibility tests, occlusion depth maps and the alignment solver.
    /// </summary>
    public sealed class Bvh
    {
        struct Node
        {
            public Vec3 Min, Max;
            public int LeftOrFirst; // interior: left child (right = left + 1); leaf: first triangle slot
            public int Count;       // > 0 for leaves
        }

        const int LeafSize = 4;
        const int Bins = 12;

        readonly Vec3[] _v0, _e1, _e2;
        readonly int[] _tri;
        Node[] _nodes;
        int _nodeCount;

        public int TriangleCount { get; }
        public Vec3 BoundsMin => _nodes[0].Min;
        public Vec3 BoundsMax => _nodes[0].Max;

        [ThreadStatic] static int[] _stack;

        public Bvh(Vec3[] positions, int[] indices)
        {
            if (indices.Length % 3 != 0) throw new ArgumentException("Index count must be a multiple of 3.");
            int n = indices.Length / 3;
            TriangleCount = n;

            var centroid = new Vec3[n];
            var tmin = new Vec3[n];
            var tmax = new Vec3[n];
            var order = new int[n];
            for (int t = 0; t < n; t++)
            {
                var a = positions[indices[3 * t]];
                var b = positions[indices[3 * t + 1]];
                var c = positions[indices[3 * t + 2]];
                tmin[t] = Vec3.Min(a, Vec3.Min(b, c));
                tmax[t] = Vec3.Max(a, Vec3.Max(b, c));
                centroid[t] = (tmin[t] + tmax[t]) * 0.5f;
                order[t] = t;
            }

            _nodes = new Node[Math.Max(1, 2 * n)];
            _nodeCount = 1;
            if (n == 0)
            {
                _nodes[0] = new Node { Min = Vec3.Zero, Max = Vec3.Zero, LeftOrFirst = 0, Count = 0 };
            }
            else
            {
                Build(order, centroid, tmin, tmax, n);
            }

            _v0 = new Vec3[n];
            _e1 = new Vec3[n];
            _e2 = new Vec3[n];
            _tri = order;
            for (int s = 0; s < n; s++)
            {
                int t = order[s];
                var a = positions[indices[3 * t]];
                _v0[s] = a;
                _e1[s] = positions[indices[3 * t + 1]] - a;
                _e2[s] = positions[indices[3 * t + 2]] - a;
            }
        }

        void Build(int[] order, Vec3[] centroid, Vec3[] tmin, Vec3[] tmax, int n)
        {
            var work = new System.Collections.Generic.Stack<(int node, int start, int count)>();
            _nodes[0].LeftOrFirst = 0;
            _nodes[0].Count = n;
            work.Push((0, 0, n));
            var binMin = new Vec3[Bins];
            var binMax = new Vec3[Bins];
            var binCount = new int[Bins];
            var rightArea = new float[Bins];
            var rightCount = new int[Bins];

            while (work.Count > 0)
            {
                var (ni, start, count) = work.Pop();
                var bmin = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
                var bmax = new Vec3(float.MinValue, float.MinValue, float.MinValue);
                var cmin = bmin;
                var cmax = bmax;
                for (int i = start; i < start + count; i++)
                {
                    int t = order[i];
                    bmin = Vec3.Min(bmin, tmin[t]);
                    bmax = Vec3.Max(bmax, tmax[t]);
                    cmin = Vec3.Min(cmin, centroid[t]);
                    cmax = Vec3.Max(cmax, centroid[t]);
                }
                _nodes[ni].Min = bmin;
                _nodes[ni].Max = bmax;

                if (count <= LeafSize)
                {
                    _nodes[ni].LeftOrFirst = start;
                    _nodes[ni].Count = count;
                    continue;
                }

                // Binned SAH over all three axes.
                int bestAxis = -1, bestSplit = -1;
                float bestCost = float.MaxValue;
                for (int axis = 0; axis < 3; axis++)
                {
                    float lo = cmin[axis], hi = cmax[axis];
                    if (hi - lo < 1e-9f) continue;
                    float scale = Bins / (hi - lo);
                    for (int b = 0; b < Bins; b++)
                    {
                        binCount[b] = 0;
                        binMin[b] = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
                        binMax[b] = new Vec3(float.MinValue, float.MinValue, float.MinValue);
                    }
                    for (int i = start; i < start + count; i++)
                    {
                        int t = order[i];
                        int b = Math.Min(Bins - 1, (int)((centroid[t][axis] - lo) * scale));
                        binCount[b]++;
                        binMin[b] = Vec3.Min(binMin[b], tmin[t]);
                        binMax[b] = Vec3.Max(binMax[b], tmax[t]);
                    }
                    var rmin = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var rmax = new Vec3(float.MinValue, float.MinValue, float.MinValue);
                    int rc = 0;
                    for (int b = Bins - 1; b > 0; b--)
                    {
                        rc += binCount[b];
                        if (binCount[b] > 0) { rmin = Vec3.Min(rmin, binMin[b]); rmax = Vec3.Max(rmax, binMax[b]); }
                        rightCount[b] = rc;
                        rightArea[b] = rc > 0 ? Area(rmin, rmax) : 0f;
                    }
                    var lmin = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var lmax = new Vec3(float.MinValue, float.MinValue, float.MinValue);
                    int lc = 0;
                    for (int b = 0; b < Bins - 1; b++)
                    {
                        lc += binCount[b];
                        if (binCount[b] > 0) { lmin = Vec3.Min(lmin, binMin[b]); lmax = Vec3.Max(lmax, binMax[b]); }
                        if (lc == 0 || rightCount[b + 1] == 0) continue;
                        float cost = lc * Area(lmin, lmax) + rightCount[b + 1] * rightArea[b + 1];
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            bestAxis = axis;
                            bestSplit = b;
                        }
                    }
                }

                int mid;
                if (bestAxis < 0)
                {
                    mid = start + count / 2; // all centroids coincide: split in half
                }
                else
                {
                    float lo = cmin[bestAxis], hi = cmax[bestAxis];
                    float scale = Bins / (hi - lo);
                    int i = start, j = start + count - 1;
                    while (i <= j)
                    {
                        int b = Math.Min(Bins - 1, (int)((centroid[order[i]][bestAxis] - lo) * scale));
                        if (b <= bestSplit) i++;
                        else
                        {
                            int tmp = order[i]; order[i] = order[j]; order[j] = tmp;
                            j--;
                        }
                    }
                    mid = i;
                    if (mid == start || mid == start + count) mid = start + count / 2;
                }

                int left = _nodeCount;
                _nodeCount += 2;
                _nodes[ni].LeftOrFirst = left;
                _nodes[ni].Count = 0;
                work.Push((left, start, mid - start));
                work.Push((left + 1, mid, start + count - mid));
            }
        }

        static float Area(Vec3 mn, Vec3 mx)
        {
            var d = mx - mn;
            return d.X * d.Y + d.Y * d.Z + d.Z * d.X;
        }

        /// <summary>Closest hit along the ray within (0, maxDistance]. Two-sided.</summary>
        public bool Raycast(Vec3 origin, Vec3 direction, float maxDistance, out RayHit hit) =>
            Trace(origin, direction, maxDistance, false, out hit);

        /// <summary>True if anything lies strictly between a and b (with a small margin at both ends).</summary>
        public bool Occluded(Vec3 a, Vec3 b, float margin = 0.05f)
        {
            var d = b - a;
            float len = d.Length;
            if (len <= 2 * margin) return false;
            var dir = d / len;
            return Trace(a + dir * margin, dir, len - 2 * margin, true, out _);
        }

        bool Trace(Vec3 o, Vec3 d, float maxDist, bool anyHit, out RayHit hit)
        {
            hit = default;
            if (TriangleCount == 0) return false;
            var stack = _stack ?? (_stack = new int[128]);
            float invX = 1f / (Math.Abs(d.X) > 1e-20f ? d.X : 1e-20f);
            float invY = 1f / (Math.Abs(d.Y) > 1e-20f ? d.Y : 1e-20f);
            float invZ = 1f / (Math.Abs(d.Z) > 1e-20f ? d.Z : 1e-20f);
            float best = maxDist;
            int bestSlot = -1;
            float bu = 0, bv = 0;

            int sp = 0;
            if (!SlabTest(ref _nodes[0], o, invX, invY, invZ, best, out _)) return false;
            stack[sp++] = 0;
            while (sp > 0)
            {
                int ni = stack[--sp];
                ref Node node = ref _nodes[ni];
                if (node.Count > 0)
                {
                    int end = node.LeftOrFirst + node.Count;
                    for (int s = node.LeftOrFirst; s < end; s++)
                    {
                        // Möller–Trumbore, no back-face culling.
                        var e1 = _e1[s];
                        var e2 = _e2[s];
                        var p = Vec3.Cross(d, e2);
                        float det = Vec3.Dot(e1, p);
                        if (det > -1e-12f && det < 1e-12f) continue;
                        float inv = 1f / det;
                        var tv = o - _v0[s];
                        float u = Vec3.Dot(tv, p) * inv;
                        if (u < 0f || u > 1f) continue;
                        var q = Vec3.Cross(tv, e1);
                        float v = Vec3.Dot(d, q) * inv;
                        if (v < 0f || u + v > 1f) continue;
                        float t = Vec3.Dot(e2, q) * inv;
                        if (t <= 1e-6f || t >= best) continue;
                        best = t;
                        bestSlot = s;
                        bu = u;
                        bv = v;
                        if (anyHit) goto Done;
                    }
                    continue;
                }

                int l = node.LeftOrFirst, r = l + 1;
                bool hl = SlabTest(ref _nodes[l], o, invX, invY, invZ, best, out float tl);
                bool hr = SlabTest(ref _nodes[r], o, invX, invY, invZ, best, out float tr);
                if (hl && hr)
                {
                    if (sp + 2 > stack.Length) Array.Resize(ref stack, stack.Length * 2);
                    // Visit the nearer child first.
                    if (tl <= tr) { stack[sp++] = r; stack[sp++] = l; }
                    else { stack[sp++] = l; stack[sp++] = r; }
                }
                else if (hl || hr)
                {
                    if (sp + 1 > stack.Length) Array.Resize(ref stack, stack.Length * 2);
                    stack[sp++] = hl ? l : r;
                }
            }
            Done:
            _stack = stack;
            if (bestSlot < 0) return false;
            var n = Vec3.Cross(_e1[bestSlot], _e2[bestSlot]).Normalized;
            hit = new RayHit
            {
                Distance = best,
                Triangle = _tri[bestSlot],
                U = bu,
                V = bv,
                Point = o + d * best,
                Normal = n,
            };
            return true;
        }

        static bool SlabTest(ref Node n, Vec3 o, float ix, float iy, float iz, float maxT, out float tEnter)
        {
            float t1 = (n.Min.X - o.X) * ix, t2 = (n.Max.X - o.X) * ix;
            float tmin = Math.Min(t1, t2), tmax = Math.Max(t1, t2);
            t1 = (n.Min.Y - o.Y) * iy; t2 = (n.Max.Y - o.Y) * iy;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
            t1 = (n.Min.Z - o.Z) * iz; t2 = (n.Max.Z - o.Z) * iz;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
            tEnter = tmin;
            return tmax >= Math.Max(tmin, 0f) && tmin <= maxT;
        }
    }
}
