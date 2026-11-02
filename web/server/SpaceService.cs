using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Matterportable.Core;

namespace Matterportable.Web
{
    /// <summary>Spaces on the server: one background job per space downloads it if needed and prepares its bundle.</summary>
    public sealed class SpaceService
    {
        public sealed class Job
        {
            public volatile string Stage = "Starting";
            public volatile string Error;
            public volatile bool Ready;
            public DownloadStatus Download;
        }

        const string DemoId = "DemoSpace01";
        static readonly string[] ServedFolders = { "panos/", "textures/", CleanPanoramas.Folder + "/" };
        static readonly string[] ServedExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

        readonly bool _allowDownloads;
        readonly ConcurrentDictionary<string, Job> _jobs = new ConcurrentDictionary<string, Job>();

        public string Root { get; }

        public SpaceService(string root, bool allowDownloads)
        {
            Root = root;
            _allowDownloads = allowDownloads;
        }

        string Dir(string id) => CacheLayout.SpaceDir(Root, id);

        public IEnumerable<object> List()
        {
            if (!Directory.Exists(Root)) return Array.Empty<object>();
            return Directory.GetDirectories(Root).Where(SpaceManifest.Exists).Select(d =>
            {
                string name = Path.GetFileName(d);
                try { name = SpaceManifest.Load(d).Name; } catch (Exception) { /* list it anyway */ }
                return (object)new { id = Path.GetFileName(d), name };
            }).ToList();
        }

        /// <summary>Starts (or reuses) the job that makes a space ready to view.</summary>
        public object Open(string id)
        {
            id = CacheLayout.SafeName(id);
            var job = _jobs.AddOrUpdate(id, _ => Start(id), (_, existing) => existing.Error != null ? Start(id) : existing);
            return Describe(id, job);
        }

        public async Task<object> OpenDemoAsync()
        {
            var dir = Dir(DemoId);
            if (!SpaceManifest.Exists(dir) || !File.Exists(Path.Combine(dir, CleanPanoramas.Folder, "clean.json"))) // (older demos lack the clean photos)
                await Task.Run(() =>
                {
                    var space = SyntheticSpace.Generate(new SyntheticOptions { ModelId = DemoId, PanoSizes = new[] { 96, 384 }, TextureSize = 192, CleanPhotos = true });
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    space.WriteTo(dir);
                });
            return Open(DemoId);
        }

        public object Status(string id) => _jobs.TryGetValue(CacheLayout.SafeName(id), out var job) ? Describe(id, job) : null;

        static object Describe(string id, Job job)
        {
            var d = job.Download;
            float progress = job.Ready ? 1f : d != null && d.FilesTotal > 0 ? 0.05f + 0.8f * d.Fraction : 0.02f;
            var stage = d != null && !job.Ready && job.Stage == "Downloading" ? "Downloading: " + d : job.Stage;
            return new { id, stage, progress, error = job.Error, ready = job.Ready };
        }

        Job Start(string id)
        {
            var job = new Job();
            Task.Run(async () =>
            {
                try
                {
                    var dir = Dir(id);
                    if (!SpaceManifest.Exists(dir))
                    {
                        if (!_allowDownloads) throw new InvalidOperationException("This server only shows the spaces it already has.");
                        job.Stage = "Downloading";
                        using (var http = new HttpClientTransport())
                        {
                            var downloader = new SpaceDownloader(http, null);
                            job.Download = downloader.Status;
                            await downloader.DownloadAsync(id, dir, new DownloadOptions { PanoSizes = new[] { 512, 2048 } }, CancellationToken.None);
                        }
                    }
                    job.Stage = "Preparing the space";
                    var log = new BufferLog();
                    WebBundle.Prepare(dir, log);
                    job.Stage = "Ready";
                    job.Ready = true;
                }
                catch (Exception e)
                {
                    while (e is AggregateException && e.InnerException != null) e = e.InnerException;
                    job.Error = e.Message;
                }
            });
            return job;
        }

        /// <summary>A prepared bundle file (space.json, geometry.bin, bvh.bin, features.bin) once the space is ready.</summary>
        public string BundleFile(string id, string file)
        {
            if (!WebBundle.Files.Contains(file) || Status(id) == null) return null;
            var path = Path.Combine(Dir(CacheLayout.SafeName(id)), WebBundle.Folder, file);
            return File.Exists(path) ? path : null;
        }

        /// <summary>A photo or mesh texture of a space; nothing outside panos/ and textures/ is served.</summary>
        public string SpaceFile(string id, string relative)
        {
            relative = (relative ?? "").Replace('\\', '/');
            if (!ServedFolders.Any(relative.StartsWith) || !ServedExtensions.Contains(Path.GetExtension(relative).ToLowerInvariant())) return null;
            var dir = Path.GetFullPath(Dir(CacheLayout.SafeName(id)));
            var full = Path.GetFullPath(Path.Combine(dir, relative));
            return full.StartsWith(dir + Path.DirectorySeparatorChar) && File.Exists(full) ? full : null;
        }
    }
}
