using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Matterportable.Core;
using NUnit.Framework;

namespace Matterportable.Tests
{
    public class GraphQLTests
    {
        static GqlDocument Sample()
        {
            var root = new GqlField("model", false, "id: \"abc\"");
            root.Required("id");
            root.Add(new GqlField("assets").Add(new GqlField("meshes").Leaves("url", "format")),
                new GqlField("locations").Add(new GqlField("pano").Leaves("id")));
            return new GqlDocument("Q", root);
        }

        [Test]
        public void RendersOneFieldPerLineAndFindsByLine()
        {
            var doc = Sample();
            var text = doc.Render();
            var lines = text.Split('\n');
            Assert.AreEqual("query Q {", lines[0]);
            Assert.AreEqual("  model(id: \"abc\") {", lines[1]);
            for (int i = 0; i < lines.Length; i++)
            {
                var name = lines[i].Trim().TrimEnd('{').Trim();
                if (name == "" || name == "}" || i == 0) continue;
                var f = doc.FindByLine(i + 1);
                Assert.IsNotNull(f, "line " + (i + 1));
                StringAssert.StartsWith(f.Name, name);
            }
        }

        [Test]
        public void RemovingLastChildRemovesOptionalParent()
        {
            var doc = Sample();
            var pano = doc.FindByPath("locations/pano/id");
            Assert.IsTrue(doc.TryRemove(pano, out var removed));
            Assert.AreEqual("model/locations", removed);
            Assert.IsNull(doc.FindByPath("locations"));
            Assert.IsFalse(doc.TryRemove(doc.FindByPath("id"), out _), "required fields must not be pruned");
        }

        [Test]
        public void SelfHealingPrunesUnknownFields()
        {
            var fake = new FakeMatterport(SyntheticSpace.Generate(new SyntheticOptions { PanoSizes = new[] { 32 }, TextureSize = 32 }));
            var client = new MatterportGraphClient(fake, NullLog.Instance);
            var outcome = client.FetchModelAsync("DemoSpace01", CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsTrue(outcome.Data["model"]["locations"].Count > 0);
            CollectionAssert.Contains(outcome.PrunedFields, "model/assets/tilesets");
            CollectionAssert.Contains(outcome.PrunedFields, "model/locations/position");
            CollectionAssert.DoesNotContain(outcome.PrunedFields, "model/locations/pano/skyboxes/urlTemplate");
            Assert.LessOrEqual(outcome.Attempts, 4, "errors for all fields of one round should be pruned together");
        }

        [Test]
        public void DetectsBotChallengePages()
        {
            Assert.IsTrue(MatterportGraphClient.IsBotChallenge(403, "<html><title>Just a moment...</title>"));
            Assert.IsFalse(MatterportGraphClient.IsBotChallenge(200, "{\"data\":{}}"));
        }
    }

    public class DownloaderTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mpt-test-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        static SyntheticSpace SmallSpace() =>
            SyntheticSpace.Generate(new SyntheticOptions { PanoSizes = new[] { 32, 64 }, TextureSize = 64 });

        [Test]
        public void DownloadsWholeSpaceIntoCacheLayout()
        {
            var space = SmallSpace();
            var fake = new FakeMatterport(space);
            var downloader = new SpaceDownloader(fake, NullLog.Instance);
            var manifest = downloader.DownloadAsync("DemoSpace01", _dir,
                new DownloadOptions { PanoSizes = new[] { 32, 64 }, MaxConcurrency = 4 }, CancellationToken.None).GetAwaiter().GetResult();

            Assert.AreEqual(space.Manifest.Sweeps.Count, manifest.Sweeps.Count, "unplaced locations are skipped");
            Assert.IsNotNull(manifest.Mesh);
            Assert.AreEqual(space.Manifest.Textures.Count, manifest.Textures.Count);
            foreach (var s in manifest.Sweeps)
            {
                Assert.AreEqual(2, s.Tiers.Count);
                Assert.AreEqual(32, s.Tiers[0].Size);
                Assert.AreEqual(64, s.Tiers[1].Size);
                for (int f = 0; f < 6; f++) Assert.IsTrue(File.Exists(CacheLayout.Resolve(_dir, s.Tiers[1].FacePath(f))));
                Assert.IsTrue(s.Neighbors.Count > 0);
            }
            var reloaded = SpaceManifest.Load(_dir);
            Assert.AreEqual(manifest.Sweeps.Count, reloaded.Sweeps.Count);
            Assert.AreEqual(space.Manifest.Sweeps[3].Position[2], reloaded.Sweeps[3].Position[2], 1e-6);

            var mesh = DamDecoder.Decode(File.ReadAllBytes(CacheLayout.Resolve(_dir, reloaded.Mesh.File)));
            Assert.AreEqual(space.SourceMesh.TriangleCount, mesh.TriangleCount);

            // Second run is a no-op (everything cached).
            int gets = fake.Gets;
            new SpaceDownloader(fake, NullLog.Instance).DownloadAsync("DemoSpace01", _dir,
                new DownloadOptions { PanoSizes = new[] { 32, 64 } }, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(gets, fake.Gets);
        }

        [Test]
        public void DiagnosticsReportSummarisesAndRedactsTokens()
        {
            var fake = new FakeMatterport(SmallSpace());
            new SpaceDownloader(fake, NullLog.Instance).DownloadAsync("DemoSpace01", _dir,
                new DownloadOptions { PanoSizes = new[] { 32 } }, CancellationToken.None).GetAwaiter().GetResult();
            var report = Diagnostics.Report(_dir, "test");
            StringAssert.Contains("Matterportable demo house (DemoSpace01)", report);
            StringAssert.Contains("panorama tier 32px", report);
            StringAssert.Contains("firstLocation", report);
            StringAssert.DoesNotContain("?t=t1", report, "signed tokens must be redacted");
        }

        [Test]
        public void RefreshesExpiredLinksAndRetries()
        {
            var space = SmallSpace();
            var fake = new FakeMatterport(space) { RotateTokenAfterGets = 10 };
            var log = new BufferLog();
            var downloader = new SpaceDownloader(fake, log);
            var manifest = downloader.DownloadAsync("DemoSpace01", _dir,
                new DownloadOptions { PanoSizes = new[] { 32 }, MaxConcurrency = 2 }, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(space.Manifest.Sweeps.Count, manifest.Sweeps.Count, log.Text());
            Assert.AreEqual(0, manifest.Warnings.Count, string.Join("\n", manifest.Warnings));
            StringAssert.Contains("refreshing", log.Text());
        }

        [Test]
        public void TextureUrlCandidatesUseTheIndex()
        {
            var c = SpaceDownloader.TextureUrlCandidates("https://x/m_50k_<texture>.jpg?t=1", "m_50k_003.jpg");
            Assert.AreEqual("https://x/m_50k_003.jpg?t=1", c[0]);
            CollectionAssert.Contains(c, "https://x/m_50k_3.jpg?t=1");
        }

        [Test]
        public void FillsFaceAndTilePlaceholders()
        {
            Assert.AreEqual("a/s_skybox4.jpg?t=x", SpaceDownloader.Fill("a/s_skybox<face>.jpg?t=x", 4, 0, 0));
            Assert.AreEqual("a/2k_face1_3_2.jpg", SpaceDownloader.Fill("a/2k_face<face>_<x>_<y>.jpg", 1, 3, 2));
        }
    }
}
