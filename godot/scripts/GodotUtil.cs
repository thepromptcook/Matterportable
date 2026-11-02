using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Matterportable.Core;

namespace Matterportable
{
    /// <summary>
    /// Core ↔ Godot glue. Core (and Unity) use a left-handed Y-up frame; Godot is right-handed Y-up, so Z is
    /// mirrored; Core triangle order is then front-facing for Godot. Image rows: Core is bottom-up, Godot images top-down.
    /// </summary>
    public static class GodotUtil
    {
        public static Vector3 ToGodot(this Vec3 v) => new Vector3(v.X, v.Y, -v.Z);
        public static Vec3 ToCore(this Vector3 v) => new Vec3(v.X, v.Y, -v.Z);
        /// <summary>A Core camera rotation (camera looking down +Z) as a Godot one (looking down -Z): M R M with M = mirror Z.</summary>
        public static Quaternion ToGodot(this Quat q) => new Quaternion(-(float)q.X, -(float)q.Y, (float)q.Z, (float)q.W);
        public static Color ToGodot(this Rgba c) => new Color(c.R, c.G, c.B, c.A);

        /// <summary>
        /// A sweep's WorldFromPano as a shader mat3 (Core frame), orthonormalised the way Unity's
        /// Quaternion.LookRotation(column 2, column 1) does, so both engines project panoramas identically.
        /// </summary>
        public static Basis ShaderRotation(Mat3 m)
        {
            var q = Frames.ShaderRotation(m);
            Vector3 C(int i) { var c = q.Column(i); return new Vector3(c.X, c.Y, c.Z); }
            return new Basis(C(0), C(1), C(2));
        }

        /// <summary>Decodes a PNG/JPEG/WebP file. Safe on worker threads (keep decoding off the main thread).</summary>
        public static Image LoadImage(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var img = new Image();
            Error err;
            if (bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == (byte)'P') err = img.LoadPngFromBuffer(bytes);
            else if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) err = img.LoadJpgFromBuffer(bytes);
            else err = img.LoadWebpFromBuffer(bytes);
            if (err != Error.Ok) throw new IOException($"Could not decode {path} ({err}).");
            return img;
        }

        /// <summary>Luminance image (Core row order) for the alignment solver.</summary>
        public static LumaImage LoadLuma(string path)
        {
            if (!File.Exists(path)) return null;
            var img = LoadImage(path);
            img.Convert(Image.Format.Rgb8);
            int w = img.GetWidth(), h = img.GetHeight();
            var data = img.GetData();
            var luma = new LumaImage(w, h);
            for (int y = 0; y < h; y++)
            {
                int src = (h - 1 - y) * w * 3, dst = y * w;
                for (int x = 0; x < w; x++, src += 3)
                    luma.Pixels[dst + x] = (0.299f * data[src] + 0.587f * data[src + 1] + 0.114f * data[src + 2]) / 255f;
            }
            int max = CalibrationPipeline.TextureSize;
            return w > max || h > max ? luma.Resize(Math.Min(max, w), Math.Min(max, h)) : luma;
        }

        /// <summary>
        /// Where spaces are cached. In the editor: the repository's MatterportCache folder, shared with the Unity
        /// project (a space downloaded by either engine opens in both). Exported games: user://MatterportCache.
        /// </summary>
        public static string CacheRoot(string overrideFolder = null)
        {
            if (!string.IsNullOrWhiteSpace(overrideFolder)) return Path.GetFullPath(overrideFolder);
            if (OS.HasFeature("editor"))
                return Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", CacheLayout.DefaultFolderName));
            return ProjectSettings.GlobalizePath("user://" + CacheLayout.DefaultFolderName);
        }

        public static string Unwrap(Exception e)
        {
            while (e is AggregateException && e.InnerException != null) e = e.InnerException;
            return e?.Message ?? "unknown error";
        }

        /// <summary>The offline demo house, written in the cache format (PNG images, Core encoder).</summary>
        public static Task<string> EnsureDemoAsync(int faceSize, CancellationToken ct) => Task.Run(() =>
        {
            const string id = "DemoSpace01";
            var dir = CacheLayout.SpaceDir(CacheRoot(), id);
            if (SpaceManifest.Exists(dir) && File.Exists(CleanPanoramas.IndexPath(dir))) return dir; // (older demos lack the clean photos)
            var space = SyntheticSpace.Generate(new SyntheticOptions
            {
                ModelId = id,
                CleanPhotos = true,
                PanoSizes = new[] { Math.Max(64, faceSize / 4), faceSize },
                TextureSize = Math.Max(128, faceSize / 2),
            });
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            space.WriteTo(dir);
            return dir;
        }, ct);
    }

    /// <summary>Core log → Godot output. Info only with verbose logging; every load also writes debug/load.log.</summary>
    public sealed class GodotLog : ILogSink
    {
        readonly bool _verbose;
        public GodotLog(bool verbose) { _verbose = verbose; }

        public void Log(LogLevel level, string message)
        {
            if (level == LogLevel.Error) GD.PushError("[Matterportable] " + message);
            else if (level == LogLevel.Warning) GD.PushWarning("[Matterportable] " + message);
            else if (_verbose) GD.Print("[Matterportable] " + message);
        }
    }
}
