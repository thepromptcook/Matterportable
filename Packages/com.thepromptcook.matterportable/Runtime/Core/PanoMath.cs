using System;
using System.Collections.Generic;

namespace Matterportable.Core
{
    /// <summary>How a sweep's stored quaternion maps panorama-local directions to world directions.</summary>
    public enum RotationMode
    {
        /// <summary>Panoramas are already world-aligned; the quaternion is ignored.</summary>
        Identity = 0,
        /// <summary>world = R(q) * local.</summary>
        Direct = 1,
        /// <summary>world = R(q)^-1 * local.</summary>
        Inverse = 2,
    }

    /// <summary>
    /// Orientation of one square panorama face image in the panorama's local frame:
    /// the image centre looks along <see cref="Forward"/>, image +s (right) is <see cref="Right"/>,
    /// image +t (up) is <see cref="Up"/>. Image coordinates are (s, t) in [0,1] with t = 0 at the bottom row.
    /// </summary>
    public struct FaceBasis : IEquatable<FaceBasis>
    {
        public SignedAxis Forward, Right, Up;

        public FaceBasis(SignedAxis forward, SignedAxis right, SignedAxis up)
        {
            Forward = forward;
            Right = right;
            Up = up;
        }

        public Vec3 Direction(float s, float t) =>
            Forward.ToVector() + Right.ToVector() * (2f * s - 1f) + Up.ToVector() * (2f * t - 1f);

        public bool IsValid => Forward.AxisIndex() != Right.AxisIndex() && Forward.AxisIndex() != Up.AxisIndex() &&
                               Right.AxisIndex() != Up.AxisIndex();

        /// <summary>True for a photo-like (non-mirrored) face: Right = Up x Forward in Unity's left-handed frame.</summary>
        public bool IsProper => Vec3.Dot(Vec3.Cross(Up.ToVector(), Forward.ToVector()), Right.ToVector()) > 0;

        public bool Equals(FaceBasis o) => Forward == o.Forward && Right == o.Right && Up == o.Up;
        public override bool Equals(object obj) => obj is FaceBasis b && Equals(b);
        public override int GetHashCode() => (int)Forward * 36 + (int)Right * 6 + (int)Up;
        public override string ToString() => $"F{Forward.Name()} R{Right.Name()} U{Up.Name()}";

        /// <summary>All 48 axis-aligned bases (24 proper + 24 mirrored).</summary>
        public static IEnumerable<FaceBasis> All()
        {
            for (int f = 0; f < 6; f++)
            for (int r = 0; r < 6; r++)
            for (int u = 0; u < 6; u++)
            {
                var b = new FaceBasis((SignedAxis)f, (SignedAxis)r, (SignedAxis)u);
                if (b.IsValid) yield return b;
            }
        }
    }

    /// <summary>
    /// Cube-map math shared by the C# side and the shaders (MatterportCommon.hlsl mirrors <see cref="Select"/>).
    /// Canonical face index c == (int)SignedAxis of the face's forward direction.
    /// </summary>
    public static class CubeFaces
    {
        /// <summary>"Natural photo" orientation of each canonical face (as if looking at it from the centre, upright).</summary>
        public static readonly FaceBasis[] Canonical =
        {
            new FaceBasis(SignedAxis.PosX, SignedAxis.NegZ, SignedAxis.PosY),
            new FaceBasis(SignedAxis.NegX, SignedAxis.PosZ, SignedAxis.PosY),
            new FaceBasis(SignedAxis.PosY, SignedAxis.PosX, SignedAxis.NegZ),
            new FaceBasis(SignedAxis.NegY, SignedAxis.PosX, SignedAxis.PosZ),
            new FaceBasis(SignedAxis.PosZ, SignedAxis.PosX, SignedAxis.PosY),
            new FaceBasis(SignedAxis.NegZ, SignedAxis.NegX, SignedAxis.PosY),
        };

        /// <summary>
        /// Best guess for Matterport skybox face order: 0 up, 1 front, 2 right, 3 back, 4 left, 5 down
        /// (front = +Z in the converted local frame). The alignment solver verifies/replaces this per space.
        /// </summary>
        public static FaceBasis[] DefaultMatterportLayout() => new[]
        {
            Canonical[(int)SignedAxis.PosY],
            Canonical[(int)SignedAxis.PosZ],
            Canonical[(int)SignedAxis.PosX],
            Canonical[(int)SignedAxis.NegZ],
            Canonical[(int)SignedAxis.NegX],
            Canonical[(int)SignedAxis.NegY],
        };

        /// <summary>Selects the canonical face for a direction and returns image coordinates (s right, t up).</summary>
        public static int Select(Vec3 d, out float s, out float t)
        {
            var axis = Axes.FromVector(d);
            int c = (int)axis;
            var b = Canonical[c];
            float ma = Vec3.Dot(d, b.Forward.ToVector());
            if (ma <= 1e-20f) { s = t = 0.5f; return c; }
            s = 0.5f * (Vec3.Dot(d, b.Right.ToVector()) / ma + 1f);
            t = 0.5f * (Vec3.Dot(d, b.Up.ToVector()) / ma + 1f);
            return c;
        }

        /// <summary>Unnormalised direction for canonical face c at image coordinates (s, t).</summary>
        public static Vec3 Direction(int c, float s, float t) => Canonical[c].Direction(s, t);

        /// <summary>
        /// Occlusion depth cube for a sweep: distance to the first surface per canonical (space-aligned) face texel,
        /// as half floats in face-major, bottom-up row order, ready to upload (Unity RHalf / Godot Rh texture arrays).
        /// Runs in parallel; call it from a worker thread.
        /// </summary>
        public static ushort[] DepthCube(Bvh bvh, Vec3 origin, int size)
        {
            var data = new ushort[6 * size * size];
            System.Threading.Tasks.Parallel.For(0, 6 * size, row =>
            {
                int face = row / size, y = row % size;
                int offset = face * size * size + y * size;
                for (int x = 0; x < size; x++)
                {
                    var dir = Direction(face, (x + 0.5f) / size, (y + 0.5f) / size).Normalized;
                    data[offset + x] = HalfFloat.FromFloat(bvh.Raycast(origin, dir, 500f, out var hit) ? hit.Distance : 1000f);
                }
            });
            return data;
        }

        /// <summary>
        /// Converts a layout (basis per source image) into the per-canonical-face lookup used by shaders:
        /// for canonical face c, which source image to sample and which axes map to its s/t.
        /// Returns false if the layout does not cover all six directions.
        /// </summary>
        public static bool TryBuildLookup(FaceBasis[] layout, out int[] sliceForFace, out Vec3[] right, out Vec3[] up)
        {
            sliceForFace = new int[6];
            right = new Vec3[6];
            up = new Vec3[6];
            for (int c = 0; c < 6; c++) sliceForFace[c] = -1;
            if (layout == null || layout.Length != 6) return false;
            for (int i = 0; i < 6; i++)
            {
                int c = (int)layout[i].Forward;
                if (sliceForFace[c] >= 0) return false;
                sliceForFace[c] = i;
                right[c] = layout[i].Right.ToVector();
                up[c] = layout[i].Up.ToVector();
            }
            return true;
        }

        /// <summary>
        /// Looks up (image index, s, t) for a local direction under a layout - the exact computation the
        /// projection shader performs. Used by tests and the CPU fallback paths.
        /// </summary>
        public static int Lookup(FaceBasis[] layout, Vec3 local, out float s, out float t)
        {
            var axis = Axes.FromVector(local);
            for (int i = 0; i < 6; i++)
            {
                if (layout[i].Forward != axis) continue;
                float ma = Vec3.Dot(local, axis.ToVector());
                s = 0.5f * (Vec3.Dot(local, layout[i].Right.ToVector()) / ma + 1f);
                t = 0.5f * (Vec3.Dot(local, layout[i].Up.ToVector()) / ma + 1f);
                return i;
            }
            s = t = 0.5f;
            return -1;
        }

        public static string Describe(FaceBasis[] layout)
        {
            var parts = new string[layout.Length];
            for (int i = 0; i < layout.Length; i++) parts[i] = i + ":" + layout[i];
            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// Everything needed to interpret a space's raw data consistently: coordinate frames, texture/winding
    /// conventions and the panorama face layout. Stored as calibration.json next to the manifest.
    /// </summary>
    public sealed class SpaceCalibration
    {
        public const string FileName = "calibration.json";

        public CoordinateFrame MeshFrame = CoordinateFrame.ZUpRightHanded;
        public CoordinateFrame SweepFrame = CoordinateFrame.ZUpRightHanded;
        public bool FlipTextureV;
        public bool FlipWinding;
        public RotationMode Rotation = RotationMode.Direct;
        public FaceBasis[] Faces = CubeFaces.DefaultMatterportLayout();

        // How the calibration was obtained. Only "default" (never attempted) triggers an automatic solve on load.
        public const string Default = "default";
        public const string Solver = "solver";
        public const string Unsolved = "unsolved";
        public const string Manual = "manual";
        public const string Synthetic = "synthetic";

        /// <summary><see cref="Default"/>, <see cref="Solver"/>, <see cref="Unsolved"/>, <see cref="Manual"/> or <see cref="Synthetic"/>.</summary>
        public string Method = Default;
        public double Score = double.NaN;
        public double Margin = double.NaN;
        public int SweepsUsed;
        public List<string> Notes = new List<string>();

        /// <summary>True until the solver has run once (failed runs are not repeated automatically).</summary>
        public bool NeedsSolve => Method == Default;

        public SpaceCalibration Clone()
        {
            var c = (SpaceCalibration)MemberwiseClone();
            c.Faces = (FaceBasis[])Faces.Clone();
            c.Notes = new List<string>(Notes);
            return c;
        }

        /// <summary>Panorama-local to Unity-world rotation for a sweep quaternion stored in <see cref="SweepFrame"/>.</summary>
        public Mat3 WorldFromPano(double[] quaternion) => WorldFromPano(quaternion, Rotation, SweepFrame);

        public static Mat3 WorldFromPano(double[] quaternion, RotationMode mode, CoordinateFrame frame)
        {
            if (mode == RotationMode.Identity || quaternion == null || quaternion.Length < 4) return Mat3.Identity;
            var m = Frames.ToUnity(frame);
            var r = m * Quat.FromArray(quaternion).ToMat3() * m.Transposed;
            return mode == RotationMode.Direct ? r : r.Transposed;
        }

        public JsonValue ToJson()
        {
            var faces = JsonValue.NewArray();
            foreach (var f in Faces)
                faces.Add(JsonValue.NewObject().Set("forward", f.Forward.Name()).Set("right", f.Right.Name()).Set("up", f.Up.Name()));
            var j = JsonValue.NewObject()
                .Set("meshFrame", Frames.ToToken(MeshFrame))
                .Set("sweepFrame", Frames.ToToken(SweepFrame))
                .Set("flipTextureV", FlipTextureV)
                .Set("flipWinding", FlipWinding)
                .Set("rotation", Rotation.ToString().ToLowerInvariant())
                .Set("faces", faces)
                .Set("method", Method)
                .Set("sweepsUsed", SweepsUsed);
            if (!double.IsNaN(Score)) j.Set("score", Math.Round(Score, 4));
            if (!double.IsNaN(Margin)) j.Set("margin", Math.Round(Margin, 4));
            j.Set("notes", JsonValue.FromArray(Notes));
            return j;
        }

        public static SpaceCalibration FromJson(JsonValue j)
        {
            var c = new SpaceCalibration
            {
                MeshFrame = Frames.FromToken(j["meshFrame"].AsString()),
                SweepFrame = Frames.FromToken(j["sweepFrame"].AsString()),
                FlipTextureV = j["flipTextureV"].AsBool(),
                FlipWinding = j["flipWinding"].AsBool(),
                Method = j["method"].AsString(Default),
                Score = j["score"].IsNull ? double.NaN : j["score"].AsDouble(),
                Margin = j["margin"].IsNull ? double.NaN : j["margin"].AsDouble(),
                SweepsUsed = j["sweepsUsed"].AsInt(),
                Notes = j["notes"].AsStringList(),
            };
            switch (j["rotation"].AsString("direct"))
            {
                case "identity": c.Rotation = RotationMode.Identity; break;
                case "inverse": c.Rotation = RotationMode.Inverse; break;
                default: c.Rotation = RotationMode.Direct; break;
            }
            var faces = j["faces"];
            if (faces.Count == 6)
            {
                var parsed = new FaceBasis[6];
                bool ok = true;
                for (int i = 0; i < 6; i++)
                {
                    ok &= Axes.TryParse(faces[i]["forward"].AsString(), out parsed[i].Forward);
                    ok &= Axes.TryParse(faces[i]["right"].AsString(), out parsed[i].Right);
                    ok &= Axes.TryParse(faces[i]["up"].AsString(), out parsed[i].Up);
                    ok &= parsed[i].IsValid;
                }
                if (ok && CubeFaces.TryBuildLookup(parsed, out _, out _, out _)) c.Faces = parsed;
            }
            return c;
        }

        public void Save(string spaceDir) =>
            IoUtil.WriteAllTextAtomic(System.IO.Path.Combine(spaceDir, FileName), ToJson().ToJson(true));

        public static SpaceCalibration LoadOrDefault(string spaceDir)
        {
            var path = System.IO.Path.Combine(spaceDir, FileName);
            if (!System.IO.File.Exists(path)) return new SpaceCalibration();
            try { return FromJson(JsonValue.Parse(System.IO.File.ReadAllText(path))); }
            catch (FormatException) { return new SpaceCalibration(); }
        }
    }
}
