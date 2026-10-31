using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Matterportable.Core;

namespace Matterportable
{
    /// <summary>
    /// Runs <see cref="CalibrationPipeline"/> inside Unity: images are decoded on the main thread (Unity's decoder),
    /// everything else (mesh decode, BVH, geometry checks, solver) on a worker thread.
    /// </summary>
    public static class CalibrationRunner
    {
        /// <summary>Main thread: decodes the textures and the low-resolution faces the solver will use.</summary>
        public static Dictionary<string, LumaImage> DecodeInputs(string spaceDir, SpaceManifest manifest)
        {
            var images = new Dictionary<string, LumaImage>();
            void Add(string rel)
            {
                var path = CacheLayout.Resolve(spaceDir, rel);
                if (images.ContainsKey(path) || !File.Exists(path)) return;
                var img = MpUtil.LoadLuma(path);
                if (img == null) return;
                // Keep memory low: the solver only needs small images.
                int max = CalibrationPipeline.TextureSize;
                if (img.Width > max || img.Height > max) img = img.Resize(Math.Min(max, img.Width), Math.Min(max, img.Height));
                images[path] = img;
            }

            foreach (var t in manifest.Textures) Add(t.File);
            foreach (int i in PanoAlignmentSolver.PickSweeps(manifest.Sweeps.Count, CalibrationPipeline.SolverSweeps))
            {
                var tier = manifest.Sweeps[i].SmallestTier;
                if (tier == null || tier.Tiles != 1) continue;
                for (int f = 0; f < 6; f++) Add(tier.FacePath(f));
            }
            return images;
        }

        /// <summary>Worker thread: geometry checks + panorama solver; saves calibration.json and returns it.</summary>
        public static Task<SpaceCalibration> SolveAsync(string spaceDir, SpaceManifest manifest, Dictionary<string, LumaImage> images,
            ILogSink log, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var damBytes = File.ReadAllBytes(CacheLayout.Resolve(spaceDir, manifest.Mesh.File));
                var mesh = DamDecoder.Decode(damBytes, log);
                if (!mesh.HasUvs)
                {
                    DamDecoder.WriteDiagnostics(spaceDir, damBytes);
                    log?.Warn("The mesh has no texture coordinates; wrote debug/dam-structure.txt for diagnosis.");
                }
                var cal = SpaceCalibrator.CheckGeometry(manifest, mesh, new SpaceCalibration(), log);
                ct.ThrowIfCancellationRequested();
                var input = CalibrationPipeline.LoadInput(spaceDir, manifest, mesh, cal,
                    path => images.TryGetValue(path, out var img) ? img : null, log);
                var result = PanoAlignmentSolver.Solve(input, log, ct);
                var solved = SpaceCalibrator.Apply(cal, result, log);
                solved.Save(spaceDir);
                return solved;
            }, ct);
        }
    }
}
