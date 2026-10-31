using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Matterportable.Core
{
    /// <summary>
    /// End-to-end calibration of a downloaded space folder: geometry checks, then the panorama solver.
    /// Image decoding is injected so the same code runs in Unity (Texture2D) and offline (PNG decoder).
    /// </summary>
    public static class CalibrationPipeline
    {
        public const int SolverSweeps = 8;
        public const int TextureSize = 256;
        public const int PanoSampleSize = 32;

        /// <summary>
        /// Gathers solver input. Calls <paramref name="loadLuma"/> on the calling thread (Unity: main thread).
        /// </summary>
        public static AlignmentInput LoadInput(string spaceDir, SpaceManifest manifest, MeshData mesh, SpaceCalibration cal,
            Func<string, LumaImage> loadLuma, ILogSink log)
        {
            var raw = cal.Clone();
            raw.FlipTextureV = false; // the solver tests both conventions itself
            var input = new AlignmentInput { Geometry = SpaceGeometry.Build(mesh, raw), SweepFrame = cal.SweepFrame };

            foreach (var t in manifest.Textures)
            {
                var path = CacheLayout.Resolve(spaceDir, t.File);
                if (!File.Exists(path)) continue;
                try
                {
                    var img = loadLuma(path);
                    if (img == null) continue;
                    int size = Math.Min(TextureSize, Math.Max(img.Width, img.Height));
                    input.Textures[t.Material ?? ""] = img.Width > size || img.Height > size ? img.Resize(size, size) : img;
                }
                catch (Exception e) { log?.Warn($"Could not read texture {t.File}: {e.Message}"); }
            }

            var sweeps = SweepBuilder.Build(manifest, cal, input.Geometry);
            foreach (int i in PanoAlignmentSolver.PickSweeps(manifest.Sweeps.Count, SolverSweeps))
            {
                var e = manifest.Sweeps[i];
                var tier = e.SmallestTier;
                if (tier == null || tier.Tiles != 1) continue;
                var faces = new LumaImage[6];
                bool ok = true;
                for (int f = 0; f < 6 && ok; f++)
                {
                    var path = CacheLayout.Resolve(spaceDir, tier.FacePath(f));
                    try
                    {
                        var img = File.Exists(path) ? loadLuma(path) : null;
                        if (img == null) { ok = false; break; }
                        faces[f] = img.Width > PanoSampleSize ? img.Resize(PanoSampleSize, PanoSampleSize) : img;
                    }
                    catch (Exception ex)
                    {
                        log?.Warn($"Could not read {tier.FacePath(f)}: {ex.Message}");
                        ok = false;
                    }
                }
                if (!ok) continue;
                input.Sweeps.Add(new AlignmentSweepInput { SweepIndex = i, Position = sweeps[i].Position, Rotation = e.Rotation, Faces = faces });
            }
            return input;
        }

        /// <summary>Runs the full calibration and saves calibration.json. Returns the new calibration.</summary>
        public static SpaceCalibration Run(string spaceDir, Func<string, LumaImage> loadLuma, ILogSink log, CancellationToken ct)
        {
            var manifest = SpaceManifest.Load(spaceDir);
            var cal = SpaceCalibration.LoadOrDefault(spaceDir);
            if (cal.Method == "manual")
            {
                log?.Info("calibration.json is marked manual; leaving it unchanged.");
                return cal;
            }
            if (manifest.Mesh == null)
            {
                log?.Warn("No mesh: panoramas will use the default face layout.");
                return cal;
            }
            var mesh = DamDecoder.Decode(File.ReadAllBytes(CacheLayout.Resolve(spaceDir, manifest.Mesh.File)), log);
            var fresh = new SpaceCalibration();
            fresh = SpaceCalibrator.CheckGeometry(manifest, mesh, fresh, log);
            ct.ThrowIfCancellationRequested();
            var input = LoadInput(spaceDir, manifest, mesh, fresh, loadLuma, log);
            var result = PanoAlignmentSolver.Solve(input, log, ct);
            var solved = SpaceCalibrator.Apply(fresh, result, log);
            solved.Save(spaceDir);
            return solved;
        }

        /// <summary>Offline loader for PNG-based spaces (synthetic data, tests, CLI).</summary>
        public static LumaImage LoadPngLuma(string path) => Png.Decode(File.ReadAllBytes(path)).ToLuma();
    }
}
