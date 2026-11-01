using System;
using System.Collections.Generic;
using Matterportable.Core;

namespace Matterportable.Tests
{
    /// <summary>Small hand-made spaces for viewer and measurement tests.</summary>
    public static class TestSpaces
    {
        /// <summary>A closed room, 4 m (x) by 3 m (z) by 2.5 m high, with faces pointing inwards, optionally rotated,
        /// plus optional boxes (furniture, steps, beams; not rotated).</summary>
        public static SpaceGeometry Room(double rotationDegrees = 0, params (Vec3 min, Vec3 max)[] boxes)
        {
            double r = rotationDegrees * Math.PI / 180;
            Vec3 P(float x, float y, float z) => new Vec3((float)(x * Math.Cos(r) - z * Math.Sin(r)), y, (float)(x * Math.Sin(r) + z * Math.Cos(r)));
            var positions = new List<Vec3>();
            var indices = new List<int>();
            var centre = P(2, 1.25f, 1.5f);
            Quad(positions, indices, P(0, 0, 0), P(4, 0, 0), P(4, 0, 3), P(0, 0, 3), centre);             // floor
            Quad(positions, indices, P(0, 2.5f, 0), P(4, 2.5f, 0), P(4, 2.5f, 3), P(0, 2.5f, 3), centre); // ceiling
            Quad(positions, indices, P(0, 0, 0), P(4, 0, 0), P(4, 2.5f, 0), P(0, 2.5f, 0), centre);       // wall z = 0
            Quad(positions, indices, P(0, 0, 3), P(4, 0, 3), P(4, 2.5f, 3), P(0, 2.5f, 3), centre);       // wall z = 3
            Quad(positions, indices, P(0, 0, 0), P(0, 0, 3), P(0, 2.5f, 3), P(0, 2.5f, 0), centre);       // wall x = 0
            Quad(positions, indices, P(4, 0, 0), P(4, 0, 3), P(4, 2.5f, 3), P(4, 2.5f, 0), centre);       // wall x = 4
            foreach (var (min, max) in boxes) Box(positions, indices, min, max);
            return Build(positions, indices);
        }

        /// <summary>Only boxes, floating in nothing (platforms with edges to walk off).</summary>
        public static SpaceGeometry Boxes(params (Vec3 min, Vec3 max)[] boxes)
        {
            var positions = new List<Vec3>();
            var indices = new List<int>();
            foreach (var (min, max) in boxes) Box(positions, indices, min, max);
            return Build(positions, indices);
        }

        /// <summary>Two triangles, oriented so their normal points towards <paramref name="toward"/>.</summary>
        static void Quad(List<Vec3> positions, List<int> indices, Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec3 toward)
        {
            int i = positions.Count;
            positions.AddRange(new[] { a, b, c, d });
            bool flip = Vec3.Dot(Vec3.Cross(b - a, c - a), toward - a) < 0;
            indices.AddRange(flip ? new[] { i, i + 2, i + 1, i, i + 3, i + 2 } : new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }

        /// <summary>A closed box with faces pointing out.</summary>
        static void Box(List<Vec3> positions, List<int> indices, Vec3 min, Vec3 max)
        {
            var c = (min + max) * 0.5f;
            Vec3 V(int i) => new Vec3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            foreach (var f in new[] { new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 } })
            {
                var faceCentre = (V(f[0]) + V(f[2])) * 0.5f;
                Quad(positions, indices, V(f[0]), V(f[1]), V(f[2]), V(f[3]), faceCentre * 2f - c);
            }
        }

        static SpaceGeometry Build(List<Vec3> positions, List<int> indices)
        {
            var pos = positions.ToArray();
            var idx = indices.ToArray();
            var min = pos[0];
            var max = pos[0];
            foreach (var p in pos) { min = Vec3.Min(min, p); max = Vec3.Max(max, p); }
            var geo = new SpaceGeometry { Positions = pos, Bvh = new Bvh(pos, idx), BoundsMin = min, BoundsMax = max };
            geo.Parts.Add(new GeometryPart { Material = "m", Indices = idx });
            return geo;
        }

        /// <summary>The room with three sweeps (1.5 m camera height, two panorama tiers each), neighbours in a chain.</summary>
        public static SpaceModel RoomModel()
        {
            var geo = Room();
            var sweeps = new List<SweepInfo>();
            var spots = new[] { new Vec3(1, 1.5f, 1), new Vec3(3, 1.5f, 1), new Vec3(3, 1.5f, 2.2f) };
            for (int i = 0; i < spots.Length; i++)
            {
                var entry = new SweepEntry { Id = "s" + i, Index = i };
                entry.Tiers.Add(new PanoTier { Size = 512, Directory = $"panos/s{i}/512", Extension = "jpg" });
                entry.Tiers.Add(new PanoTier { Size = 2048, Directory = $"panos/s{i}/2048", Extension = "jpg" });
                sweeps.Add(new SweepInfo { Index = i, Id = entry.Id, Position = spots[i], FloorPoint = new Vec3(spots[i].X, 0.01f, spots[i].Z), FloorHit = true, WorldFromPano = Mat3.Identity, Entry = entry });
            }
            sweeps[0].Neighbors.Add(1);
            sweeps[1].Neighbors.AddRange(new[] { 0, 2 });
            sweeps[2].Neighbors.Add(1);
            return new SpaceModel(new SpaceManifest { ModelId = "room", Name = "Room" }, new SpaceCalibration(), geo, sweeps);
        }
    }
}
