using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Matterportable.Core
{
    // Minimal engine-agnostic math. All vectors use Unity's convention once converted:
    // left-handed, +X right, +Y up, +Z forward (metres).

    public struct Vec2
    {
        public float X, Y;
        public Vec2(float x, float y) { X = x; Y = y; }
        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###})", X, Y);
    }

    public struct Vec3 : IEquatable<Vec3>
    {
        public float X, Y, Z;

        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public Vec3(double x, double y, double z) { X = (float)x; Y = (float)y; Z = (float)z; }

        public static readonly Vec3 Zero = new Vec3(0f, 0f, 0f);
        public static readonly Vec3 One = new Vec3(1f, 1f, 1f);
        public static readonly Vec3 Up = new Vec3(0f, 1f, 0f);
        public static readonly Vec3 Down = new Vec3(0f, -1f, 0f);
        public static readonly Vec3 Right = new Vec3(1f, 0f, 0f);
        public static readonly Vec3 Forward = new Vec3(0f, 0f, 1f);

        public float this[int i]
        {
            get => i == 0 ? X : i == 1 ? Y : Z;
            set { if (i == 0) X = value; else if (i == 1) Y = value; else Z = value; }
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(float s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        /// <summary>Standard cross product formula (in Unity's left-handed frame Up x Forward = Right).</summary>
        public static Vec3 Cross(Vec3 a, Vec3 b) =>
            new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public static Vec3 Min(Vec3 a, Vec3 b) => new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        public static Vec3 Max(Vec3 a, Vec3 b) => new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => a + (b - a) * t;

        public float SqrLength => X * X + Y * Y + Z * Z;
        public float Length => (float)Math.Sqrt(SqrLength);

        public Vec3 Normalized
        {
            get
            {
                float l = Length;
                return l > 1e-12f ? this / l : Zero;
            }
        }

        public static float Distance(Vec3 a, Vec3 b) => (a - b).Length;

        public bool Equals(Vec3 o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() * 397) ^ (Z.GetHashCode() * 7919);
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
    }

    /// <summary>Row-major 3x3 matrix. <c>m * v</c> treats v as a column vector.</summary>
    public struct Mat3
    {
        public float M00, M01, M02, M10, M11, M12, M20, M21, M22;

        public static readonly Mat3 Identity = new Mat3(1, 0, 0, 0, 1, 0, 0, 0, 1);

        public Mat3(float m00, float m01, float m02, float m10, float m11, float m12, float m20, float m21, float m22)
        {
            M00 = m00; M01 = m01; M02 = m02;
            M10 = m10; M11 = m11; M12 = m12;
            M20 = m20; M21 = m21; M22 = m22;
        }

        public static Mat3 FromColumns(Vec3 c0, Vec3 c1, Vec3 c2) =>
            new Mat3(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);

        public Vec3 Column(int i) => i == 0 ? new Vec3(M00, M10, M20) : i == 1 ? new Vec3(M01, M11, M21) : new Vec3(M02, M12, M22);
        public Vec3 Row(int i) => i == 0 ? new Vec3(M00, M01, M02) : i == 1 ? new Vec3(M10, M11, M12) : new Vec3(M20, M21, M22);

        public static Vec3 operator *(Mat3 m, Vec3 v) => new Vec3(
            m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
            m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
            m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z);

        public static Mat3 operator *(Mat3 a, Mat3 b) => FromColumns(a * b.Column(0), a * b.Column(1), a * b.Column(2));

        public Mat3 Transposed => new Mat3(M00, M10, M20, M01, M11, M21, M02, M12, M22);

        public float Determinant =>
            M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

        public static Mat3 RotationY(double radians)
        {
            float c = (float)Math.Cos(radians), s = (float)Math.Sin(radians);
            // Same handedness as Unity's Quaternion.AngleAxis(deg, Vector3.up): +Z rotates towards +X.
            return new Mat3(c, 0, s, 0, 1, 0, -s, 0, c);
        }

        public override string ToString() => $"[{Row(0)}, {Row(1)}, {Row(2)}]";
    }

    public struct Quat
    {
        public double X, Y, Z, W;

        public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Quat Identity = new Quat(0, 0, 0, 1);

        public static Quat FromArray(double[] a) =>
            a != null && a.Length >= 4 ? new Quat(a[0], a[1], a[2], a[3]).Normalized : Identity;

        public Quat Normalized
        {
            get
            {
                double l = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
                return l > 1e-12 ? new Quat(X / l, Y / l, Z / l, W / l) : Identity;
            }
        }

        public static Quat AxisAngle(Vec3 axis, double radians)
        {
            var a = axis.Normalized;
            double s = Math.Sin(radians / 2);
            return new Quat(a.X * s, a.Y * s, a.Z * s, Math.Cos(radians / 2));
        }

        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        /// <summary>Quaternion of a proper rotation matrix (inverse of <see cref="ToMat3"/>).</summary>
        public static Quat FromMat3(Mat3 m)
        {
            double trace = m.M00 + m.M11 + m.M22;
            double x, y, z, w;
            if (trace > 0)
            {
                double s = Math.Sqrt(trace + 1.0) * 2;
                w = 0.25 * s;
                x = (m.M21 - m.M12) / s;
                y = (m.M02 - m.M20) / s;
                z = (m.M10 - m.M01) / s;
            }
            else if (m.M00 > m.M11 && m.M00 > m.M22)
            {
                double s = Math.Sqrt(1.0 + m.M00 - m.M11 - m.M22) * 2;
                w = (m.M21 - m.M12) / s;
                x = 0.25 * s;
                y = (m.M01 + m.M10) / s;
                z = (m.M02 + m.M20) / s;
            }
            else if (m.M11 > m.M22)
            {
                double s = Math.Sqrt(1.0 + m.M11 - m.M00 - m.M22) * 2;
                w = (m.M02 - m.M20) / s;
                x = (m.M01 + m.M10) / s;
                y = 0.25 * s;
                z = (m.M12 + m.M21) / s;
            }
            else
            {
                double s = Math.Sqrt(1.0 + m.M22 - m.M00 - m.M11) * 2;
                w = (m.M10 - m.M01) / s;
                x = (m.M02 + m.M20) / s;
                y = (m.M12 + m.M21) / s;
                z = 0.25 * s;
            }
            return new Quat(x, y, z, w).Normalized;
        }

        public double[] ToArray() => new[] { X, Y, Z, W };

        /// <summary>Rotation matrix of the active rotation v' = q v q* (same algebra Unity uses).</summary>
        public Mat3 ToMat3()
        {
            var q = Normalized;
            double x = q.X, y = q.Y, z = q.Z, w = q.W;
            return new Mat3(
                (float)(1 - 2 * (y * y + z * z)), (float)(2 * (x * y - z * w)), (float)(2 * (x * z + y * w)),
                (float)(2 * (x * y + z * w)), (float)(1 - 2 * (x * x + z * z)), (float)(2 * (y * z - x * w)),
                (float)(2 * (x * z - y * w)), (float)(2 * (y * z + x * w)), (float)(1 - 2 * (x * x + y * y)));
        }
    }

    /// <summary>The six signed coordinate axes; the numeric values double as cube face indices.</summary>
    public enum SignedAxis { PosX = 0, NegX = 1, PosY = 2, NegY = 3, PosZ = 4, NegZ = 5 }

    public static class Axes
    {
        public static Vec3 ToVector(this SignedAxis a)
        {
            switch (a)
            {
                case SignedAxis.PosX: return new Vec3(1f, 0f, 0f);
                case SignedAxis.NegX: return new Vec3(-1f, 0f, 0f);
                case SignedAxis.PosY: return new Vec3(0f, 1f, 0f);
                case SignedAxis.NegY: return new Vec3(0f, -1f, 0f);
                case SignedAxis.PosZ: return new Vec3(0f, 0f, 1f);
                default: return new Vec3(0f, 0f, -1f);
            }
        }

        public static int AxisIndex(this SignedAxis a) => (int)a >> 1;

        /// <summary>The signed axis closest to the given direction.</summary>
        public static SignedAxis FromVector(Vec3 v)
        {
            float ax = Math.Abs(v.X), ay = Math.Abs(v.Y), az = Math.Abs(v.Z);
            if (ax >= ay && ax >= az) return v.X >= 0 ? SignedAxis.PosX : SignedAxis.NegX;
            if (ay >= az) return v.Y >= 0 ? SignedAxis.PosY : SignedAxis.NegY;
            return v.Z >= 0 ? SignedAxis.PosZ : SignedAxis.NegZ;
        }

        static readonly string[] Names = { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };
        public static string Name(this SignedAxis a) => Names[(int)a];

        public static bool TryParse(string s, out SignedAxis axis)
        {
            int i = Array.IndexOf(Names, (s ?? "").Trim().ToUpperInvariant());
            axis = i >= 0 ? (SignedAxis)i : SignedAxis.PosX;
            return i >= 0;
        }
    }

    /// <summary>Coordinate conventions source data may be expressed in.</summary>
    public enum CoordinateFrame
    {
        /// <summary>Right-handed, Z up (Matterport model space, MatterPak OBJ). Unity = (x, z, y).</summary>
        ZUpRightHanded = 0,
        /// <summary>Right-handed, Y up (three.js / glTF / Showcase SDK). Unity = (x, y, -z).</summary>
        YUpRightHanded = 1,
        /// <summary>Already in Unity's left-handed Y-up frame.</summary>
        Unity = 2,
    }

    public static class Frames
    {
        /// <summary>
        /// A sweep's WorldFromPano as the proper rotation every engine's shader uses: orthonormalised the way Unity's
        /// Quaternion.LookRotation(column 2, column 1) does it (columns: right, up, forward).
        /// </summary>
        public static Mat3 ShaderRotation(Mat3 m)
        {
            var f = m.Column(2).Normalized;
            var r = Vec3.Cross(m.Column(1), f).Normalized;
            return Mat3.FromColumns(r, Vec3.Cross(f, r), f);
        }

        public static Mat3 ToUnity(CoordinateFrame f)
        {
            switch (f)
            {
                case CoordinateFrame.ZUpRightHanded: return new Mat3(1, 0, 0, 0, 0, 1, 0, 1, 0);
                case CoordinateFrame.YUpRightHanded: return new Mat3(1, 0, 0, 0, 1, 0, 0, 0, -1);
                default: return Mat3.Identity;
            }
        }

        /// <summary>True when converting flips handedness (and therefore triangle winding).</summary>
        public static bool FlipsHandedness(CoordinateFrame f) => ToUnity(f).Determinant < 0;

        public static string ToToken(CoordinateFrame f) =>
            f == CoordinateFrame.ZUpRightHanded ? "zup-rh" : f == CoordinateFrame.YUpRightHanded ? "yup-rh" : "unity";

        public static CoordinateFrame FromToken(string s, CoordinateFrame fallback = CoordinateFrame.ZUpRightHanded)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "zup-rh": case "zup": case "matterport": return CoordinateFrame.ZUpRightHanded;
                case "yup-rh": case "yup": case "threejs": case "gltf": return CoordinateFrame.YUpRightHanded;
                case "unity": return CoordinateFrame.Unity;
                default: return fallback;
            }
        }
    }

    /// <summary>IEEE half-precision encoding (for GPU depth maps), usable on worker threads.</summary>
    public static class HalfFloat
    {
        [StructLayout(LayoutKind.Explicit)]
        struct Bits
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public uint U;
        }

        /// <summary>Round-toward-zero conversion; tiny values flush to zero, huge ones become infinity.</summary>
        public static ushort FromFloat(float value)
        {
            uint b = new Bits { F = value }.U;
            uint sign = (b >> 16) & 0x8000u;
            int exponent = (int)((b >> 23) & 0xFF) - 127 + 15;
            if (exponent <= 0) return (ushort)sign;
            if (exponent >= 31) return (ushort)(sign | 0x7C00u);
            return (ushort)(sign | ((uint)exponent << 10) | ((b & 0x7FFFFFu) >> 13));
        }

        public static float ToFloat(ushort h)
        {
            uint sign = (uint)(h & 0x8000) << 16;
            int exponent = (h >> 10) & 0x1F;
            uint mantissa = (uint)(h & 0x3FF);
            if (exponent == 0) return 0f * (sign != 0 ? -1 : 1);
            uint bits = exponent == 31 ? sign | 0x7F800000u | (mantissa << 13) : sign | ((uint)(exponent - 15 + 127) << 23) | (mantissa << 13);
            return new Bits { U = bits }.F;
        }
    }
}
