using System;
using System.IO;
using Matterportable.Core;
using NUnit.Framework;
using UnityEngine;

namespace Matterportable.Tests
{
    /// <summary>Checks that need the Unity engine (run from Window ▸ General ▸ Test Runner ▸ EditMode).</summary>
    public class UnityIntegrationTests
    {
        [Test]
        public void ShadersAreFound()
        {
            foreach (var name in new[] { "Matterportable/Mesh", "Matterportable/Skybox", "Matterportable/Marker", "Hidden/Matterportable/FaceBlit" })
                Assert.IsNotNull(Shader.Find(name), name);
        }

        [Test]
        public void MatrixQuaternionRoundTrip()
        {
            var q = Quaternion.Euler(12, 230, -5);
            var m = q.ToMat3();
            var back = m.ToQuaternion();
            Assert.Less(Quaternion.Angle(q, back), 0.01f);
            var v = new Vector3(0.3f, -0.2f, 0.9f);
            Assert.Less(((q * v) - (m * v.ToCore()).ToUnity()).magnitude, 1e-5f);
        }

        [Test]
        public void CoreRotationYMatchesUnity()
        {
            var unity = Quaternion.AngleAxis(37f, Vector3.up) * Vector3.forward;
            var core = Mat3.RotationY(37 * Math.PI / 180) * Vec3.Forward;
            Assert.Less((unity - core.ToUnity()).magnitude, 1e-5f);
        }

        [Test]
        public void DecodedImagesAreBottomUpLikeCore()
        {
            // Bottom-left pixel white, everything else black; PNG written by the Core encoder (top-down file rows).
            var img = new RgbImage(4, 4);
            img.Set(0, 0, 255, 255, 255);
            var path = Path.Combine(Path.GetTempPath(), "mpt-orientation-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(path, Png.Encode(img));
                var luma = MpUtil.LoadLuma(path);
                Assert.Greater(luma[0, 0], 0.9f, "Unity's decoder should put the bottom-left pixel at index 0");
                Assert.Less(luma[0, 3], 0.1f);
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void EditModePreviewBuildsDemoSpace()
        {
            var root = Path.Combine(Path.GetTempPath(), "mpt-cache-" + Guid.NewGuid().ToString("N"));
            var go = new GameObject("test space");
            try
            {
                var synth = SyntheticSpace.Generate(new SyntheticOptions { PanoSizes = new[] { 16 }, TextureSize = 16 });
                synth.WriteTo(CacheLayout.SpaceDir(root, synth.Manifest.ModelId));
                var space = go.AddComponent<MatterportSpace>();
                space.cacheFolder = root;
                space.link = synth.Manifest.ModelId;
                space.BuildEditorPreview();
                Assert.IsNull(space.Error, space.Error);
                Assert.IsNotNull(space.MeshObject);
                Assert.AreEqual(synth.Manifest.Sweeps.Count, space.Sweeps.Count);
                Assert.AreEqual(synth.Geometry.Parts.Count, space.MeshObject.GetComponent<MeshFilter>().sharedMesh.subMeshCount);
                // Sweep positions survive the round trip through files and frame conversion.
                Assert.Less((space.SweepWorldPosition(0) - new Vector3(1.2f, 1.55f, 1.2f)).magnitude, 1e-3f);
                Assert.IsTrue(space.Raycast(new Ray(space.SweepWorldPosition(0), Vector3.down), 5f, out var p, out var n, out _));
                Assert.AreEqual(0f, p.y, 1e-3f);
                Assert.Greater(n.y, 0.9f, "floors must face up after conversion");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }
    }
}
