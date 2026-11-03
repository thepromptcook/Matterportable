using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Matterportable.Core;

namespace Matterportable.Cli
{
    /// <summary>
    /// matterportable - download, inspect and convert Matterport spaces outside Unity (same code as the Unity package).
    /// </summary>
    static class Program
    {
        const string Usage = @"matterportable <command> [options]

Commands:
  download <link> [--out DIR] [--pano 512,2048] [--textures high|low] [--max-sweeps N] [--concurrency N]
        Download a space into DIR/<model id> (default DIR: ./MatterportCache). Resumable.
  demo [--out DIR] [--size 1024]
        Write the procedural demo house (PNG panoramas) - no network needed.
  info <space dir>
        Summarise a cached space.
  diagnose <space dir>
        Print the diagnostics report (same as Copy Diagnostics in Unity; signed links redacted).
  masks <space dir> [--size 256] [--fill-size 512] [--out DIR]
        For the reconstruction step: per panorama face that shows props, a PNG of which prop each pixel shows
        (red + 256 * green = prop index + 1) and masks.json (default DIR: <space dir>/recon/masks).
";

        static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "help" || args[0] == "--help" || args[0] == "-h")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }
            var log = new ConsoleLog();
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };
                try
                {
                    var opts = Options.Parse(args.Skip(1).ToArray());
                    switch (args[0])
                    {
                        case "download": return Download(opts, log, cts.Token).GetAwaiter().GetResult();
                        case "demo": return Demo(opts);
                        case "info": return Info(opts);
                        case "masks": return Masks(opts);
                        case "diagnose":
                            Console.WriteLine(Diagnostics.Report(opts.Positional(0, "space dir"), "matterportable CLI"));
                            return 0;
                        default:
                            Console.Error.WriteLine("Unknown command: " + args[0]);
                            Console.WriteLine(Usage);
                            return 1;
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("Cancelled (downloaded files are kept; run again to resume).");
                    return 2;
                }
                catch (Exception e) when (e is MatterportException || e is ArgumentException || e is IOException || e is FormatException)
                {
                    Console.Error.WriteLine("error: " + e.Message);
                    return 1;
                }
            }
        }

        static DownloadOptions DownloadOptionsFrom(Options o) => new DownloadOptions
        {
            PanoSizes = o.Get("pano", "512,2048").Split(',').Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray(),
            TextureQuality = o.Get("textures", "high"),
            MaxSweeps = int.Parse(o.Get("max-sweeps", "0"), CultureInfo.InvariantCulture),
            MaxConcurrency = int.Parse(o.Get("concurrency", "6"), CultureInfo.InvariantCulture),
        };

        static async Task<int> Download(Options o, ILogSink log, CancellationToken ct)
        {
            var id = MatterportLink.ParseModelId(o.Positional(0, "link"));
            var dir = CacheLayout.SpaceDir(o.Get("out", CacheLayout.DefaultFolderName), id);
            using (var http = new HttpClientTransport())
            {
                var downloader = new SpaceDownloader(http, log);
                var task = downloader.DownloadAsync(id, dir, DownloadOptionsFrom(o), ct);
                await Progress(task, downloader.Status);
                var m = await task;
                Console.WriteLine($"Done: \"{m.Name}\" → {dir}");
            }
            return 0;
        }

        static async Task Progress(Task task, DownloadStatus status)
        {
            bool interactive = !Console.IsOutputRedirected;
            while (!task.IsCompleted)
            {
                await Task.WhenAny(task, Task.Delay(500));
                if (interactive) Console.Write("\r" + status.ToString().PadRight(Math.Max(0, Console.WindowWidth - 1)));
            }
            if (interactive) Console.WriteLine();
        }

        static int Demo(Options o)
        {
            int size = int.Parse(o.Get("size", "1024"), CultureInfo.InvariantCulture);
            var space = SyntheticSpace.Generate(new SyntheticOptions { PanoSizes = new[] { Math.Max(32, size / 4), size }, CleanPhotos = true });
            var dir = CacheLayout.SpaceDir(o.Get("out", CacheLayout.DefaultFolderName), space.Manifest.ModelId);
            space.WriteTo(dir);
            Console.WriteLine($"Demo space written to {dir}");
            return 0;
        }

        static int Masks(Options o)
        {
            var dir = o.Positional(0, "space dir");
            int size = int.Parse(o.Get("size", "256"), CultureInfo.InvariantCulture);
            int fillSize = int.Parse(o.Get("fill-size", "512"), CultureInfo.InvariantCulture); // 0: no reprojection maps
            var outDir = o.Get("out", Path.Combine(dir, "recon", "masks"));
            var model = SpaceModel.Load(dir, SpaceManifest.Load(dir), SpaceCalibration.LoadOrDefault(dir), new ConsoleLog());
            if (model.Geometry == null) throw new ArgumentException("The space has no mesh.");
            Directory.CreateDirectory(outDir);
            var faces = JsonValue.NewArray();
            var started = DateTime.UtcNow;
            for (int s = 0; s < model.Sweeps.Count; s++)
                for (int f = 0; f < 6; f++)
                {
                    var mask = PropMasks.PieceMask(model, s, f, size);
                    var counts = new SortedDictionary<int, int>();
                    foreach (int p in mask) if (p > 0) counts[p - 1] = counts.TryGetValue(p - 1, out int c) ? c + 1 : 1;
                    if (counts.Count == 0) continue;
                    var img = new RgbImage(size, size);
                    for (int i = 0; i < mask.Length; i++) img.Set(i % size, i / size, (byte)(mask[i] & 255), (byte)(mask[i] >> 8), 0);
                    var file = $"s{s:D3}_f{f}.png";
                    File.WriteAllBytes(Path.Combine(outDir, file), Png.Encode(img));
                    var props = JsonValue.NewObject();
                    foreach (var kv in counts) props.Set(kv.Key.ToString(CultureInfo.InvariantCulture), kv.Value);
                    var entry = JsonValue.NewObject().Set("sweep", s).Set("face", f).Set("mask", file).Set("photo", Photo(model, s)?.FacePath(f)).Set("props", props);
                    if (fillSize > 0) // where other sweeps saw what the props hide (the painting script copies it in)
                    {
                        var fill = $"s{s:D3}_f{f}.fill";
                        using (var gz = new System.IO.Compression.GZipStream(File.Create(Path.Combine(outDir, fill)), System.IO.Compression.CompressionLevel.Fastest))
                        {
                            var bytes = Reprojection.Build(model, s, f, fillSize).Serialize(); // mostly "nothing": gzip, ~1/20
                            gz.Write(bytes, 0, bytes.Length);
                        }
                        entry.Set("fill", fill);
                    }
                    faces.Add(entry);
                }
            var photos = JsonValue.NewArray(); // per sweep, its six photo faces (sources for the fills)
            for (int s = 0; s < model.Sweeps.Count; s++)
            {
                var faceFiles = JsonValue.NewArray();
                for (int f = 0; f < 6; f++) faceFiles.Add(JsonValue.From(Photo(model, s)?.FacePath(f)));
                photos.Add(faceFiles);
            }
            var json = JsonValue.NewObject().Set("size", size).Set("props", model.Props.Count).Set("photos", photos).Set("faces", faces);
            File.WriteAllText(Path.Combine(outDir, "masks.json"), json.ToJson(true));
            Console.WriteLine($"{faces.Count} faces show props ({model.Props.Count} props, {model.Sweeps.Count} sweeps) in {(DateTime.UtcNow - started).TotalSeconds:0.0}s -> {outDir}");
            return 0;
        }

        /// <summary>The largest single-file tier (what the faces were painted at, as CleanPanoramas).</summary>
        static PanoTier Photo(SpaceModel model, int sweep) => model.Sweeps[sweep].Entry.Tiers.Where(t => t.Tiles <= 1).OrderBy(t => t.Size).LastOrDefault();

        static int Info(Options o)
        {
            var dir = o.Positional(0, "space dir");
            var m = SpaceManifest.Load(dir);
            var cal = SpaceCalibration.LoadOrDefault(dir);
            Console.WriteLine($"{m.Name} ({m.ModelId})  source: {m.Source}  created: {m.CreatedUtc}");
            Console.WriteLine($"Sweeps: {m.Sweeps.Count}, floors: {m.Floors.Count}, textures: {m.Textures.Count}, size: {IoUtil.FormatBytes(IoUtil.DirectorySize(dir))}");
            var tiers = m.Sweeps.SelectMany(s => s.Tiers.Select(t => t.Size)).Distinct().OrderBy(x => x);
            Console.WriteLine("Panorama tiers: " + string.Join(", ", tiers));
            if (m.Mesh != null)
            {
                var mesh = DamDecoder.Decode(File.ReadAllBytes(CacheLayout.Resolve(dir, m.Mesh.File)));
                var geo = SpaceGeometry.Build(mesh, cal);
                Console.WriteLine($"Mesh: {mesh.VertexCount:N0} vertices, {mesh.TriangleCount:N0} triangles, {geo.Parts.Count} materials, " +
                                  $"bounds {geo.BoundsMin} - {geo.BoundsMax} (Unity space)");
                var sweeps = SweepBuilder.Build(m, cal, geo);
                Console.WriteLine($"Sweeps above a floor: {sweeps.Count(s => s.FloorHit)}/{sweeps.Count}, " +
                                  $"front-facing surfaces: {GeometryChecks.FrontFacingFraction(geo, sweeps):P0}");
            }
            else Console.WriteLine("Mesh: none");
            Console.WriteLine($"Calibration: {cal.Method}, rotation {cal.Rotation}, frames {Frames.ToToken(cal.MeshFrame)}/{Frames.ToToken(cal.SweepFrame)}" +
                              (double.IsNaN(cal.Score) ? "" : $", score {cal.Score:0.000}"));
            Console.WriteLine("Faces: " + CubeFaces.Describe(cal.Faces));
            foreach (var w in m.Warnings) Console.WriteLine("warning: " + w);
            return 0;
        }

        sealed class Options
        {
            readonly List<string> _positional = new List<string>();
            readonly Dictionary<string, string> _named = new Dictionary<string, string>();

            public static Options Parse(string[] args)
            {
                var o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i].StartsWith("--"))
                    {
                        var key = args[i].Substring(2);
                        o._named[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
                    }
                    else o._positional.Add(args[i]);
                }
                return o;
            }

            public string Positional(int i, string name) =>
                i < _positional.Count ? _positional[i] : throw new ArgumentException($"Missing argument: {name}");

            public string Get(string key, string fallback) => _named.TryGetValue(key, out var v) ? v : fallback;
        }
    }
}
