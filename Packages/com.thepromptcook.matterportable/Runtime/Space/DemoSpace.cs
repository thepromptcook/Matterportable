using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Matterportable.Core;

namespace Matterportable
{
    /// <summary>
    /// The offline demo: a procedural house written in exactly the cache format of a downloaded Matterport space,
    /// so the whole pipeline can be tried without network access. Used by the launcher, the import window and tests.
    /// </summary>
    public static class DemoSpace
    {
        public const string ModelId = "DemoSpace01";

        /// <summary>
        /// Generates the demo (if missing or <paramref name="regenerate"/>) and returns its folder. Await on the main
        /// thread: rendering runs on a worker, JPEG encoding uses Unity's encoder.
        /// </summary>
        public static async Task<string> EnsureAsync(string cacheFolder, int faceSize, bool regenerate, CancellationToken ct)
        {
            var dir = MatterportPaths.SpaceDir(cacheFolder, ModelId);
            if (!regenerate && SpaceManifest.Exists(dir)) return dir;
            var options = new SyntheticOptions
            {
                ModelId = ModelId,
                PanoSizes = new[] { Math.Max(64, faceSize / 4), faceSize },
                TextureSize = 512,
                ImageExtension = "jpg",
            };
            var space = await Task.Run(() => SyntheticSpace.Generate(options), ct);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            space.WriteTo(dir, img => MpUtil.EncodeJpg(img, 92));
            return dir;
        }
    }
}
