using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Matterportable.Core
{
    /// <summary>
    /// The query sent to the GraphQL endpoint the Matterport web viewer itself uses for public spaces.
    /// Every non-essential field is optional: unknown fields are pruned automatically (see <see cref="SelfHealingQuery"/>).
    /// </summary>
    public static class MatterportQueries
    {
        public const string ShowcaseGraphEndpoint = "https://my.matterport.com/api/mp/models/graph";

        static GqlField Point(string name) => new GqlField(name).Leaves("x", "y", "z");
        static GqlField Rotation(string name) => new GqlField(name).Leaves("x", "y", "z", "w");

        static GqlField Pano(string name) => new GqlField(name).Add(
            new GqlField("id"),
            new GqlField("label"),
            new GqlField("placement"),
            new GqlField("source"),
            Point("position"),
            Rotation("rotation"),
            new GqlField("resolutions"),
            new GqlField("skyboxes").Leaves("id", "resolution", "status", "urlTemplate",
                "tileResolution", "tileCount", "tileUrlTemplate", "validUntil"));

        public static GqlDocument ModelQuery(string modelId)
        {
            var root = new GqlField("model", optional: false, arguments: "id: " + JsonValue.Quote(modelId));
            root.Required("id");
            root.Leaves("name");
            root.Add(
                new GqlField("assets").Add(
                    new GqlField("meshes").Leaves("id", "status", "filename", "format", "resolution", "url", "validUntil"),
                    new GqlField("textures").Leaves("id", "status", "format", "resolution", "quality", "urlTemplate", "validUntil"),
                    new GqlField("tilesets").Leaves("status", "url", "urlTemplate", "validUntil", "tilesetVersion", "tilesetDepth", "tilesetPreset")),
                new GqlField("floors").Leaves("id", "label", "sequence"),
                new GqlField("locations").Add(
                    new GqlField("id"),
                    new GqlField("index"),
                    new GqlField("label"),
                    new GqlField("neighbors"),
                    new GqlField("floor").Leaves("id"),
                    new GqlField("room").Leaves("id"),
                    Point("position"),
                    Pano("pano")));
            return new GqlDocument("MatterportableModel", root);
        }
    }

    public sealed class RemoteMesh
    {
        public string Id, Format, Resolution, Url, Filename, Status;
        public override string ToString() => $"{Format} {Resolution} ({Filename})";
    }

    public sealed class RemoteTextureSet
    {
        public string Id, Format, Resolution, Quality, UrlTemplate, Status;
        public override string ToString() => $"{Format} {Resolution} {Quality}";
    }

    public sealed class RemoteSkybox
    {
        public string Resolution, Status, UrlTemplate, TileUrlTemplate;
        public int Size, TileResolution, TileCount;
        public bool IsTiled => string.IsNullOrEmpty(UrlTemplate) && !string.IsNullOrEmpty(TileUrlTemplate);
    }

    public sealed class RemoteSweep
    {
        public string Id, Label, FloorId, RoomId, Placement;
        public int Index;
        public double[] Position, Rotation;
        public List<string> Neighbors = new List<string>();
        public List<RemoteSkybox> Skyboxes = new List<RemoteSkybox>();
    }

    public sealed class RemoteModel
    {
        public string ModelId, Name;
        public List<RemoteMesh> Meshes = new List<RemoteMesh>();
        public List<RemoteTextureSet> TextureSets = new List<RemoteTextureSet>();
        public List<string> TilesetUrls = new List<string>();
        /// <summary>URL of any tileset file, with &lt;file&gt; for its relative path (signed, short-lived).</summary>
        public string TileTemplate;
        public List<FloorEntry> Floors = new List<FloorEntry>();
        public List<RemoteSweep> Sweeps = new List<RemoteSweep>();
        public int SkippedSweeps;

        public RemoteMesh ChooseMesh()
        {
            RemoteMesh best = null;
            int bestScore = int.MinValue;
            foreach (var m in Meshes)
            {
                if (string.IsNullOrEmpty(m.Url)) continue;
                int score = 0;
                if (m.Format == "dam" || (m.Filename ?? m.Url).IndexOf(".dam", StringComparison.OrdinalIgnoreCase) >= 0) score += 1000;
                if (TextureSets.Exists(t => t.Resolution == m.Resolution)) score += 500;
                score += Math.Min(400, ResolutionNumber(m.Resolution) / 1000);
                if (score > bestScore) { bestScore = score; best = m; }
            }
            return best;
        }

        public RemoteTextureSet ChooseTextures(RemoteMesh mesh, string preferredQuality)
        {
            RemoteTextureSet best = null;
            int bestScore = int.MinValue;
            foreach (var t in TextureSets)
            {
                if (string.IsNullOrEmpty(t.UrlTemplate)) continue;
                int score = 0;
                if (mesh != null && t.Resolution == mesh.Resolution) score += 1000;
                if (string.Equals(t.Quality, preferredQuality, StringComparison.OrdinalIgnoreCase)) score += 100;
                else if (string.Equals(t.Quality, "high", StringComparison.OrdinalIgnoreCase)) score += 50;
                if (t.Format == "jpg" || t.Format == "jpeg") score += 10;
                if (score > bestScore) { bestScore = score; best = t; }
            }
            return best;
        }

        static int ResolutionNumber(string r)
        {
            if (string.IsNullOrEmpty(r)) return 0;
            var m = Regex.Match(r, @"(\d+)\s*([kKmM]?)");
            if (!m.Success) return 0;
            int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var suffix = m.Groups[2].Value.ToLowerInvariant();
            return suffix == "k" ? n * 1000 : suffix == "m" ? n * 1000000 : n;
        }
    }

    public static class RemoteModelParser
    {
        public static RemoteModel Parse(JsonValue data, ILogSink log)
        {
            var m = data["model"];
            if (m.IsNull || !m.IsObject)
                throw new MatterportException("Matterport returned no model. The space may be private, unpublished, or the ID is wrong.");

            var model = new RemoteModel
            {
                ModelId = m["id"].AsString(),
                Name = m["name"].AsString() ?? m["id"].AsString(),
            };

            foreach (var mesh in m["assets"]["meshes"].Items)
            {
                model.Meshes.Add(new RemoteMesh
                {
                    Id = mesh["id"].AsString(),
                    Format = (mesh["format"].AsString() ?? "").ToLowerInvariant(),
                    Resolution = mesh["resolution"].AsString(),
                    Url = mesh["url"].AsString(),
                    Filename = mesh["filename"].AsString(),
                    Status = mesh["status"].AsString(),
                });
            }

            foreach (var t in m["assets"]["textures"].Items)
            {
                model.TextureSets.Add(new RemoteTextureSet
                {
                    Id = t["id"].AsString(),
                    Format = (t["format"].AsString() ?? "").ToLowerInvariant(),
                    Resolution = t["resolution"].AsString(),
                    Quality = t["quality"].AsString(),
                    UrlTemplate = t["urlTemplate"].AsString(),
                    Status = t["status"].AsString(),
                });
            }

            foreach (var ts in m["assets"]["tilesets"].Items)
            {
                var u = ts["url"].AsString() ?? ts["urlTemplate"].AsString();
                if (!string.IsNullOrEmpty(u)) model.TilesetUrls.Add(u);
                var template = ts["urlTemplate"].AsString();
                if (model.TileTemplate == null && template != null && template.Contains("<file>")) model.TileTemplate = template;
            }

            int floorOrdinal = 0;
            foreach (var f in m["floors"].Items)
            {
                model.Floors.Add(new FloorEntry
                {
                    Id = f["id"].AsString() ?? floorOrdinal.ToString(CultureInfo.InvariantCulture),
                    Index = f["sequence"].AsInt(f["index"].AsInt(floorOrdinal)),
                    Label = f["label"].AsString() ?? "",
                });
                floorOrdinal++;
            }
            model.Floors.Sort((a, b) => a.Index.CompareTo(b.Index));

            int ordinal = 0;
            foreach (var loc in m["locations"].Items)
            {
                var sweep = ParseLocation(loc, ordinal++);
                if (sweep == null) { model.SkippedSweeps++; continue; }
                model.Sweeps.Add(sweep);
            }
            model.Sweeps.Sort((a, b) => a.Index.CompareTo(b.Index));

            if (model.SkippedSweeps > 0)
                log.Info($"Skipped {model.SkippedSweeps} locations without a placed panorama (e.g. unaligned 360° views).");
            return model;
        }

        static RemoteSweep ParseLocation(JsonValue loc, int ordinal)
        {
            var pano = loc["pano"];
            if (!pano.IsObject) return null;

            var placement = pano["placement"].AsString();
            if (string.Equals(placement, "unplaced", StringComparison.OrdinalIgnoreCase)) return null;

            var position = ReadPoint(pano["position"]) ?? ReadPoint(loc["position"]);
            if (position == null) return null;

            var sweep = new RemoteSweep
            {
                Id = loc["id"].AsString() ?? pano["id"].AsString() ?? ordinal.ToString(CultureInfo.InvariantCulture),
                Index = loc["index"].AsInt(ordinal),
                Label = loc["label"].AsString() ?? pano["label"].AsString() ?? "",
                FloorId = loc["floor"]["id"].AsString(),
                RoomId = loc["room"]["id"].AsString(),
                Placement = placement,
                Position = position,
                Rotation = ReadQuat(pano["rotation"]),
                Neighbors = loc["neighbors"].AsStringList(),
            };

            foreach (var s in pano["skyboxes"].Items)
            {
                var sb = new RemoteSkybox
                {
                    Resolution = s["resolution"].AsString(),
                    Status = s["status"].AsString(),
                    UrlTemplate = s["urlTemplate"].AsString(),
                    TileUrlTemplate = s["tileUrlTemplate"].AsString(),
                    TileResolution = ResolutionToSize(s["tileResolution"].AsString()),
                    TileCount = s["tileCount"].AsInt(0),
                };
                sb.Size = ResolutionToSize(sb.Resolution);
                if (string.IsNullOrEmpty(sb.UrlTemplate) && string.IsNullOrEmpty(sb.TileUrlTemplate)) continue;
                var status = (sb.Status ?? "").ToLowerInvariant();
                if (status.Contains("unavailable") || status.Contains("fail") || status.Contains("pending")) continue;
                sweep.Skyboxes.Add(sb);
            }
            return sweep.Skyboxes.Count > 0 ? sweep : null;
        }

        static double[] ReadPoint(JsonValue p)
        {
            if (p.IsArray)
            {
                var a = p.AsDoubleArray();
                return a != null && a.Length >= 3 ? new[] { a[0], a[1], a[2] } : null;
            }
            if (!p.IsObject || !p.Has("x")) return null;
            return new[] { p["x"].AsDouble(), p["y"].AsDouble(), p["z"].AsDouble() };
        }

        static double[] ReadQuat(JsonValue q)
        {
            if (q.IsArray)
            {
                var a = q.AsDoubleArray();
                return a != null && a.Length >= 4 ? new[] { a[0], a[1], a[2], a[3] } : null;
            }
            if (!q.IsObject || !q.Has("w")) return null;
            return new[] { q["x"].AsDouble(), q["y"].AsDouble(), q["z"].AsDouble(), q["w"].AsDouble() };
        }

        /// <summary>Maps Matterport resolution names ("low", "high", "2k", "4k", "512") to face sizes in pixels.</summary>
        public static int ResolutionToSize(string res)
        {
            if (string.IsNullOrEmpty(res)) return 0;
            switch (res.Trim().ToLowerInvariant())
            {
                case "low": case "512": return 512;
                case "high": case "1k": case "1024": return 1024;
                case "2k": case "2048": return 2048;
                case "4k": case "4096": return 4096;
                case "8k": return 8192;
            }
            var m = Regex.Match(res, @"(\d+)\s*k", RegexOptions.IgnoreCase);
            if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1024;
            return int.TryParse(res, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        }
    }

    public sealed class MatterportGraphClient
    {
        public string Endpoint = MatterportQueries.ShowcaseGraphEndpoint;
        readonly IHttpTransport _http;
        readonly ILogSink _log;

        public MatterportGraphClient(IHttpTransport http, ILogSink log)
        {
            _http = http;
            _log = log ?? NullLog.Instance;
        }

        public Task<QueryOutcome> FetchModelAsync(string modelId, CancellationToken ct) =>
            SelfHealingQuery.RunAsync(MatterportQueries.ModelQuery(modelId), PostAsync, _log, ct);

        async Task<string> PostAsync(string body, CancellationToken ct)
        {
            HttpResult r = null;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                r = await _http.PostJsonAsync(Endpoint, body, ct).ConfigureAwait(false);
                if (!r.IsTransient || LooksLikeGraphQL(r)) break;
                _log.Info($"Matterport API: {(r.Error ?? "HTTP " + r.Status)}; retrying ({attempt}/4)…");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct).ConfigureAwait(false);
            }

            if (r.Error != null) throw new MatterportException("Could not reach Matterport: " + r.Error);
            var text = r.Text;
            if (IsBotChallenge(r.Status, text))
                throw new MatterportException("Matterport's bot protection blocked the request from this network; try again later or from another network.");
            if (LooksLikeGraphQL(r)) return text;
            if (r.IsAuthFailure)
                throw new MatterportException($"Matterport refused access (HTTP {r.Status}). The space is probably private or requires a login.");
            throw new MatterportException($"Unexpected response from Matterport (HTTP {r.Status}): {SelfHealingQuery.Truncate(text, 300)}");
        }

        static bool LooksLikeGraphQL(HttpResult r)
        {
            if (r.Body == null || r.Body.Length == 0) return false;
            var t = r.Text.TrimStart();
            return t.StartsWith("{") && (t.Contains("\"data\"") || t.Contains("\"errors\""));
        }

        public static bool IsBotChallenge(int status, string body) =>
            (status == 403 || status == 503 || status == 429) && body != null &&
            (body.Contains("Just a moment") || body.Contains("challenge-platform") || body.Contains("cf-chl") || body.Contains("Attention Required"));
    }
}
