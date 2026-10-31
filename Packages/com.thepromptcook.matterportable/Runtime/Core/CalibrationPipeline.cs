using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Matterportable.Core
{
    /// <summary>
    /// End-to-end calibration of a downloaded space folder: geometry checks (frames, winding), then the panorama
    /// solver; saves calibration.json. Image decoding is injected so the same code runs in Unity (Texture2D,
    /// see CalibrationRunner) and offline (PNG decoder: CLI and tests).
    /// </summary>
    public static class CalibrationPipeline
    {
        public const int SolverSweeps = 8;
        public const int TextureSize = 256;
        public const int PanoSampleSize = 32;

        /// <summary>Sweeps whose smallest panorama tier the solver compares against the mesh.</summary>
        public static List<int> SolverSweepIndices(SpaceManifest manifest)
        {
            var result = new List<int>();
            foreach (int i in PanoAlignmentSolver.PickSweeps(manifest.Sweeps.Count, SolverSweeps))
            {
                var tier = manifest.Sweeps[i].SmallestTier;
                if (tier != null && tier.Tiles == 1) result.Add(i);
            }
            return result;
        }

        /// <summary>Absolute paths of every image the solver reads (mesh textures + solver sweep faces).</summary>
        public static List<string> RequiredImages(string spaceDir, SpaceManifest manifest)
        {
            var paths = new List<string>();
            foreach (var t in manifest.Textures) paths.Add(CacheLayout.Resolve(spaceDir, t.File));
            foreach (int i in SolverSweepIndices(manifest))
                for (int f = 0; f < 6; f++) paths.Add(CacheLayout.Resolve(spaceDir, manifest.Sweeps[i].SmallestTier.FacePath(f)));
            return paths;
        }

        /// <summary>Gathers solver input; <paramref name="loadLuma"/> returns null for unreadable files.</summary>
        public static AlignmentInput LoadInput(string spaceDir, SpaceManifest manifest, MeshData mesh, SpaceCalibration cal,
            Func<string, LumaImage> loadLuma, ILogSink log)
        {
            var raw = cal.Clone();
            raw.FlipTextureV = false; // the solver tests both conventions itself
            var input = new AlignmentInput { Geometry = SpaceGeometry.Build(mesh, raw), SweepFrame = cal.SweepFrame };

            foreach (var t in manifest.Textures)
            {
                var img = TryLoad(CacheLayout.Resolve(spaceDir, t.File), loadLuma, log);
                if (img == null) continue;
                int size = Math.Min(TextureSize, Math.Max(img.Width, img.Height));
                input.Textures[t.Material ?? ""] = img.Width > size || img.Height > size ? img.Resize(size, size) : img;
            }

            var sweeps = SweepBuilder.Build(manifest, cal, input.Geometry);
            foreach (int i in SolverSweepIndices(manifest))
            {
                var e = manifest.Sweeps[i];
                var faces = new LumaImage[6];
                for (int f = 0; f < 6; f++)
                {
                    var img = TryLoad(CacheLayout.Resolve(spaceDir, e.SmallestTier.FacePath(f)), loadLuma, log);
                    if (img == null) { faces = null; break; }
                    faces[f] = img.Width > PanoSampleSize ? img.Resize(PanoSampleSize, PanoSampleSize) : img;
                }
                if (faces != null)
                    input.Sweeps.Add(new AlignmentSweepInput { SweepIndex = i, Position = sweeps[i].Position, Rotation = e.Rotation, Faces = faces });
            }
            return input;
        }

        static LumaImage TryLoad(string path, Func<string, LumaImage> loadLuma, ILogSink log)
        {
            if (!File.Exists(path)) return null;
            try { return loadLuma(path); }
            catch (Exception e)
            {
                log?.Warn($"Could not read {Path.GetFileName(path)}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Runs the full calibration and saves calibration.json. A manual calibration is kept unless
        /// <paramref name="force"/> is set.
        /// </summary>
        public static SpaceCalibration Run(string spaceDir, Func<string, LumaImage> loadLuma, ILogSink log, CancellationToken ct, bool force = false)
        {
            var manifest = SpaceManifest.Load(spaceDir);
            var existing = SpaceCalibration.LoadOrDefault(spaceDir);
            if (existing.Method == SpaceCalibration.Manual && !force)
            {
                log?.Info("calibration.json is marked manual; leaving it unchanged.");
                return existing;
            }
            if (manifest.Mesh == null)
            {
                log?.Warn("No mesh: panoramas use the default face layout.");
                return existing;
            }
            var bytes = File.ReadAllBytes(CacheLayout.Resolve(spaceDir, manifest.Mesh.File));
            var mesh = DamDecoder.Decode(bytes, log);
            if (!mesh.HasUvs)
            {
                DamDecoder.WriteDiagnostics(spaceDir, bytes);
                log?.Warn("The mesh has no texture coordinates; wrote debug/dam-structure.txt for diagnosis.");
            }
            var cal = SpaceCalibrator.CheckGeometry(manifest, mesh, new SpaceCalibration(), log);
            ct.ThrowIfCancellationRequested();
            var input = LoadInput(spaceDir, manifest, mesh, cal, loadLuma, log);
            var solved = SpaceCalibrator.Apply(cal, PanoAlignmentSolver.Solve(input, log, ct), log);
            solved.Save(spaceDir);
            return solved;
        }

        /// <summary>Offline loader for PNG-based spaces (synthetic data, tests, CLI).</summary>
        public static LumaImage LoadPngLuma(string path) => Png.Decode(File.ReadAllBytes(path)).ToLuma();
    }
}
