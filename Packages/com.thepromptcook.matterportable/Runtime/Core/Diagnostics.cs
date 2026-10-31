using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Matterportable.Core
{
    /// <summary>
    /// One plain-text report with everything needed to debug a cached space: manifest summary, calibration,
    /// pruned GraphQL fields, the last load log, the mesh structure dump and a sample of the raw API response.
    /// Signed download tokens are redacted, so the report is safe to paste into a chat or issue.
    /// Unity: inspector / import window "Copy Diagnostics". CLI: <c>matterportable diagnose &lt;space dir&gt;</c>.
    /// </summary>
    public static class Diagnostics
    {
        public const string FileName = "diagnostics.txt";

        public static string Report(string spaceDir, string environment = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Matterportable diagnostics");
            sb.AppendLine("Generated: " + DateTime.UtcNow.ToString("u"));
            if (!string.IsNullOrEmpty(environment)) sb.AppendLine("Environment: " + environment);
            sb.AppendLine("Space folder: " + spaceDir);
            sb.AppendLine();

            try
            {
                var m = SpaceManifest.Load(spaceDir);
                sb.AppendLine($"## Manifest\n{m.Name} ({m.ModelId}), source {m.Source}, created {m.CreatedUtc}, {IoUtil.FormatBytes(IoUtil.DirectorySize(spaceDir))}");
                sb.AppendLine($"Mesh: {(m.Mesh == null ? "none" : m.Mesh.File + " (" + m.Mesh.Format + " " + m.Mesh.Resolution + ")")}");
                sb.AppendLine($"Textures: {m.Textures.Count}" + (m.Textures.Count > 0 ? $" (first: {m.Textures[0].Material} -> {m.Textures[0].File})" : ""));
                sb.AppendLine($"Floors: {m.Floors.Count}, sweeps: {m.Sweeps.Count}");
                var tiers = new System.Collections.Generic.SortedDictionary<string, int>();
                foreach (var s in m.Sweeps)
                    foreach (var t in s.Tiers)
                    {
                        var key = t.Size + (t.Tiles > 1 ? $"px ({t.Tiles}x{t.Tiles} tiles)" : "px") + " ." + t.Extension;
                        tiers[key] = tiers.TryGetValue(key, out int n) ? n + 1 : 1;
                    }
                foreach (var kv in tiers) sb.AppendLine($"  panorama tier {kv.Key}: {kv.Value} sweeps");
                if (m.Sweeps.Count > 0)
                {
                    var s0 = m.Sweeps[0];
                    sb.AppendLine($"  sweep 0: id {s0.Id}, position [{string.Join(", ", s0.Position)}], rotation [{(s0.Rotation == null ? "none" : string.Join(", ", s0.Rotation))}], neighbors {s0.Neighbors.Count}");
                }
                foreach (var w in m.Warnings) sb.AppendLine("  warning: " + w);
            }
            catch (Exception e) { sb.AppendLine("Manifest unreadable: " + e.Message); }

            Section(sb, "calibration.json", Path.Combine(spaceDir, SpaceCalibration.FileName), 80, false);
            var debug = Path.Combine(spaceDir, CacheLayout.DebugDir);
            Section(sb, "debug/graph-pruned-fields.txt", Path.Combine(debug, "graph-pruned-fields.txt"), 60, false);
            Section(sb, "debug/load.log (last lines)", Path.Combine(debug, "load.log"), 150, true);
            Section(sb, "debug/dam-structure.txt", Path.Combine(debug, "dam-structure.txt"), 120, false);
            sb.AppendLine("\n## GraphQL response sample");
            sb.AppendLine(ResponseSample(Path.Combine(debug, "graph-response.json")));
            return Redact(sb.ToString());
        }

        static void Section(StringBuilder sb, string title, string path, int maxLines, bool tail)
        {
            if (!File.Exists(path)) return;
            sb.AppendLine("\n## " + title);
            var lines = File.ReadAllLines(path);
            int start = tail ? Math.Max(0, lines.Length - maxLines) : 0;
            int end = tail ? lines.Length : Math.Min(lines.Length, maxLines);
            for (int i = start; i < end; i++) sb.AppendLine(lines[i]);
            if (lines.Length > maxLines) sb.AppendLine($"… ({lines.Length} lines total)");
        }

        /// <summary>Model fields, assets and the first location of the saved API response.</summary>
        static string ResponseSample(string path)
        {
            if (!File.Exists(path)) return "(no debug/graph-response.json)";
            try
            {
                var json = JsonValue.Parse(File.ReadAllText(path));
                var model = json["data"]["model"];
                if (!model.IsObject) model = json["response"]["data"]["model"];
                if (!model.IsObject) return "(response has no data.model)";
                var sample = JsonValue.NewObject()
                    .Set("modelFields", JsonValue.FromArray(model.Keys))
                    .Set("assets", model["assets"])
                    .Set("floors", model["floors"])
                    .Set("locationCount", model["locations"].Count)
                    .Set("firstLocation", model["locations"][0]);
                return sample.ToJson(true);
            }
            catch (FormatException e) { return "(unparseable: " + e.Message + ")"; }
        }

        static readonly Regex SignedParams = new Regex(@"([?&](?:t|token|sig|signature|Signature|Expires|Key-Pair-Id|Policy|X-Amz-[A-Za-z]+)=)[^&""\s]+");

        /// <summary>Removes signed URL parameters (short-lived access tokens).</summary>
        public static string Redact(string text) => SignedParams.Replace(text, "$1…");
    }
}
