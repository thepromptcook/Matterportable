using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Matterportable.Core;

namespace Matterportable.Tests
{
    /// <summary>
    /// Offline stand-in for Matterport: a GraphQL endpoint with its own schema (validated per query line, with
    /// graphql-java style errors) plus a CDN with signed, expiring links. Built from a <see cref="SyntheticSpace"/>.
    /// </summary>
    public sealed class FakeMatterport : IHttpTransport
    {
        public readonly HashSet<string> Schema = new HashSet<string>();
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
        public string Token = "t1";
        /// <summary>After this many successful GETs the token rotates (old links get HTTP 403). -1 = never.</summary>
        public int RotateTokenAfterGets = -1;
        public int Posts, Gets, Rejected;
        public readonly List<string> Queries = new List<string>();
        readonly SyntheticSpace _space;
        readonly object _lock = new object();

        public const string Cdn = "https://cdn.test/models/";

        public FakeMatterport(SyntheticSpace space)
        {
            _space = space;
            foreach (var p in new[]
            {
                "model", "model/id", "model/name",
                "model/assets", "model/assets/meshes", "model/assets/meshes/id", "model/assets/meshes/status",
                "model/assets/meshes/filename", "model/assets/meshes/format", "model/assets/meshes/resolution", "model/assets/meshes/url",
                "model/assets/textures", "model/assets/textures/id", "model/assets/textures/format", "model/assets/textures/resolution",
                "model/assets/textures/quality", "model/assets/textures/urlTemplate",
                "model/floors", "model/floors/id", "model/floors/label", "model/floors/sequence",
                "model/locations", "model/locations/id", "model/locations/index", "model/locations/label", "model/locations/neighbors",
                "model/locations/floor", "model/locations/floor/id", "model/locations/room", "model/locations/room/id",
                "model/locations/pano", "model/locations/pano/id", "model/locations/pano/placement",
                "model/locations/pano/position", "model/locations/pano/position/x", "model/locations/pano/position/y", "model/locations/pano/position/z",
                "model/locations/pano/rotation", "model/locations/pano/rotation/x", "model/locations/pano/rotation/y",
                "model/locations/pano/rotation/z", "model/locations/pano/rotation/w",
                "model/locations/pano/skyboxes", "model/locations/pano/skyboxes/resolution", "model/locations/pano/skyboxes/status",
                "model/locations/pano/skyboxes/urlTemplate",
            }) Schema.Add(p);

            var id = space.Manifest.ModelId;
            Files[Cdn + id + "/assets/" + id + "_50k.dam"] = DamWriter.Write(space.SourceMesh);
            foreach (var t in space.Manifest.Textures)
                Files[Cdn + id + "/assets/" + id + "_50k_texture_png_high/" + t.Material + ".png"] = Png.Encode(space.Textures[t.Material]);
            for (int i = 0; i < space.Manifest.Sweeps.Count; i++)
            {
                var s = space.Manifest.Sweeps[i];
                for (int k = 0; k < s.Tiers.Count; k++)
                for (int f = 0; f < 6; f++)
                    Files[Cdn + id + "/assets/pan/" + s.Tiers[k].Size + "/" + s.Id + "_skybox" + f + ".png"] = Png.Encode(space.Panoramas[i][k][f]);
            }
        }

        JsonValue BuildData()
        {
            var m = _space.Manifest;
            var id = m.ModelId;
            string Signed(string path) => Cdn + id + "/assets/" + path + "?t=" + Token;

            var model = JsonValue.NewObject().Set("id", id).Set("name", m.Name);
            var meshes = JsonValue.NewArray().Add(JsonValue.NewObject()
                .Set("id", "mesh1").Set("status", "available").Set("filename", id + "_50k.dam").Set("format", "dam")
                .Set("resolution", "50k").Set("url", Signed(id + "_50k.dam")));
            var textures = JsonValue.NewArray()
                .Add(JsonValue.NewObject().Set("id", "tex-low").Set("format", "png").Set("resolution", "50k").Set("quality", "low")
                    .Set("urlTemplate", Signed(id + "_50k_texture_png_low/" + id + "_50k_<texture>.png")))
                .Add(JsonValue.NewObject().Set("id", "tex-high").Set("format", "png").Set("resolution", "50k").Set("quality", "high")
                    .Set("urlTemplate", Signed(id + "_50k_texture_png_high/" + id + "_50k_<texture>.png")));
            model.Set("assets", JsonValue.NewObject().Set("meshes", meshes).Set("textures", textures));
            var floors = JsonValue.NewArray();
            foreach (var f in m.Floors) floors.Add(JsonValue.NewObject().Set("id", f.Id).Set("label", f.Label).Set("sequence", f.Index));
            model.Set("floors", floors);

            var locations = JsonValue.NewArray();
            foreach (var s in m.Sweeps)
            {
                var skyboxes = JsonValue.NewArray();
                foreach (var t in s.Tiers)
                    skyboxes.Add(JsonValue.NewObject().Set("resolution", t.Size.ToString(CultureInfo.InvariantCulture)).Set("status", "available")
                        .Set("urlTemplate", Signed("pan/" + t.Size + "/" + s.Id + "_skybox<face>.png")));
                var pano = JsonValue.NewObject()
                    .Set("id", "pano-" + s.Id)
                    .Set("placement", "auto")
                    .Set("position", Point(s.Position))
                    .Set("rotation", JsonValue.NewObject().Set("x", s.Rotation[0]).Set("y", s.Rotation[1]).Set("z", s.Rotation[2]).Set("w", s.Rotation[3]))
                    .Set("skyboxes", skyboxes);
                locations.Add(JsonValue.NewObject()
                    .Set("id", s.Id).Set("index", s.Index).Set("label", s.Label)
                    .Set("neighbors", JsonValue.FromArray(s.Neighbors))
                    .Set("floor", JsonValue.NewObject().Set("id", s.FloorId))
                    .Set("room", JsonValue.NewObject().Set("id", s.RoomId))
                    .Set("pano", pano));
            }
            // An unplaced 360° view, which must be skipped.
            locations.Add(JsonValue.NewObject().Set("id", "unplaced1").Set("index", 99)
                .Set("pano", JsonValue.NewObject().Set("placement", "unplaced").Set("skyboxes", JsonValue.NewArray())));
            model.Set("locations", locations);
            return JsonValue.NewObject().Set("model", model);
        }

        static JsonValue Point(double[] p) => JsonValue.NewObject().Set("x", p[0]).Set("y", p[1]).Set("z", p[2]);

        public Task<HttpResult> PostJsonAsync(string url, string json, CancellationToken ct)
        {
            lock (_lock) Posts++;
            var query = JsonValue.Parse(json)["query"].AsString();
            lock (_lock) Queries.Add(query);
            var errors = JsonValue.NewArray();
            var stack = new List<string>();
            var lines = query.Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t.Length == 0) continue;
                if (t == "}") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
                bool opens = t.EndsWith("{");
                var name = t.TrimEnd('{').Trim();
                int paren = name.IndexOf('(');
                if (paren >= 0) name = name.Substring(0, paren);
                var path = stack.Count == 0 ? name : string.Join("/", stack) + "/" + name;
                if (!Schema.Contains(path))
                {
                    var parent = stack.Count == 0 ? "Query" : stack[stack.Count - 1];
                    errors.Add(JsonValue.NewObject()
                        .Set("message", $"Validation error (FieldUndefined@[{path}]) : Field '{name}' in type '{parent}' is undefined")
                        .Set("locations", JsonValue.NewArray().Add(JsonValue.NewObject().Set("line", i + 1).Set("column", lines[i].Length - lines[i].TrimStart().Length + 1)))
                        .Set("extensions", JsonValue.NewObject().Set("classification", "ValidationError")));
                }
                if (opens) stack.Add(name);
            }
            JsonValue resp;
            if (errors.Count > 0)
            {
                lock (_lock) Rejected++;
                resp = JsonValue.NewObject().Set("errors", errors).Set("data", JsonValue.Null);
            }
            else resp = JsonValue.NewObject().Set("data", BuildData());
            return Task.FromResult(new HttpResult { Status = 200, Body = Encoding.UTF8.GetBytes(resp.ToJson(true)) });
        }

        public Task<HttpResult> GetToFileAsync(string url, string path, Action<long> onBytes, CancellationToken ct)
        {
            int q = url.IndexOf('?');
            var key = q >= 0 ? url.Substring(0, q) : url;
            var token = q >= 0 ? url.Substring(q + 3) : "";
            lock (_lock)
            {
                if (token != Token) return Task.FromResult(new HttpResult { Status = 403, Body = Encoding.UTF8.GetBytes("expired") });
                if (!Files.TryGetValue(key, out var bytes)) return Task.FromResult(new HttpResult { Status = 404 });
                Gets++;
                if (RotateTokenAfterGets >= 0 && Gets >= RotateTokenAfterGets) { Token = "t" + (Gets + 1); RotateTokenAfterGets = -1; }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
                onBytes?.Invoke(bytes.Length);
                return Task.FromResult(new HttpResult { Status = 200, Bytes = bytes.Length });
            }
        }
    }
}
