using System.IO;
using Matterportable.Core;
using UnityEngine;

namespace Matterportable
{
    /// <summary>Conversions between the engine-agnostic Core types and Unity types.</summary>
    public static class MpConvert
    {
        public static Vector3 ToUnity(this Vec3 v) => new Vector3(v.X, v.Y, v.Z);
        public static Vec3 ToCore(this Vector3 v) => new Vec3(v.x, v.y, v.z);

        public static Quaternion ToQuaternion(this Mat3 m) =>
            Quaternion.LookRotation(m.Column(2).ToUnity(), m.Column(1).ToUnity());

        public static Matrix4x4 ToMatrix(this Mat3 m)
        {
            var r = Matrix4x4.identity;
            r.m00 = m.M00; r.m01 = m.M01; r.m02 = m.M02;
            r.m10 = m.M10; r.m11 = m.M11; r.m12 = m.M12;
            r.m20 = m.M20; r.m21 = m.M21; r.m22 = m.M22;
            return r;
        }

        public static Mat3 ToMat3(this Quaternion q) => Mat3.FromColumns(
            (q * Vector3.right).ToCore(), (q * Vector3.up).ToCore(), (q * Vector3.forward).ToCore());
    }

    /// <summary>Forwards Core log messages to the Unity console (thread-safe).</summary>
    public sealed class UnityLog : ILogSink
    {
        public static readonly UnityLog Instance = new UnityLog();
        public bool Verbose = true;

        public void Log(LogLevel level, string message)
        {
            switch (level)
            {
                case LogLevel.Error: Debug.LogError("[Matterportable] " + message); break;
                case LogLevel.Warning: Debug.LogWarning("[Matterportable] " + message); break;
                default: if (Verbose) Debug.Log("[Matterportable] " + message); break;
            }
        }
    }

    public static class MpUtil
    {
        public static void SafeDestroy(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        public static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            SafeDestroy(rt);
            rt = null;
        }

        /// <summary>Decodes an image file on the calling (main) thread into luminance for the alignment solver.</summary>
        public static LumaImage LoadLuma(string path)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(path))) return null;
                var px = tex.GetPixels32();
                var l = new LumaImage(tex.width, tex.height);
                for (int i = 0; i < px.Length; i++)
                    l.Pixels[i] = (0.299f * px[i].r + 0.587f * px[i].g + 0.114f * px[i].b) / 255f;
                return l;
            }
            finally { SafeDestroy(tex); }
        }

        /// <summary>Encodes a Core image as JPEG using Unity's encoder (used by the demo space generator).</summary>
        public static byte[] EncodeJpg(RgbImage img, int quality = 92)
        {
            var tex = new Texture2D(img.Width, img.Height, TextureFormat.RGB24, false);
            try
            {
                tex.SetPixelData(img.Pixels, 0);
                tex.Apply(false);
                return tex.EncodeToJPG(quality);
            }
            finally { SafeDestroy(tex); }
        }

        public static string FileUri(string path) => new System.Uri(Path.GetFullPath(path)).AbsoluteUri;
    }

    /// <summary>Where downloaded spaces live.</summary>
    public static class MatterportPaths
    {
        /// <summary>
        /// Editor: &lt;project&gt;/MatterportCache (outside Assets, so Unity never imports the raw files).
        /// Player: StreamingAssets/Matterport if present (spaces bundled into the build), else persistentDataPath.
        /// </summary>
        public static string DefaultCacheRoot
        {
            get
            {
#if UNITY_EDITOR
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", CacheLayout.DefaultFolderName));
#else
                var bundled = Path.Combine(Application.streamingAssetsPath, "Matterport");
                return Directory.Exists(bundled) ? bundled : Path.Combine(Application.persistentDataPath, "Matterport");
#endif
            }
        }

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        public static string ResolveCacheRoot(string overrideFolder)
        {
            if (string.IsNullOrWhiteSpace(overrideFolder)) return DefaultCacheRoot;
#if UNITY_EDITOR
            return Path.IsPathRooted(overrideFolder) ? overrideFolder : Path.GetFullPath(Path.Combine(ProjectRoot, overrideFolder));
#else
            // In players a custom editor folder is meaningless; spaces are bundled/downloaded to the default root.
            return Path.IsPathRooted(overrideFolder) && Directory.Exists(overrideFolder) ? overrideFolder : DefaultCacheRoot;
#endif
        }

        public static string SpaceDir(string overrideFolder, string modelId) =>
            CacheLayout.SpaceDir(ResolveCacheRoot(overrideFolder), modelId);
    }
}
