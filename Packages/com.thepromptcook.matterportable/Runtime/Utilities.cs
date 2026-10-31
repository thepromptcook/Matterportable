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

        public static Mat3 ToMat3(this Quaternion q) => Mat3.FromColumns(
            (q * Vector3.right).ToCore(), (q * Vector3.up).ToCore(), (q * Vector3.forward).ToCore());
    }

    /// <summary>
    /// Forwards Core log messages to the Unity console (thread-safe). Info messages only appear with
    /// <see cref="MatterportViewerSettings.verboseLogging"/>; every load also writes them to debug/load.log.
    /// </summary>
    public sealed class UnityLog : ILogSink
    {
        public static readonly UnityLog All = new UnityLog(true);
        public static readonly UnityLog WarningsOnly = new UnityLog(false);

        public static UnityLog For(MatterportViewerSettings settings) =>
            settings != null && settings.verboseLogging ? All : WarningsOnly;

        readonly bool _verbose;

        UnityLog(bool verbose) { _verbose = verbose; }

        public void Log(LogLevel level, string message)
        {
            switch (level)
            {
                case LogLevel.Error: Debug.LogError("[Matterportable] " + message); break;
                case LogLevel.Warning: Debug.LogWarning("[Matterportable] " + message); break;
                default:
                    if (_verbose) Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", "[Matterportable] " + message);
                    break;
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

        /// <summary>First loaded object of a type (version-independent wrapper around Unity's find APIs).</summary>
        public static T FindFirst<T>() where T : Object
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindAnyObjectByType<T>();
#else
            return Object.FindObjectOfType<T>();
#endif
        }

        /// <summary>All loaded objects of a type.</summary>
        public static T[] FindAll<T>() where T : Object
        {
#if UNITY_6000_5_OR_NEWER
            return Object.FindObjectsByType<T>();
#elif UNITY_2023_1_OR_NEWER
            return Object.FindObjectsByType<T>(FindObjectsSortMode.None);
#else
            return Object.FindObjectsOfType<T>();
#endif
        }

        /// <summary>1x1 texture of a colour (for IMGUI backgrounds).</summary>
        public static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
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

        /// <summary>Encodes a Core image as JPEG using Unity's encoder (used by the demo space).</summary>
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

        /// <summary>Unity/platform details prepended to diagnostics reports.</summary>
        public static string EnvironmentSummary() =>
            $"Unity {Application.unityVersion}, {Application.platform}, {SystemInfo.graphicsDeviceType} ({SystemInfo.graphicsDeviceName}), " +
            $"pipeline: {(UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.GetType().Name : "Built-in")}, " +
            $"color space: {QualitySettings.activeColorSpace}";
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
