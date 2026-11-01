using System;
using System.Collections.Generic;
using Matterportable.Core;

namespace Matterportable.Tests
{
    /// <summary>Small hand-made spaces for viewer and measurement tests.</summary>
    public static class TestSpaces
    {
        /// <summary>A closed room, 4 m (x) by 3 m (z) by 2.5 m high, with faces pointing inwards, optionally rotated.</summary>
        public static SpaceGeometry Room(double rotationDegrees = 0)
        {
            double r = rotationDegrees * Math.PI / 180;
            Vec3 P(float x, float y, float z) => new Vec3((float)(x * Math.Cos(r) - z * Math.Sin(r)), y, (float)(x * Math.Sin(r) + z * Math.Cos(r)));
            var positions = new List<Vec3>();
            var indices = new List<int>();
            void Quad(Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec3 inside)
            {
                int i = positions.Count;
                positions.AddRange(new[] { a, b, c, d });
                // Orient both triangles so their normal points into the room.
                var n = Vec3.Cross(b - a, c - a);
                bool flip = Vec3.Dot(n, inside - a) < 0;
                indices.AddRange(flip ? new[] { i, i + 2, i + 1, i, i + 3, i + 2 } : new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            var centre = P(2, 1.25f, 1.5f);
            Quad(P(0, 0, 0), P(4, 0, 0), P(4, 0, 3), P(0, 0, 3), centre);             // floor
            Quad(P(0, 2.5f, 0), P(4, 2.5f, 0), P(4, 2.5f, 3), P(0, 2.5f, 3), centre); // ceiling
            Quad(P(0, 0, 0), P(4, 0, 0), P(4, 2.5f, 0), P(0, 2.5f, 0), centre);       // wall z = 0
            Quad(P(0, 0, 3), P(4, 0, 3), P(4, 2.5f, 3), P(0, 2.5f, 3), centre);       // wall z = 3
            Quad(P(0, 0, 0), P(0, 0, 3), P(0, 2.5f, 3), P(0, 2.5f, 0), centre);       // wall x = 0
            Quad(P(4, 0, 0), P(4, 0, 3), P(4, 2.5f, 3), P(4, 2.5f, 0), centre);       // wall x = 4
            var pos = positions.ToArray();
            var idx = indices.ToArray();
            var geo = new SpaceGeometry { Positions = pos, Bvh = new Bvh(pos, idx), BoundsMin = new Vec3(0, 0, 0), BoundsMax = new Vec3(4, 2.5f, 3) };
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
