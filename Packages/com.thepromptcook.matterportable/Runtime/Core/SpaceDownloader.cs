using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Matterportable.Core
{
    public sealed class DownloadOptions
    {
        /// <summary>Panorama face sizes to fetch. The smallest doubles as the quick preview and alignment input.</summary>
        public int[] PanoSizes = { 512, 2048 };
        public string TextureQuality = "high";
        public int MaxConcurrency = 6;
        public int MaxRetries = 4;
        public bool Panoramas = true;
        /// <summary>Only download the first N sweeps (0 = all). Handy for a quick first look at a huge space.</summary>
        public int MaxSweeps;
        public bool Overwrite;
    }

    /// <summary>Live, thread-safe download status for progress bars.</summary>
    public sealed class DownloadStatus
    {
        volatile string _stage = "Idle";
        volatile string _current = "";
        int _done, _total, _failed;
        long _bytes;

        public string Stage => _stage;
        public string Current => _current;
        public int FilesDone => Volatile.Read(ref _done);
        public int FilesTotal => Volatile.Read(ref _total);
        public int FilesFailed => Volatile.Read(ref _failed);
        public long Bytes => Interlocked.Read(ref _bytes);
        public float Fraction => FilesTotal > 0 ? Math.Min(1f, FilesDone / (float)FilesTotal) : 0f;

        internal void SetStage(string s) => _stage = s;
        internal void SetCurrent(string s) => _current = s;
        internal void AddTotal(int n) => Interlocked.Add(ref _total, n);
        internal void AddBytes(long n) => Interlocked.Add(ref _bytes, n);

        internal void FileFinished(bool ok)
        {
            Interlocked.Increment(ref _done);
            if (!ok) Interlocked.Increment(ref _failed);
        }

        public override string ToString() =>
            $"{Stage}: {FilesDone}/{FilesTotal} files, {IoUtil.FormatBytes(Bytes)}" + (FilesFailed > 0 ? $", {FilesFailed} failed" : "");
    }

    /// <summary>
    /// Downloads a space into a cache folder: metadata → mesh → mesh textures → panorama faces, then writes manifest.json.
    /// Resumable: files that already exist are skipped, partial files never replace complete ones.
    /// </summary>
    public sealed class SpaceDownloader
    {
        readonly IHttpTransport _http;
        readonly ILogSink _log;

        public DownloadStatus Status { get; } = new DownloadStatus();

        public SpaceDownloader(IHttpTransport http, ILogSink log)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _log = log ?? NullLog.Instance;
        }

        public Task<SpaceManifest> DownloadAsync(string modelId, string spaceDir, DownloadOptions options, CancellationToken ct)
        {
            var graph = new MatterportGraphClient(_http, _log);
            async Task<JsonValue> Fetch(CancellationToken token)
            {
                var outcome = await graph.FetchModelAsync(modelId, token).ConfigureAwait(false);
                WriteDebug(spaceDir, "graph-query.graphql", outcome.FinalQuery);
                WriteDebug(spaceDir, "graph-response.json", outcome.RawResponse);
                if (outcome.PrunedFields.Count > 0)
                    WriteDebug(spaceDir, "graph-pruned-fields.txt", string.Join("\n", outcome.PrunedFields));
                return outcome.Data;
            }
            return RunAsync(modelId, Fetch, true, spaceDir, options ?? new DownloadOptions(), MatterportLink.ShowcaseUrl(modelId), ct);
        }

        /// <summary>
        /// Imports the JSON saved by the browser export snippet (or any raw GraphQL response for the model query).
        /// The signed asset links inside expire after a while, so import soon after exporting.
        /// </summary>
        public Task<SpaceManifest> ImportExportAsync(string exportJson, string cacheRoot, DownloadOptions options, CancellationToken ct)
        {
            var json = JsonValue.Parse(exportJson);
            JsonValue data = json["response"]["data"];
            if (!data.IsObject) data = json["data"];
            if (!data.IsObject && json["model"].IsObject) data = json;
            if (!data.IsObject || !data["model"].IsObject)
                throw new MatterportException("This file does not look like a Matterportable browser export (no data.model found).");

            var modelId = data["model"]["id"].AsString() ?? json["modelId"].AsString();
            if (string.IsNullOrEmpty(modelId)) throw new MatterportException("The export does not contain a model ID.");
            var spaceDir = CacheLayout.SpaceDir(cacheRoot, modelId);
            WriteDebug(spaceDir, "graph-response.json", exportJson);
            return RunAsync(modelId, _ => Task.FromResult(data), false, spaceDir, options ?? new DownloadOptions(),
                MatterportLink.ShowcaseUrl(modelId), ct);
        }

        sealed class Item
        {
            public string Key, RelPath;
            public List<string> Urls = new List<string>();
            public bool Ok, AuthFailed;
            public int LastStatus;
            public string LastError;
        }

        sealed class TierPlan
        {
            public PanoTier Tier;
            public List<Item> Items = new List<Item>();
        }

        async Task<SpaceManifest> RunAsync(string modelIdHint, Func<CancellationToken, Task<JsonValue>> fetch, bool canRefresh,
            string spaceDir, DownloadOptions o, string sourceUrl, CancellationToken ct)
        {
            Directory.CreateDirectory(spaceDir);
            Status.SetStage("Fetching space information");
            var remote = RemoteModelParser.Parse(await fetch(ct).ConfigureAwait(false), _log);
            _log.Info($"\"{remote.Name}\": {remote.Sweeps.Count} panoramas, {remote.Floors.Count} floors, " +
                      $"meshes [{string.Join(", ", remote.Meshes.ConvertAll(m => m.ToString()))}], " +
                      $"textures [{string.Join(", ", remote.TextureSets.ConvertAll(t => t.ToString()))}].");
            if (remote.Sweeps.Count == 0)
                throw new MatterportException("Matterport returned no placed panoramas for this space (see debug/graph-response.json).");

            var manifest = new SpaceManifest
            {
                ModelId = remote.ModelId ?? modelIdHint,
                Name = remote.Name,
                SourceUrl = sourceUrl,
                CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Frame = CoordinateFrame.ZUpRightHanded,
                Floors = remote.Floors,
                TilesetUrls = remote.TilesetUrls,
            };

            // 1) Mesh first: texture names come from the mesh's material names.
            var materials = new List<string>();
            var mesh = remote.ChooseMesh();
            if (mesh != null)
            {
                var rel = CacheLayout.MeshDir + "/" + CacheLayout.SafeName(FileNameOf(mesh.Filename ?? mesh.Url, "mesh.dam"));
                var item = new Item { Key = "mesh", RelPath = rel };
                item.Urls.Add(mesh.Url);
                Status.SetStage("Downloading mesh");
                Status.AddTotal(1);
                await RunItemsAsync(new List<Item> { item }, spaceDir, o, ct).ConfigureAwait(false);
                if (item.Ok)
                {
                    manifest.Mesh = new MeshEntry { File = rel, Format = string.IsNullOrEmpty(mesh.Format) ? "dam" : mesh.Format, Resolution = mesh.Resolution };
                    try
                    {
                        materials = DamDecoder.ReadMaterialNames(File.ReadAllBytes(CacheLayout.Resolve(spaceDir, rel)));
                    }
                    catch (Exception e)
                    {
                        Warn(manifest, "Could not read material names from the mesh: " + e.Message);
                    }
                }
                else Warn(manifest, $"Mesh download failed ({Describe(item)}). The space will be panorama-only.");
            }
            else
            {
                Warn(manifest, remote.TilesetUrls.Count > 0
                    ? "This space only offers streamed mesh tiles, which are not supported yet. It will be panorama-only."
                    : "No mesh is available for this space. It will be panorama-only.");
            }

            // 2) Textures and panoramas.
            var texSet = remote.ChooseTextures(mesh, o.TextureQuality);
            var texItems = PlanTextures(texSet, materials);
            if (materials.Count > 0 && texSet == null) Warn(manifest, "No mesh texture set found; the mesh will be untextured.");

            var sweeps = remote.Sweeps;
            if (o.MaxSweeps > 0 && sweeps.Count > o.MaxSweeps) sweeps = sweeps.GetRange(0, o.MaxSweeps);
            var panoPlans = new Dictionary<RemoteSweep, List<TierPlan>>();
            var all = new List<Item>(texItems);
            if (o.Panoramas)
            {
                foreach (var s in sweeps)
                {
                    var plans = PlanPanos(s, o.PanoSizes, manifest);
                    panoPlans[s] = plans;
                    foreach (var p in plans) all.AddRange(p.Items);
                }
            }

            Status.SetStage("Downloading textures and panoramas");
            Status.AddTotal(all.Count);
            await RunItemsAsync(all, spaceDir, o, ct).ConfigureAwait(false);

            // 3) Signed links can expire mid-download; refresh them once and retry what failed.
            var failed = all.FindAll(i => !i.Ok);
            if (canRefresh && failed.Exists(i => i.AuthFailed))
            {
                Status.SetStage("Refreshing expired links");
                _log.Info($"{failed.Count} downloads were refused; refreshing the signed links and retrying…");
                try
                {
                    var fresh = RemoteModelParser.Parse(await fetch(ct).ConfigureAwait(false), NullLog.Instance);
                    var freshUrls = new Dictionary<string, List<string>>();
                    foreach (var it in PlanTextures(fresh.ChooseTextures(fresh.ChooseMesh(), o.TextureQuality), materials))
                        freshUrls[it.Key] = it.Urls;
                    foreach (var s in fresh.Sweeps)
                        foreach (var p in PlanPanos(s, o.PanoSizes, null))
                            foreach (var it in p.Items) freshUrls[it.Key] = it.Urls;
                    foreach (var it in failed)
                    {
                        if (!freshUrls.TryGetValue(it.Key, out var urls)) continue;
                        it.Urls = urls;
                        it.AuthFailed = false;
                    }
                    Status.AddTotal(failed.Count);
                    await RunItemsAsync(failed, spaceDir, o, ct).ConfigureAwait(false);
                }
                catch (MatterportException e)
                {
                    _log.Warn("Refreshing links failed: " + e.Message);
                }
            }

            // 4) Manifest.
            foreach (var it in texItems)
            {
                if (it.Ok) manifest.Textures.Add(new TextureEntry { Material = it.Key.Substring(4), File = it.RelPath });
                else Warn(manifest, $"Texture for material '{it.Key.Substring(4)}' failed ({Describe(it)}).");
            }

            foreach (var s in sweeps)
            {
                var entry = new SweepEntry
                {
                    Id = s.Id,
                    Index = s.Index,
                    Label = s.Label ?? "",
                    FloorId = s.FloorId,
                    RoomId = s.RoomId,
                    Position = s.Position,
                    Rotation = s.Rotation,
                    Neighbors = s.Neighbors,
                };
                if (panoPlans.TryGetValue(s, out var plans))
                {
                    foreach (var p in plans)
                    {
                        if (!p.Items.TrueForAll(i => i.Ok)) continue;
                        var firstPath = CacheLayout.Resolve(spaceDir, p.Items[0].RelPath);
                        if (ImageInfo.TryReadSize(firstPath, out int w, out _) && w > 0) p.Tier.Size = w * p.Tier.Tiles;
                        if (p.Tier.Size <= 0) p.Tier.Size = 512;
                        if (!entry.Tiers.Exists(t => t.Size == p.Tier.Size)) entry.Tiers.Add(p.Tier);
                    }
                }
                entry.Tiers.Sort((a, b) => a.Size.CompareTo(b.Size));
                if (entry.Tiers.Count == 0 && o.Panoramas)
                {
                    Warn(manifest, $"Sweep {s.Index} ({s.Id}) has no complete panorama and was left out.");
                    continue;
                }
                manifest.Sweeps.Add(entry);
            }

            manifest.Save(spaceDir);
            Status.SetStage("Done");
            Status.SetCurrent("");
            _log.Info($"Saved {manifest.Sweeps.Count} sweeps, {manifest.Textures.Count} textures, mesh: {(manifest.Mesh != null ? "yes" : "no")} " +
                      $"→ {spaceDir} ({IoUtil.FormatBytes(IoUtil.DirectorySize(spaceDir))}).");
            return manifest;
        }

        List<Item> PlanTextures(RemoteTextureSet set, List<string> materials)
        {
            var items = new List<Item>();
            if (set == null || string.IsNullOrEmpty(set.UrlTemplate)) return items;
            var seen = new HashSet<string>();
            foreach (var mat in materials)
            {
                if (string.IsNullOrEmpty(mat) || !seen.Add(mat)) continue;
                var item = new Item
                {
                    Key = "tex:" + mat,
                    RelPath = CacheLayout.TexturesDir + "/" + CacheLayout.SafeName(TextureFileName(mat, set)),
                    Urls = TextureUrlCandidates(set.UrlTemplate, mat),
                };
                if (item.Urls.Count > 0) items.Add(item);
            }
            return items;
        }

        static string TextureFileName(string material, RemoteTextureSet set)
        {
            if (Regex.IsMatch(material, @"\.(jpe?g|png)$", RegexOptions.IgnoreCase)) return material;
            var ext = string.IsNullOrEmpty(set.Format) || set.Format == "jpeg" ? "jpg" : set.Format;
            return material + "." + ext;
        }

        /// <summary>Candidate URLs for a texture: Matterport templates use a &lt;texture&gt; placeholder for the texture index.</summary>
        public static List<string> TextureUrlCandidates(string template, string material)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(template)) return result;
            var name = Regex.Replace(material, @"\.(jpe?g|png)$", "", RegexOptions.IgnoreCase);
            var digits = Regex.Match(name, @"(\d+)$");
            var values = new List<string>();
            if (digits.Success)
            {
                values.Add(digits.Groups[1].Value);
                values.Add(int.Parse(digits.Groups[1].Value, CultureInfo.InvariantCulture).ToString("000", CultureInfo.InvariantCulture));
                values.Add(int.Parse(digits.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            }
            values.Add(name);
            values.Add(material);
            foreach (var placeholder in new[] { "<texture>", "%3Ctexture%3E", "%3ctexture%3e", "{texture}" })
            {
                if (template.IndexOf(placeholder, StringComparison.Ordinal) < 0) continue;
                foreach (var v in values)
                {
                    var url = template.Replace(placeholder, Uri.EscapeDataString(v));
                    if (!result.Contains(url)) result.Add(url);
                }
            }
            return result;
        }

        List<TierPlan> PlanPanos(RemoteSweep sweep, int[] sizes, SpaceManifest manifestForWarnings)
        {
            var chosen = new List<RemoteSkybox>();
            var wanted = (int[])sizes.Clone();
            Array.Sort(wanted);
            foreach (var want in wanted)
            {
                RemoteSkybox pick = null;
                foreach (var sb in sweep.Skyboxes)
                    if (sb.Size > 0 && sb.Size <= want && (pick == null || sb.Size > pick.Size)) pick = sb;
                if (pick == null)
                    foreach (var sb in sweep.Skyboxes)
                        if (sb.Size > 0 && (pick == null || sb.Size < pick.Size)) pick = sb;
                if (pick == null && sweep.Skyboxes.Count > 0) pick = sweep.Skyboxes[0];
                if (pick != null && !chosen.Contains(pick)) chosen.Add(pick);
            }

            var plans = new List<TierPlan>();
            for (int k = 0; k < chosen.Count; k++)
            {
                var sb = chosen[k];
                string template = sb.IsTiled ? sb.TileUrlTemplate : sb.UrlTemplate;
                if (!HasPlaceholder(template, "face"))
                {
                    if (manifestForWarnings != null)
                        Warn(manifestForWarnings, $"Skybox template for sweep {sweep.Id} has no <face> placeholder: {SelfHealingQuery.Truncate(template, 120)}");
                    continue;
                }
                var ext = ExtensionOf(template);
                int sizeLabel = sb.Size > 0 ? sb.Size : 1000 + k;
                var tier = new PanoTier
                {
                    Size = sb.Size,
                    Directory = CacheLayout.PanoTierDir(sweep.Id, sizeLabel),
                    Extension = ext,
                    Tiles = sb.IsTiled ? TilesPerSide(sb) : 1,
                };
                var plan = new TierPlan { Tier = tier };
                for (int face = 0; face < 6; face++)
                {
                    if (tier.Tiles == 1)
                    {
                        var it = new Item { Key = $"pano:{sweep.Id}:{sizeLabel}:{face}", RelPath = tier.FacePath(face) };
                        it.Urls.Add(Fill(template, face, 0, 0));
                        plan.Items.Add(it);
                    }
                    else
                    {
                        for (int y = 0; y < tier.Tiles; y++)
                        for (int x = 0; x < tier.Tiles; x++)
                        {
                            var it = new Item { Key = $"pano:{sweep.Id}:{sizeLabel}:{face}:{x}:{y}", RelPath = tier.TilePath(face, x, y) };
                            it.Urls.Add(Fill(template, face, x, y));
                            plan.Items.Add(it);
                        }
                    }
                }
                plans.Add(plan);
            }
            return plans;
        }

        static int TilesPerSide(RemoteSkybox sb)
        {
            if (sb.Size > 0 && sb.TileResolution > 0) return Math.Max(1, sb.Size / sb.TileResolution);
            if (sb.TileCount > 0)
            {
                int perFace = sb.TileCount % 6 == 0 ? sb.TileCount / 6 : sb.TileCount;
                return Math.Max(1, (int)Math.Round(Math.Sqrt(perFace)));
            }
            return Math.Max(1, sb.Size / 512);
        }

        static readonly string[][] Placeholders =
        {
            new[] { "<face>", "%3Cface%3E", "%3cface%3e", "{face}" },
            new[] { "<x>", "%3Cx%3E", "%3cx%3e", "{x}", "<tileX>", "<col>" },
            new[] { "<y>", "%3Cy%3E", "%3cy%3e", "{y}", "<tileY>", "<row>" },
        };

        static bool HasPlaceholder(string template, string which)
        {
            if (string.IsNullOrEmpty(template)) return false;
            var set = which == "face" ? Placeholders[0] : which == "x" ? Placeholders[1] : Placeholders[2];
            foreach (var p in set) if (template.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public static string Fill(string template, int face, int x, int y)
        {
            var s = template;
            foreach (var p in Placeholders[0]) s = s.Replace(p, face.ToString(CultureInfo.InvariantCulture));
            foreach (var p in Placeholders[1]) s = s.Replace(p, x.ToString(CultureInfo.InvariantCulture));
            foreach (var p in Placeholders[2]) s = s.Replace(p, y.ToString(CultureInfo.InvariantCulture));
            return s;
        }

        static string ExtensionOf(string url)
        {
            var path = url;
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            var m = Regex.Match(path, @"\.([A-Za-z0-9]{3,4})$");
            var ext = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "jpg";
            return ext == "jpeg" ? "jpg" : ext;
        }

        static string FileNameOf(string urlOrName, string fallback)
        {
            if (string.IsNullOrEmpty(urlOrName)) return fallback;
            var s = urlOrName;
            int q = s.IndexOf('?');
            if (q >= 0) s = s.Substring(0, q);
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            s = Uri.UnescapeDataString(s);
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        async Task RunItemsAsync(List<Item> items, string spaceDir, DownloadOptions o, CancellationToken ct)
        {
            using (var gate = new SemaphoreSlim(Math.Max(1, o.MaxConcurrency)))
            {
                var tasks = new List<Task>(items.Count);
                foreach (var item in items)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        try { await DownloadItemAsync(item, spaceDir, o, ct).ConfigureAwait(false); }
                        finally { gate.Release(); }
                    }, ct));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        async Task DownloadItemAsync(Item item, string spaceDir, DownloadOptions o, CancellationToken ct)
        {
            var path = CacheLayout.Resolve(spaceDir, item.RelPath);
            if (!o.Overwrite && IoUtil.NonEmptyFileExists(path))
            {
                item.Ok = true;
                Status.FileFinished(true);
                return;
            }
            Status.SetCurrent(item.RelPath);
            bool allAuth = item.Urls.Count > 0;
            foreach (var url in item.Urls)
            {
                bool authFailure = false;
                for (int attempt = 1; attempt <= Math.Max(1, o.MaxRetries); attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = await _http.GetToFileAsync(url, path, n => Status.AddBytes(n), ct).ConfigureAwait(false);
                    if (r.Ok)
                    {
                        item.Ok = true;
                        item.AuthFailed = false;
                        Status.FileFinished(true);
                        return;
                    }
                    item.LastStatus = r.Status;
                    item.LastError = r.Error;
                    if (r.IsAuthFailure) { authFailure = true; break; }
                    if (!r.IsTransient) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt - 1)), ct).ConfigureAwait(false);
                }
                allAuth &= authFailure;
            }
            item.AuthFailed = allAuth;
            Status.FileFinished(false);
        }

        static string Describe(Item i) => i.LastError ?? ("HTTP " + i.LastStatus);

        void Warn(SpaceManifest manifest, string message)
        {
            _log.Warn(message);
            manifest?.Warnings.Add(message);
        }

        static void WriteDebug(string spaceDir, string name, string text)
        {
            if (text == null) return;
            try { IoUtil.WriteAllTextAtomic(Path.Combine(spaceDir, CacheLayout.DebugDir, name), text); }
            catch (IOException) { /* diagnostics only */ }
        }
    }
}
