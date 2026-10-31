using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Matterportable.Core;

namespace Matterportable
{
    /// <summary>
    /// Runs <see cref="CalibrationPipeline"/> inside Unity: the images it needs are decoded on the main thread with
    /// Unity's decoder, everything else (mesh decode, BVH, geometry checks, solver) runs on a worker thread.
    /// </summary>
    public static class CalibrationRunner
    {
        /// <summary>
        /// Call on the main thread. Returns the task solving (and saving) the space's calibration.
        /// A manual calibration is kept unless <paramref name="force"/> is set.
        /// </summary>
        public static Task<SpaceCalibration> RunAsync(string spaceDir, ILogSink log, CancellationToken ct, bool force = false)
        {
            var images = DecodeInputs(spaceDir, SpaceManifest.Load(spaceDir));
            return Task.Run(() => CalibrationPipeline.Run(spaceDir, path => images.TryGetValue(path, out var img) ? img : null, log, ct, force), ct);
        }

        static Dictionary<string, LumaImage> DecodeInputs(string spaceDir, SpaceManifest manifest)
        {
            var images = new Dictionary<string, LumaImage>();
            int max = CalibrationPipeline.TextureSize;
            foreach (var path in CalibrationPipeline.RequiredImages(spaceDir, manifest))
            {
                if (images.ContainsKey(path) || !File.Exists(path)) continue;
                var img = MpUtil.LoadLuma(path);
                if (img == null) continue;
                // The solver only needs small images; keep memory low.
                images[path] = img.Width > max || img.Height > max ? img.Resize(Math.Min(max, img.Width), Math.Min(max, img.Height)) : img;
            }
            return images;
        }
    }
}
