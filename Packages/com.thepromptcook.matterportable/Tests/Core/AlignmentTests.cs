using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Matterportable.Core;
using NUnit.Framework;

namespace Matterportable.Tests
{
    public class AlignmentTests
    {
        static SyntheticSpace Make(SpaceCalibration truth, int seed = 7) =>
            SyntheticSpace.Generate(new SyntheticOptions { Truth = truth, Seed = seed, PanoSizes = new[] { 32 }, TextureSize = 64 });

        /// <summary>True if two calibrations send every panorama pixel of every sweep in the same world direction.</summary>
        static void AssertSameMapping(SyntheticSpace space, SpaceCalibration truth, AlignmentResult r)
        {
            Assert.IsTrue(r.Success, string.Join("\n", r.Report));
            Assert.AreEqual(truth.FlipTextureV, r.FlipTextureV, "texture V convention");
            foreach (var s in space.Manifest.Sweeps)
            {
                var wt = SpaceCalibration.WorldFromPano(s.Rotation, truth.Rotation, truth.SweepFrame);
                var wr = SpaceCalibration.WorldFromPano(s.Rotation, r.Rotation, truth.SweepFrame);
                for (int i = 0; i < 6; i++)
                foreach (var st in new[] { (0.2f, 0.3f), (0.8f, 0.6f), (0.5f, 0.9f) })
                {
                    var a = (wt * truth.Faces[i].Direction(st.Item1, st.Item2)).Normalized;
                    var b = (wr * r.Faces[i].Direction(st.Item1, st.Item2)).Normalized;
                    Assert.Less((a - b).Length, 1e-3f, $"sweep {s.Id} face {i}: {truth.Faces[i]} vs {r.Faces[i]}\n" + string.Join("\n", r.Report));
                }
            }
        }

        [Test]
        public void RecoversDefaultMatterportConvention()
        {
            var truth = new SpaceCalibration();
            var space = Make(truth);
            var r = PanoAlignmentSolver.Solve(space.ToAlignmentInput(new SpaceCalibration()), null, CancellationToken.None);
            AssertSameMapping(space, truth, r);
            Assert.Greater(r.Score, 0.5);
            Assert.Greater(r.Margin, 0.05);
            Assert.IsFalse(r.Mirrored);
        }

        [TestCase(1, RotationMode.Inverse, false)]
        [TestCase(2, RotationMode.Direct, true)]
        [TestCase(3, RotationMode.Identity, false)]
        [TestCase(4, RotationMode.Inverse, true)]
        public void RecoversRandomConventions(int seed, RotationMode mode, bool flipV)
        {
            var rng = new Random(seed);
            // Random face order and per-face orientation (any proper basis per face, distinct directions).
            var axes = new List<int> { 0, 1, 2, 3, 4, 5 };
            var faces = new FaceBasis[6];
            for (int i = 0; i < 6; i++)
            {
                int pick = rng.Next(axes.Count);
                var forward = (SignedAxis)axes[pick];
                axes.RemoveAt(pick);
                var options = new List<FaceBasis>(FaceBasis.All()).FindAll(b => b.IsProper && b.Forward == forward);
                faces[i] = options[rng.Next(options.Count)];
            }
            var truth = new SpaceCalibration { Rotation = mode, FlipTextureV = flipV, Faces = faces };
            var space = Make(truth, seed);
            var r = PanoAlignmentSolver.Solve(space.ToAlignmentInput(new SpaceCalibration()), null, CancellationToken.None);
            AssertSameMapping(space, truth, r);
        }

        [Test]
        public void GeometryCheckKeepsCorrectFramesAndFixesInvertedWinding()
        {
            var truth = new SpaceCalibration { FlipWinding = true };
            var space = Make(truth);
            var cal = SpaceCalibrator.CheckGeometry(space.Manifest, space.SourceMesh, new SpaceCalibration(), null);
            Assert.AreEqual(CoordinateFrame.ZUpRightHanded, cal.MeshFrame);
            Assert.AreEqual(CoordinateFrame.ZUpRightHanded, cal.SweepFrame);
            Assert.IsTrue(cal.FlipWinding);
        }

        [Test]
        public void GeometryCheckDetectsYUpData()
        {
            var truth = new SpaceCalibration { MeshFrame = CoordinateFrame.YUpRightHanded, SweepFrame = CoordinateFrame.YUpRightHanded };
            var space = Make(truth);
            var cal = SpaceCalibrator.CheckGeometry(space.Manifest, space.SourceMesh, new SpaceCalibration(), null);
            Assert.AreEqual(CoordinateFrame.YUpRightHanded, cal.MeshFrame);
            Assert.AreEqual(CoordinateFrame.YUpRightHanded, cal.SweepFrame);
            Assert.IsFalse(cal.FlipWinding);
        }

        [Test]
        public void FullPipelineFromFilesOnDisk()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mpt-align-" + Guid.NewGuid().ToString("N"));
            try
            {
                var truth = new SpaceCalibration { Rotation = RotationMode.Inverse };
                var space = SyntheticSpace.Generate(new SyntheticOptions { Truth = truth, PanoSizes = new[] { 32, 64 }, TextureSize = 64 });
                space.WriteTo(dir, writeCalibration: false);
                var cal = CalibrationPipeline.Run(dir, CalibrationPipeline.LoadPngLuma, null, CancellationToken.None);
                Assert.AreEqual("solver", cal.Method, string.Join("\n", cal.Notes));
                var reloaded = SpaceCalibration.LoadOrDefault(dir);
                Assert.AreEqual(cal.Rotation, reloaded.Rotation);
                var r = new AlignmentResult { Success = true, Rotation = cal.Rotation, Faces = cal.Faces, FlipTextureV = cal.FlipTextureV };
                AssertSameMapping(space, truth, r);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
