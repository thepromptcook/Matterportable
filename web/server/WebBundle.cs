using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Matterportable.Core;

namespace Matterportable.Web
{
    /// <summary>
    /// What the browser needs for a space, computed natively once and stored in MatterportCache/&lt;id&gt;/web/ (Unity and
    /// Godot ignore it): space.json (sweeps, face layout, parts, textures), geometry.bin (Core-frame positions, UVs,
    /// indices), bvh.bin and features.bin (the BVH and snapping features, which take seconds to build in WebAssembly).
    /// Rebuilt when the mesh, calibration, props or clean photos (recon/clean, furniture painted out) change.
    /// </summary>
    public static class WebBundle
    {
        public const string Folder = "web";
        public static readonly string[] Files = { "space.json", "geometry.bin", "bvh.bin", "features.bin" };
        const int Version = 3;

        public static void Prepare(string dir, ILogSink log)
        {
            var manifest = SpaceManifest.Load(dir);
            var cal = SpaceCalibration.LoadOrDefault(dir);
            var meshPath = manifest.Mesh != null ? CacheLayout.Resolve(dir, manifest.Mesh.File) : null;
            var objects = Path.Combine(dir, PropFinder.FileName);
            var clean = Path.Combine(dir, CleanPanoramas.Folder, "clean.json");
            long Ticks(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
            string Key() => $"v{Version}|{(meshPath != null ? File.GetLastWriteTimeUtc(meshPath).Ticks : 0)}|{manifest.Sweeps.Count}|{cal.ToJson().ToJson(false)}|" +
                            $"{Ticks(objects)}|{Ticks(clean)}";
            var web = Path.Combine(dir, Folder);
            var keyFile = Path.Combine(web, "key.txt");
            if (File.Exists(keyFile) && File.ReadAllText(keyFile) == Key()) return;

            MeshData mesh = meshPath != null ? DamDecoder.Decode(File.ReadAllBytes(meshPath), log) : null;
            // Never solved: run the frame and winding checks here. The server has no JPEG decoder for the photo solver,
            // so the face layout stays Matterport's known one (confirmed on every real space tested). Not saved, so
            // Unity and Godot still run the full solver when they open the space.
            if (cal.NeedsSolve && mesh != null) cal = SpaceCalibrator.CheckGeometry(manifest, mesh, new SpaceCalibration(), log);
            var geo = mesh != null ? SpaceGeometry.Build(mesh, cal) : null;
            var sweeps = SweepBuilder.Build(manifest, cal, geo, log);
            CleanPanoramas.Apply(dir, sweeps);
            // The props (objects.json, shared with Unity and Godot) - found but not saved while the calibration is a guess.
            var floors = new SpaceModel(manifest, cal, geo, sweeps).FloorHeights;
            var props = geo == null ? new List<Prop>() : cal.NeedsSolve ? PropFinder.Find(geo, floors) : PropFinder.LoadOrFind(dir, geo, floors, log);

            Directory.CreateDirectory(web);
            if (geo != null)
            {
                WriteGeometry(Path.Combine(web, "geometry.bin"), geo);
                File.WriteAllBytes(Path.Combine(web, "bvh.bin"), geo.Bvh.SerializeHierarchy());
                File.WriteAllBytes(Path.Combine(web, "features.bin"), MeshFeatures.Build(geo).Serialize());
            }
            File.WriteAllText(Path.Combine(web, "space.json"), JsonSerializer.Serialize(Describe(manifest, cal, geo, sweeps, props)));
            File.WriteAllText(keyFile, Key()); // after objects.json was written
        }

        /// <summary>"MPW1", vertex count, index count, has-UVs flag, then positions, UVs and indices (little-endian).</summary>
        static void WriteGeometry(string path, SpaceGeometry geo)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(new[] { (byte)'M', (byte)'P', (byte)'W', (byte)'1' });
                w.Write(geo.Positions.Length);
                w.Write(geo.Parts.Sum(p => p.Indices.Length));
                w.Write(geo.HasUvs ? 1 : 0);
                foreach (var p in geo.Positions) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
                if (geo.HasUvs) foreach (var uv in geo.Uvs) { w.Write(uv.X); w.Write(uv.Y); }
                foreach (var part in geo.Parts) foreach (var i in part.Indices) w.Write(i);
            }
        }

        static object Describe(SpaceManifest manifest, SpaceCalibration cal, SpaceGeometry geo, List<SweepInfo> sweeps, List<Prop> props)
        {
            if (!CubeFaces.TryBuildLookup(cal.Faces, out var slice, out var right, out var up))
                CubeFaces.TryBuildLookup(CubeFaces.DefaultMatterportLayout(), out slice, out right, out up);
            float[] V(Vec3 v) => new[] { v.X, v.Y, v.Z };
            var textures = manifest.Textures.GroupBy(t => t.Material ?? "").ToDictionary(g => g.Key, g => g.First().File);
            int start = 0;
            var parts = geo?.Parts.Select(p =>
            {
                var part = new { material = p.Material, start, count = p.Indices.Length, texture = textures.TryGetValue(p.Material ?? "", out var f) ? f : null };
                start += p.Indices.Length;
                return part;
            }).ToList();
            return new
            {
                id = manifest.ModelId,
                name = manifest.Name,
                calibration = new { method = cal.Method, score = double.IsNaN(cal.Score) ? (double?)null : cal.Score, rotation = cal.Rotation.ToString() },
                faceRight = Enumerable.Range(0, 6).Select(c => new[] { right[c].X, right[c].Y, right[c].Z, slice[c] }),
                faceUp = Enumerable.Range(0, 6).Select(c => V(up[c])),
                bounds = geo != null ? new[] { V(geo.BoundsMin), V(geo.BoundsMax) } : null,
                triangles = geo?.TriangleCount ?? 0,
                parts,
                objects = geo != null ? PropFinder.ToJson(props, geo).ToJson(false) : null,
                floors = manifest.Floors.Select(f => f.Label),
                sweeps = sweeps.Select(s =>
                {
                    var r = Frames.ShaderRotation(s.WorldFromPano);
                    return new
                    {
                        id = s.Id,
                        label = s.Label,
                        position = V(s.Position),
                        floorPoint = V(s.FloorPoint),
                        floor = s.Floor,
                        neighbors = s.Neighbors,
                        rotation = V(r.Column(0)).Concat(V(r.Column(1))).Concat(V(r.Column(2))),
                        tiers = s.Entry.Tiers.Select(t => new { size = t.Size, tiles = Math.Max(1, t.Tiles), dir = t.Directory, ext = t.Extension }),
                        clean = s.CleanFaces, // furniture painted out (null without), and which prop each pixel had painted out
                        painted = s.PaintedProps,
                    };
                }),
                warnings = manifest.Warnings,
            };
        }
    }
}
