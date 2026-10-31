using System;
using System.Collections.Generic;
using Matterportable.Core;
using NUnit.Framework;

namespace Matterportable.Tests
{
    public class DamTests
    {
        static MeshData Cube()
        {
            var m = new MeshData
            {
                Positions = new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1 },
                Uvs = new float[] { 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1 },
            };
            m.Parts.Add(new MeshPart { Name = "a", Material = "m_50k_000.jpg", Indices = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6 } });
            m.Parts.Add(new MeshPart { Name = "b", Material = "m_50k_001.jpg", Indices = new[] { 4, 6, 7, 1, 5, 6 } });
            return m;
        }

        static void AssertSameTriangles(MeshData src, MeshData dec, float tolerance)
        {
            Assert.AreEqual(src.TriangleCount, dec.TriangleCount);
            Assert.AreEqual(src.Parts.Count, dec.Parts.Count);
            Assert.IsTrue(dec.HasUvs);
            // Compare triangle corners (vertex order inside chunks may change).
            for (int p = 0; p < src.Parts.Count; p++)
            {
                Assert.AreEqual(src.Parts[p].Material, dec.Parts[p].Material);
                for (int i = 0; i < src.Parts[p].Indices.Length; i++)
                {
                    var a = src.Vertex(src.Parts[p].Indices[i]);
                    var b = dec.Vertex(dec.Parts[p].Indices[i]);
                    Assert.Less((a - b).Length, tolerance);
                    Assert.AreEqual(src.Uvs[2 * src.Parts[p].Indices[i] + 1], dec.Uvs[2 * dec.Parts[p].Indices[i] + 1], 1e-3);
                }
            }
        }

        [Test]
        public void RoundTripsSimpleChunks()
        {
            var src = Cube();
            var bytes = DamWriter.Write(src);
            AssertSameTriangles(src, DamDecoder.Decode(bytes), 1e-6f);
            CollectionAssert.AreEqual(new[] { "m_50k_000.jpg", "m_50k_001.jpg" }, DamDecoder.ReadMaterialNames(bytes));
        }

        [Test]
        public void RoundTripsQuantizedChunksOfARealisticMesh()
        {
            var src = SyntheticSpace.Generate(new SyntheticOptions { PanoSizes = new[] { 8 }, TextureSize = 8 }).SourceMesh;
            AssertSameTriangles(src, DamDecoder.Decode(DamWriter.Write(src, quantized: true)), 1e-3f);
        }

        [Test]
        public void RejectsGarbageWithStructureDump()
        {
            var ex = Assert.Throws<FormatException>(() => DamDecoder.Decode(new byte[] { 0x08, 0x96, 0x01 }));
            StringAssert.Contains("No mesh chunks", ex.Message);
        }
    }

    public class MathTests
    {
        [Test]
        public void ZUpConversionMapsUpToUnityUpAndFlipsHandedness()
        {
            var m = Frames.ToUnity(CoordinateFrame.ZUpRightHanded);
            Assert.AreEqual(new Vec3(0, 1, 0), m * new Vec3(0, 0, 1));
            Assert.IsTrue(Frames.FlipsHandedness(CoordinateFrame.ZUpRightHanded));
            Assert.IsTrue(Frames.FlipsHandedness(CoordinateFrame.YUpRightHanded));
            Assert.IsFalse(Frames.FlipsHandedness(CoordinateFrame.Unity));
        }

        [TestCase(0.05f)]
        [TestCase(1.53f)]
        [TestCase(12.7f)]
        [TestCase(1000f)]
        public void HalfFloatKeepsDepthPrecision(float meters)
        {
            float back = HalfFloat.ToFloat(HalfFloat.FromFloat(meters));
            Assert.AreEqual(meters, back, meters * 0.002f);
        }

        [Test]
        public void DepthCubeMeasuresDistanceToFirstSurface()
        {
            // A floor 1.5 m below the sweep: straight down (-Y face centre) hits it, straight up (+Y) misses.
            var floor = new[] { new Vec3(-50, -1.5f, -50), new Vec3(50, -1.5f, -50), new Vec3(50, -1.5f, 50), new Vec3(-50, -1.5f, 50) };
            var bvh = new Bvh(floor, new[] { 0, 1, 2, 0, 2, 3 });
            const int size = 8;
            var depth = CubeFaces.DepthCube(bvh, Vec3.Zero, size);
            int centre = (size / 2) * size + size / 2;
            Assert.AreEqual(1.5f, HalfFloat.ToFloat(depth[3 * size * size + centre]), 0.1f);
            Assert.AreEqual(1000f, HalfFloat.ToFloat(depth[2 * size * size + centre]), 1f);
        }

        [Test]
        public void ShaderRotationKeepsRotationsAndFixesMirrors()
        {
            var q = new Quat(0.2, -0.4, 0.1, 0.9).Normalized.ToMat3();
            var r = Frames.ShaderRotation(q);
            for (int i = 0; i < 3; i++) Assert.Less((r.Column(i) - q.Column(i)).Length, 1e-5f);
            var mirrored = Mat3.FromColumns(-q.Column(0), q.Column(1), q.Column(2));
            var fixedUp = Frames.ShaderRotation(mirrored);
            Assert.Less((fixedUp.Column(0) - q.Column(0)).Length, 1e-5f, "a mirrored basis becomes the proper rotation, like Unity");
        }

        [Test]
        public void QuaternionMatrixRoundTrip()
        {
            var rng = new Random(3);
            for (int i = 0; i < 50; i++)
            {
                var q = new Quat(rng.NextDouble() - .5, rng.NextDouble() - .5, rng.NextDouble() - .5, rng.NextDouble() - .5).Normalized;
                var back = Quat.FromMat3(q.ToMat3());
                double dot = Math.Abs(q.X * back.X + q.Y * back.Y + q.Z * back.Z + q.W * back.W);
                Assert.AreEqual(1.0, dot, 1e-5);
            }
        }

        [Test]
        public void ZUpQuaternionConversionMatchesClosedForm()
        {
            // For Unity = swap(y, z) of a right-handed Z-up frame the converted quaternion is (-x, -z, -y, w).
            var q = new Quat(0.1, -0.3, 0.5, 0.8).Normalized;
            var viaMatrix = SpaceCalibration.WorldFromPano(new[] { q.X, q.Y, q.Z, q.W }, RotationMode.Direct, CoordinateFrame.ZUpRightHanded);
            var closed = new Quat(-q.X, -q.Z, -q.Y, q.W).ToMat3();
            for (int c = 0; c < 3; c++) Assert.Less((viaMatrix.Column(c) - closed.Column(c)).Length, 1e-5f);
        }

        [Test]
        public void RotationYMatchesUnityHandedness()
        {
            // Unity: Quaternion.Euler(0, 90, 0) * Vector3.forward == Vector3.right
            var r = Mat3.RotationY(Math.PI / 2);
            Assert.Less((r * Vec3.Forward - Vec3.Right).Length, 1e-6f);
        }

        [Test]
        public void CanonicalFacesAreProperAndSelectRoundTrips()
        {
            foreach (var b in CubeFaces.Canonical) Assert.IsTrue(b.IsProper, b.ToString());
            Assert.AreEqual(24, new List<FaceBasis>(FaceBasis.All()).FindAll(b => b.IsProper).Count);
            var rng = new Random(5);
            for (int i = 0; i < 500; i++)
            {
                var d = GeometryChecks.RandomDirection(rng);
                int c = CubeFaces.Select(d, out float s, out float t);
                Assert.That(s, Is.InRange(0f, 1f));
                Assert.That(t, Is.InRange(0f, 1f));
                var back = CubeFaces.Direction(c, s, t).Normalized;
                Assert.Less((back - d).Length, 1e-4f);
            }
        }

        [Test]
        public void LayoutLookupInvertsFaceDirections()
        {
            var layout = CubeFaces.DefaultMatterportLayout();
            Assert.IsTrue(CubeFaces.TryBuildLookup(layout, out var slice, out _, out _));
            Assert.AreEqual(0, slice[(int)SignedAxis.PosY], "image 0 should be the up face");
            Assert.AreEqual(5, slice[(int)SignedAxis.NegY], "image 5 should be the down face");
            for (int i = 0; i < 6; i++)
            {
                var d = layout[i].Direction(0.3f, 0.8f);
                int img = CubeFaces.Lookup(layout, d, out float s, out float t);
                Assert.AreEqual(i, img);
                Assert.AreEqual(0.3f, s, 1e-5);
                Assert.AreEqual(0.8f, t, 1e-5);
            }
        }

        [Test]
        public void CalibrationJsonRoundTrip()
        {
            var cal = new SpaceCalibration { Rotation = RotationMode.Inverse, FlipTextureV = true, Method = "solver", Score = 0.8 };
            cal.Faces[0] = new FaceBasis(SignedAxis.PosY, SignedAxis.NegX, SignedAxis.PosZ);
            var back = SpaceCalibration.FromJson(JsonValue.Parse(cal.ToJson().ToJson()));
            Assert.AreEqual(RotationMode.Inverse, back.Rotation);
            Assert.IsTrue(back.FlipTextureV);
            Assert.AreEqual(cal.Faces[0], back.Faces[0]);
            Assert.AreEqual(0.8, back.Score, 1e-9);
        }
    }

    public class BvhTests
    {
        [Test]
        public void MatchesBruteForceRaycasts()
        {
            var space = SyntheticSpace.Generate(new SyntheticOptions { PanoSizes = new[] { 8 }, TextureSize = 8 });
            var geo = space.Geometry;
            var tris = new List<int>();
            foreach (var p in geo.Parts) tris.AddRange(p.Indices);
            var rng = new Random(11);
            int hits = 0;
            for (int i = 0; i < 2000; i++)
            {
                var o = new Vec3((float)rng.NextDouble() * 10, (float)rng.NextDouble() * 2.6f + 0.05f, (float)rng.NextDouble() * 9);
                var d = GeometryChecks.RandomDirection(rng);
                bool h = geo.Bvh.Raycast(o, d, 100f, out var hit);
                float best = float.MaxValue;
                for (int t = 0; t < tris.Count; t += 3)
                {
                    var a = geo.Positions[tris[t]];
                    var e1 = geo.Positions[tris[t + 1]] - a;
                    var e2 = geo.Positions[tris[t + 2]] - a;
                    var p = Vec3.Cross(d, e2);
                    float det = Vec3.Dot(e1, p);
                    if (Math.Abs(det) < 1e-12f) continue;
                    var tv = o - a;
                    float u = Vec3.Dot(tv, p) / det;
                    if (u < 0 || u > 1) continue;
                    var q = Vec3.Cross(tv, e1);
                    float v = Vec3.Dot(d, q) / det;
                    if (v < 0 || u + v > 1) continue;
                    float dist = Vec3.Dot(e2, q) / det;
                    if (dist > 1e-6f && dist < best) best = dist;
                }
                Assert.AreEqual(best < 100f, h, "hit/miss mismatch at ray " + i);
                if (h)
                {
                    hits++;
                    Assert.AreEqual(best, hit.Distance, 1e-3);
                }
            }
            Assert.Greater(hits, 1500);
        }

        [Test]
        public void SyntheticHouseIsWoundInwardsForUnity()
        {
            var space = SyntheticSpace.Generate(new SyntheticOptions { PanoSizes = new[] { 8 }, TextureSize = 8 });
            var sweeps = SweepBuilder.Build(space.Manifest, space.Options.Truth, space.Geometry);
            Assert.Greater(GeometryChecks.FrontFacingFraction(space.Geometry, sweeps), 0.95);
            foreach (var s in sweeps)
            {
                Assert.IsTrue(s.FloorHit);
                Assert.AreEqual(0.01f, s.FloorPoint.Y, 1e-3);
                Assert.Greater(s.Neighbors.Count, 0);
            }
        }
    }

    public class PngTests
    {
        [Test]
        public void EncodesAndDecodes()
        {
            var img = new RgbImage(17, 9);
            var rng = new Random(2);
            rng.NextBytes(img.Pixels);
            var back = Png.Decode(Png.Encode(img));
            Assert.AreEqual(img.Width, back.Width);
            Assert.AreEqual(img.Height, back.Height);
            CollectionAssert.AreEqual(img.Pixels, back.Pixels);
            Assert.IsTrue(ImageInfo.TryReadSize(Png.Encode(img), 64, out int w, out int h));
            Assert.AreEqual(17, w);
            Assert.AreEqual(9, h);
        }
    }
}

namespace Matterportable.Tests
{
    public class DamUvVariantTests
    {
        static byte[] Chunk(int uvField, bool perCorner)
        {
            var xyz = new System.Collections.Generic.List<float> { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 };
            var faces = new ulong[] { 0, 1, 2, 0, 2, 3 };
            var uv = new System.Collections.Generic.List<float>();
            int count = perCorner ? faces.Length : 4;
            for (int i = 0; i < count; i++) { uv.Add(0.1f * i); uv.Add(0.05f * i); }
            var verts = new ProtoWriter();
            verts.PackedFloats(1, xyz);
            verts.PackedFloats(uvField, uv);
            var f = new ProtoWriter();
            f.PackedVarints(1, faces);
            var chunk = new ProtoWriter();
            chunk.Message(1, verts);
            chunk.Message(2, f);
            chunk.String(4, "m_000.jpg");
            var root = new ProtoWriter();
            root.Message(1, chunk);
            return root.ToArray();
        }

        [NUnit.Framework.TestCase(2, false)]
        [NUnit.Framework.TestCase(3, false)]
        [NUnit.Framework.TestCase(4, true)]
        public void FindsUvsInOtherFieldsAndPerCorner(int field, bool perCorner)
        {
            var mesh = DamDecoder.Decode(Chunk(field, perCorner));
            NUnit.Framework.Assert.IsTrue(mesh.HasUvs);
            NUnit.Framework.Assert.AreEqual(2, mesh.TriangleCount);
            if (perCorner) NUnit.Framework.Assert.AreEqual(6, mesh.VertexCount);
            // Third corner of the second triangle uses UV #5 when per-corner, vertex 3's UV otherwise.
            int v = mesh.Parts[0].Indices[5];
            NUnit.Framework.Assert.AreEqual(perCorner ? 0.5f : 0.3f, mesh.Uvs[2 * v], 1e-6);
        }
    }
}
