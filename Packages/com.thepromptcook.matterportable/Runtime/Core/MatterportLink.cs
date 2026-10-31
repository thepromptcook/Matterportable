using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Matterportable.Core
{
    /// <summary>
    /// Turns anything a user might paste (share links, embed links, discover pages, raw IDs) into a model ID.
    /// </summary>
    public static class MatterportLink
    {
        // Matterport model ("space") IDs are 11 alphanumeric characters, e.g. rGMMnZDh3Tf.
        static readonly Regex StrictId = new Regex("^[A-Za-z0-9]{11}$", RegexOptions.CultureInvariant);
        static readonly Regex LooseId = new Regex("^[A-Za-z0-9]{6,32}$", RegexOptions.CultureInvariant);

        static readonly string[] IdQueryKeys = { "m", "model", "modelid", "model_id", "space", "spaceid", "sid" };
        static readonly string[] PathKeywords = { "space", "spaces", "models", "model", "show", "tour", "tours", "embed", "m", "s" };

        public static bool IsModelId(string s) => s != null && StrictId.IsMatch(s);

        public static string ShowcaseUrl(string modelId) => "https://my.matterport.com/show/?m=" + modelId;

        /// <summary>Returns the model ID or throws <see cref="ArgumentException"/> with a user-facing message.</summary>
        public static string ParseModelId(string input)
        {
            if (TryParseModelId(input, out var id)) return id;
            throw new ArgumentException(
                "Could not find a Matterport model ID in \"" + (input ?? "") + "\". Paste a link such as " +
                "https://discover.matterport.com/space/rGMMnZDh3Tf or https://my.matterport.com/show/?m=rGMMnZDh3Tf, " +
                "or the 11-character model ID itself.");
        }

        public static bool TryParseModelId(string input, out string modelId)
        {
            modelId = null;
            if (string.IsNullOrWhiteSpace(input)) return false;

            var s = input.Trim().Trim('"', '\'', '<', '>', '(', ')', '[', ']');
            if (StrictId.IsMatch(s)) { modelId = s; return true; }

            // Accept scheme-less links ("my.matterport.com/show/?m=...").
            if (!s.Contains("://") && s.Contains(".")) s = "https://" + s;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return false;

            // 1) Query string / fragment parameters (?m=ID, #m=ID).
            foreach (var part in new[] { uri.Query, uri.Fragment })
            {
                foreach (var kv in ParseQuery(part))
                {
                    if (Array.IndexOf(IdQueryKeys, kv.Key.ToLowerInvariant()) >= 0 && LooseId.IsMatch(kv.Value))
                    {
                        modelId = kv.Value;
                        return true;
                    }
                }
            }

            // 2) Path segments following a keyword (/space/ID, /models/ID, /show/ID).
            var segments = new List<string>();
            foreach (var seg in uri.AbsolutePath.Split('/'))
                if (seg.Length > 0) segments.Add(Uri.UnescapeDataString(seg));

            for (int i = 0; i < segments.Count - 1; i++)
            {
                if (Array.IndexOf(PathKeywords, segments[i].ToLowerInvariant()) >= 0 && LooseId.IsMatch(segments[i + 1]))
                {
                    modelId = segments[i + 1];
                    return true;
                }
            }

            // 3) Any 11-character segment, preferring the last one.
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                if (StrictId.IsMatch(segments[i]))
                {
                    modelId = segments[i];
                    return true;
                }
            }
            return false;
        }

        static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query)
        {
            if (string.IsNullOrEmpty(query)) yield break;
            var q = query.TrimStart('?', '#');
            foreach (var pair in q.Split('&', ';'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string key = Uri.UnescapeDataString(pair.Substring(0, eq).Replace('+', ' '));
                string value = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                yield return new KeyValuePair<string, string>(key, value.Trim());
            }
        }
    }
}
