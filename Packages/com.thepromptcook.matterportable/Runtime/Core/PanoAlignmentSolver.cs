using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Matterportable.Core
{
    public sealed class AlignmentSweepInput
    {
        public int SweepIndex;
        /// <summary>Camera position in Unity space.</summary>
        public Vec3 Position;
        /// <summary>Raw quaternion as stored in the manifest (may be null).</summary>
        public double[] Rotation;
        /// <summary>The six face images in source order (any square size).</summary>
        public LumaImage[] Faces;
    }

    public sealed class AlignmentInput
    {
        /// <summary>Geometry built with FlipTextureV = false (raw UVs).</summary>
        public SpaceGeometry Geometry;
        /// <summary>Mesh texture luminance by material name.</summary>
        public Dictionary<string, LumaImage> Textures = new Dictionary<string, LumaImage>();
        public List<AlignmentSweepInput> Sweeps = new List<AlignmentSweepInput>();
        public CoordinateFrame SweepFrame = CoordinateFrame.ZUpRightHanded;
    }

    public sealed class AlignmentResult
    {
        public bool Success;
        public RotationMode Rotation;
        public FaceBasis[] Faces;
        public bool FlipTextureV;
        public bool Mirrored;
        /// <summary>Mean normalised cross-correlation between panoramas and the textured mesh (-1..1).</summary>
        public double Score;
        /// <summary>Lead over the best alternative hypothesis.</summary>
        public double Margin;
        public int SweepsUsed;
        public List<string> Report = new List<string>();
    }

    /// <summary>
    /// Determines how panorama face images map to world directions by comparing them against the textured mesh
    /// as seen from each sweep. It searches sweep-rotation conventions, all 48 axis-aligned orientations of every
    /// face image, and the texture V convention, and keeps the hypothesis whose views correlate best.
    /// This removes any dependence on guessed face order/orientation conventions.
    /// </summary>
    public static class PanoAlignmentSolver
    {
        public const int FaceSamples = 16;
        public const int GroundTruthSize = 32;
        const float MaxRayDistance = 40f;

        static readonly RotationMode[] Modes = { RotationMode.Direct, RotationMode.Inverse, RotationMode.Identity };

        sealed class Cube
        {
            public readonly float[][] Value = new float[6][];
            public readonly bool[][] Valid = new bool[6][];
            public int ValidCount;

            public Cube()
            {
                int n = GroundTruthSize * GroundTruthSize;
                for (int f = 0; f < 6; f++)
                {
                    Value[f] = new float[n];
                    Valid[f] = new bool[n];
                }
            }

            public bool Lookup(Vec3 d, out float v)
            {
                int c = CubeFaces.Select(d, out float s, out float t);
                int x = Math.Min(GroundTruthSize - 1, Math.Max(0, (int)(s * GroundTruthSize)));
                int y = Math.Min(GroundTruthSize - 1, Math.Max(0, (int)(t * GroundTruthSize)));
                int i = y * GroundTruthSize + x;
                v = Value[c][i];
                return Valid[c][i];
            }
        }

        public static AlignmentResult Solve(AlignmentInput input, ILogSink log, CancellationToken ct)
        {
            var result = new AlignmentResult();
            var geo = input.Geometry;
            if (geo == null || !geo.HasUvs || input.Textures.Count == 0)
            {
                result.Report.Add("No textured mesh available; cannot solve panorama alignment.");
                return result;
            }

            // Ground-truth views of the textured mesh from each sweep, for both V conventions.
            var sweeps = new List<AlignmentSweepInput>();
            var truth = new List<Cube[]>();
            foreach (var s in input.Sweeps)
            {
                ct.ThrowIfCancellationRequested();
                if (s.Faces == null || s.Faces.Length != 6) continue;
                var cubes = RenderTruth(geo, input.Textures, s.Position);
                int minValid = 6 * GroundTruthSize * GroundTruthSize / 3;
                if (cubes[0].ValidCount < minValid)
                {
                    result.Report.Add($"Sweep {s.SweepIndex}: mesh covers too little of the view; skipped.");
                    continue;
                }
                sweeps.Add(s);
                truth.Add(cubes);
            }
            result.SweepsUsed = sweeps.Count;
            if (sweeps.Count == 0)
            {
                result.Report.Add("No usable sweeps for alignment.");
                return result;
            }

            // Panorama samples: N x N per face, bottom-up rows.
            int nSamples = FaceSamples * FaceSamples;
            var pano = new float[sweeps.Count][][];
            for (int si = 0; si < sweeps.Count; si++)
            {
                pano[si] = new float[6][];
                for (int f = 0; f < 6; f++) pano[si][f] = sweeps[si].Faces[f].Resize(FaceSamples, FaceSamples).Pixels;
            }

            // Local sample directions for all 48 candidate bases.
            var bases = new List<FaceBasis>(FaceBasis.All());
            var localDirs = new Vec3[bases.Count][];
            for (int b = 0; b < bases.Count; b++)
            {
                localDirs[b] = new Vec3[nSamples];
                for (int y = 0; y < FaceSamples; y++)
                for (int x = 0; x < FaceSamples; x++)
                    localDirs[b][y * FaceSamples + x] = bases[b].Direction((x + 0.5f) / FaceSamples, (y + 0.5f) / FaceSamples);
            }

            var candidates = new List<(RotationMode mode, bool flip, bool mirrored, double total, FaceBasis[] faces)>();
            foreach (var mode in Modes)
            {
                for (int flipIndex = 0; flipIndex < 2; flipIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    // score[f][b]: weighted mean NCC of source face f under basis b across sweeps.
                    var score = new double[6, bases.Count];
                    var weight = new double[6, bases.Count];
                    Parallel.For(0, bases.Count, b =>
                    {
                        var y = new float[nSamples];
                        var valid = new bool[nSamples];
                        for (int si = 0; si < sweeps.Count; si++)
                        {
                            var w = SpaceCalibration.WorldFromPano(sweeps[si].Rotation, mode, input.SweepFrame);
                            var cube = truth[si][flipIndex];
                            int validCount = 0;
                            for (int k = 0; k < nSamples; k++)
                            {
                                valid[k] = cube.Lookup(w * localDirs[b][k], out y[k]);
                                if (valid[k]) validCount++;
                            }
                            if (validCount < nSamples / 4) continue;
                            for (int f = 0; f < 6; f++)
                            {
                                double ncc = Ncc(pano[si][f], y, valid);
                                score[f, b] += ncc * validCount;
                                weight[f, b] += validCount;
                            }
                        }
                    });

                    foreach (bool mirrored in new[] { false, true })
                    {
                        // best[f][axis] = (score, basis) among bases with that forward axis and handedness.
                        var best = new double[6, 6];
                        var bestBasis = new FaceBasis[6, 6];
                        for (int f = 0; f < 6; f++)
                        for (int a = 0; a < 6; a++) best[f, a] = double.NegativeInfinity;
                        for (int b = 0; b < bases.Count; b++)
                        {
                            if (bases[b].IsProper == mirrored) continue;
                            int a = (int)bases[b].Forward;
                            for (int f = 0; f < 6; f++)
                            {
                                if (weight[f, b] <= 0) continue;
                                double s = score[f, b] / weight[f, b];
                                if (s > best[f, a]) { best[f, a] = s; bestBasis[f, a] = bases[b]; }
                            }
                        }
                        var perm = BestAssignment(best, out double total);
                        if (perm == null) continue;
                        var faces = new FaceBasis[6];
                        for (int f = 0; f < 6; f++) faces[f] = bestBasis[f, perm[f]];
                        candidates.Add((mode, flipIndex == 1, mirrored, total / 6.0, faces));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                result.Report.Add("Alignment search produced no candidates.");
                return result;
            }

            // Prefer Direct > Inverse > Identity and proper faces on near-ties.
            candidates.Sort((p, q) =>
            {
                double d = q.total - p.total;
                if (Math.Abs(d) > 1e-4) return d > 0 ? 1 : -1;
                int pr = Array.IndexOf(Modes, p.mode) + (p.mirrored ? 10 : 0);
                int qr = Array.IndexOf(Modes, q.mode) + (q.mirrored ? 10 : 0);
                return pr.CompareTo(qr);
            });
            var top = candidates[0];
            // Near-tie: prefer Matterport's known convention (identical on every real space tested) over a lookalike.
            var known = CubeFaces.DefaultMatterportLayout();
            bool IsKnown((RotationMode mode, bool flip, bool mirrored, double total, FaceBasis[] faces) c) =>
                c.mode == RotationMode.Direct && !c.flip && !c.mirrored && SameMapping(c.faces, known);
            if (!IsKnown(top))
                foreach (var c in candidates)
                    if (IsKnown(c) && top.total - c.total < KnownConventionPreference)
                    {
                        result.Report.Add($"Within {KnownConventionPreference} of the best score: using Matterport's known convention.");
                        top = c;
                        break;
                    }
            // Margin: lead over the best hypothesis that maps panorama pixels to different directions.
            // (Alternatives differing only in the texture V convention are reported separately.)
            double runnerUp = double.NegativeInfinity, flipRunnerUp = double.NegativeInfinity;
            foreach (var c in candidates)
            {
                if (ReferenceEquals(c.faces, top.faces) && c.mode == top.mode && c.flip == top.flip) continue;
                bool sameMapping = SameMapping(c.faces, top.faces) && EquivalentModes(sweeps, input.SweepFrame, c.mode, top.mode);
                if (!sameMapping) runnerUp = Math.Max(runnerUp, c.total);
                else if (c.flip != top.flip) flipRunnerUp = Math.Max(flipRunnerUp, c.total);
            }

            result.Success = true;
            result.Rotation = top.mode;
            result.Faces = top.faces;
            result.FlipTextureV = top.flip;
            result.Mirrored = top.mirrored;
            result.Score = top.total;
            result.Margin = double.IsNegativeInfinity(runnerUp) ? top.total : top.total - runnerUp;
            double flipMargin = double.IsNegativeInfinity(flipRunnerUp) ? top.total : top.total - flipRunnerUp;
            result.Report.Add($"Best: rotation={top.mode}, flipTextureV={top.flip}, mirrored={top.mirrored}, " +
                              $"score={top.total:0.000}, margin={result.Margin:0.000}, textureV margin={flipMargin:0.000}, sweeps={sweeps.Count}.");
            result.Report.Add("Faces: " + CubeFaces.Describe(top.faces));
            for (int i = 1; i < Math.Min(4, candidates.Count); i++)
                result.Report.Add($"  alt: rotation={candidates[i].mode}, flipV={candidates[i].flip}, mirrored={candidates[i].mirrored}, score={candidates[i].total:0.000}");
            foreach (var line in result.Report) log?.Info("Alignment: " + line);
            return result;
        }

        /// <summary>Score lead a different layout needs over Matterport's known convention to replace it.</summary>
        public const double KnownConventionPreference = 0.05;

        static bool SameMapping(FaceBasis[] a, FaceBasis[] b)
        {
            for (int i = 0; i < 6; i++) if (!a[i].Equals(b[i])) return false;
            return true;
        }

        static bool EquivalentModes(List<AlignmentSweepInput> sweeps, CoordinateFrame frame, RotationMode a, RotationMode b)
        {
            if (a == b) return true;
            foreach (var s in sweeps)
            {
                var ma = SpaceCalibration.WorldFromPano(s.Rotation, a, frame);
                var mb = SpaceCalibration.WorldFromPano(s.Rotation, b, frame);
                for (int c = 0; c < 3; c++)
                    if ((ma.Column(c) - mb.Column(c)).Length > 1e-3f) return false;
            }
            return true;
        }

        /// <summary>Permutation (face → axis) maximising the summed score; brute force over 720 permutations.</summary>
        static int[] BestAssignment(double[,] score, out double total)
        {
            total = double.NegativeInfinity;
            int[] best = null;
            var perm = new[] { 0, 1, 2, 3, 4, 5 };
            do
            {
                double s = 0;
                for (int f = 0; f < 6 && !double.IsNegativeInfinity(s); f++) s += score[f, perm[f]];
                if (s > total)
                {
                    total = s;
                    best = (int[])perm.Clone();
                }
            } while (NextPermutation(perm));
            return double.IsNegativeInfinity(total) ? null : best;
        }

        static bool NextPermutation(int[] a)
        {
            int i = a.Length - 2;
            while (i >= 0 && a[i] >= a[i + 1]) i--;
            if (i < 0) return false;
            int j = a.Length - 1;
            while (a[j] <= a[i]) j--;
            int t = a[i]; a[i] = a[j]; a[j] = t;
            Array.Reverse(a, i + 1, a.Length - i - 1);
            return true;
        }

        static double Ncc(float[] x, float[] y, bool[] valid)
        {
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            int n = 0;
            for (int k = 0; k < x.Length; k++)
            {
                if (!valid[k]) continue;
                double a = x[k], b = y[k];
                sx += a; sy += b; sxx += a * a; syy += b * b; sxy += a * b;
                n++;
            }
            if (n < 8) return 0;
            double cov = sxy - sx * sy / n;
            double vx = sxx - sx * sx / n, vy = syy - sy * sy / n;
            if (vx <= 1e-9 || vy <= 1e-9) return 0;
            return cov / Math.Sqrt(vx * vy);
        }

        /// <summary>Renders the textured mesh's luminance into a world-aligned cube around a point (both V conventions).</summary>
        static Cube[] RenderTruth(SpaceGeometry geo, Dictionary<string, LumaImage> textures, Vec3 origin)
        {
            var cubes = new[] { new Cube(), new Cube() };
            int g = GroundTruthSize;
            var partTex = new LumaImage[geo.Parts.Count];
            for (int p = 0; p < geo.Parts.Count; p++)
                textures.TryGetValue(geo.Parts[p].Material ?? "", out partTex[p]);

            int validA = 0;
            object gate = new object();
            Parallel.For(0, 6 * g, row =>
            {
                int face = row / g, y = row % g;
                int localValid = 0;
                for (int x = 0; x < g; x++)
                {
                    float sumA = 0, sumB = 0;
                    int hits = 0;
                    for (int sub = 0; sub < 4; sub++)
                    {
                        float s = (x + 0.25f + 0.5f * (sub & 1)) / g;
                        float t = (y + 0.25f + 0.5f * (sub >> 1)) / g;
                        var dir = CubeFaces.Direction(face, s, t).Normalized;
                        if (!geo.Bvh.Raycast(origin, dir, MaxRayDistance, out var hit)) continue;
                        var uv = geo.UvAt(hit, out int part);
                        var tex = partTex[part];
                        if (tex == null) continue;
                        sumA += tex.SampleWrapped(uv.X, uv.Y);
                        sumB += tex.SampleWrapped(uv.X, 1f - uv.Y);
                        hits++;
                    }
                    if (hits < 3) continue;
                    int i = y * g + x;
                    cubes[0].Value[face][i] = sumA / hits;
                    cubes[1].Value[face][i] = sumB / hits;
                    cubes[0].Valid[face][i] = cubes[1].Valid[face][i] = true;
                    localValid++;
                }
                lock (gate) validA += localValid;
            });
            cubes[0].ValidCount = cubes[1].ValidCount = validA;
            return cubes;
        }

        /// <summary>Picks up to <paramref name="count"/> sweeps spread evenly through the list.</summary>
        public static List<int> PickSweeps(int total, int count)
        {
            var r = new List<int>();
            if (total <= 0) return r;
            count = Math.Min(count, total);
            for (int i = 0; i < count; i++)
            {
                int idx = (int)Math.Round((i + 0.5) * total / count - 0.5);
                idx = Math.Max(0, Math.Min(total - 1, idx));
                if (!r.Contains(idx)) r.Add(idx);
            }
            return r;
        }
    }

    /// <summary>Calibration pipeline: geometry checks (frames, winding), then the panorama solver.</summary>
    public static class SpaceCalibrator
    {
        /// <summary>
        /// Chooses mesh/sweep coordinate frames that put the cameras inside the mesh above a floor, and fixes
        /// inverted winding. Cheap; needs only the mesh.
        /// </summary>
        public static SpaceCalibration CheckGeometry(SpaceManifest manifest, MeshData mesh, SpaceCalibration current, ILogSink log)
        {
            var cal = current.Clone();
            if (mesh == null || manifest.Sweeps.Count == 0) return cal;

            int upAxis = GeometryChecks.DominantNormalAxis(mesh);
            double bestScore = -1;
            var bestMesh = cal.MeshFrame;
            var bestSweep = cal.SweepFrame;
            SpaceGeometry bestGeo = null;
            foreach (var meshFrame in new[] { cal.MeshFrame, CoordinateFrame.ZUpRightHanded, CoordinateFrame.YUpRightHanded })
            {
                var probe = cal.Clone();
                probe.MeshFrame = meshFrame;
                probe.FlipWinding = false;
                var geo = SpaceGeometry.Build(mesh, probe);
                var sweepFrame = GeometryChecks.BestSweepFrame(manifest, geo, meshFrame == cal.MeshFrame ? cal.SweepFrame : meshFrame, out double score);
                // Small bias towards the configured frame so ties keep the default.
                if (meshFrame == cal.MeshFrame) score += 0.05;
                if (score > bestScore + 1e-9)
                {
                    bestScore = score;
                    bestMesh = meshFrame;
                    bestSweep = sweepFrame;
                    bestGeo = geo;
                }
                if (meshFrame == cal.MeshFrame && score >= 0.85) break;
            }

            if (bestMesh != cal.MeshFrame || bestSweep != cal.SweepFrame)
                log?.Warn($"Coordinate frames adjusted: mesh {Frames.ToToken(cal.MeshFrame)}→{Frames.ToToken(bestMesh)}, " +
                          $"sweeps {Frames.ToToken(cal.SweepFrame)}→{Frames.ToToken(bestSweep)}.");
            cal.MeshFrame = bestMesh;
            cal.SweepFrame = bestSweep;
            cal.Notes.Add($"frames: raw dominant-normal axis {"xyz"[upAxis]}, sweeps-above-floor score {Math.Min(1, bestScore):0.00}");

            var sweeps = SweepBuilder.Build(manifest, cal, bestGeo);
            double front = GeometryChecks.FrontFacingFraction(bestGeo, sweeps);
            if (front < 0.35)
            {
                cal.FlipWinding = !current.FlipWinding;
                log?.Info($"Mesh winding inverted (front-facing {front:P0}); flipping.");
            }
            else cal.FlipWinding = false;
            cal.Notes.Add($"winding: {front:P0} of visible surfaces front-facing before correction");
            return cal;
        }

        /// <summary>Applies a solver result to a calibration if it is trustworthy enough.</summary>
        public static SpaceCalibration Apply(SpaceCalibration current, AlignmentResult r, ILogSink log, double minScore = 0.25)
        {
            var cal = current.Clone();
            if (!r.Success)
            {
                log?.Warn("Panorama alignment could not be solved; using the default layout. " + string.Join(" ", r.Report));
                cal.Method = SpaceCalibration.Unsolved;
                cal.Notes.Add("solver failed: " + string.Join(" ", r.Report));
                return cal;
            }
            if (r.Score < minScore)
            {
                log?.Warn($"Panorama alignment is uncertain (score {r.Score:0.00}); keeping the default layout. " +
                          "Check the view and adjust it in the MatterportSpace inspector if panoramas look misaligned.");
                cal.Method = SpaceCalibration.Unsolved;
                cal.Notes.Add($"solver score {r.Score:0.000} below threshold; default kept");
                cal.Score = r.Score;
                return cal;
            }
            cal.Rotation = r.Rotation;
            cal.Faces = r.Faces;
            cal.FlipTextureV = r.FlipTextureV;
            cal.Method = SpaceCalibration.Solver;
            cal.Score = r.Score;
            cal.Margin = r.Margin;
            cal.SweepsUsed = r.SweepsUsed;
            if (r.Mirrored) cal.Notes.Add("solver chose mirrored faces (unusual; source handedness may differ)");
            cal.Notes.AddRange(r.Report);
            return cal;
        }
    }
}
