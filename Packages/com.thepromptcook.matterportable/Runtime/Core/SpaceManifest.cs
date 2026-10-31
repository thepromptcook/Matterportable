using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Matterportable.Core
{
    public sealed class MeshEntry
    {
        public string File;
        public string Format = "dam";
        public string Resolution;
    }

    public sealed class TextureEntry
    {
        /// <summary>Material name referenced by the mesh.</summary>
        public string Material;
        /// <summary>Path relative to the space folder.</summary>
        public string File;
    }

    public sealed class FloorEntry
    {
        public string Id;
        public int Index;
        public string Label;
    }

    /// <summary>One resolution level of a sweep's six panorama face images.</summary>
    public sealed class PanoTier
    {
        public int Size;
        /// <summary>Folder (relative to the space folder) containing face0..face5.</summary>
        public string Directory;
        public string Extension = "jpg";
        /// <summary>Tiles per face side; 1 means one image per face.</summary>
        public int Tiles = 1;

        public string FacePath(int face) => Directory + "/face" + face + "." + Extension;
        public string TilePath(int face, int x, int y) => Directory + "/face" + face + "_" + x + "_" + y + "." + Extension;
    }

    public sealed class SweepEntry
    {
        public string Id;
        public int Index;
        public string Label = "";
        public string FloorId;
        public string RoomId;
        /// <summary>Camera position in the manifest's coordinate frame (metres).</summary>
        public double[] Position;
        /// <summary>Quaternion (x, y, z, w) in the manifest's frame, or null.</summary>
        public double[] Rotation;
        public List<string> Neighbors = new List<string>();
        /// <summary>Sorted by ascending size.</summary>
        public List<PanoTier> Tiers = new List<PanoTier>();

        public PanoTier SmallestTier => Tiers.Count > 0 ? Tiers[0] : null;
        public PanoTier LargestTier => Tiers.Count > 0 ? Tiers[Tiers.Count - 1] : null;

        /// <summary>Largest tier not exceeding maxSize (or the smallest tier if all are larger).</summary>
        public PanoTier TierAtMost(int maxSize)
        {
            PanoTier best = null;
            foreach (var t in Tiers) if (t.Size <= maxSize) best = t;
            return best ?? SmallestTier;
        }
    }

    /// <summary>
    /// Normalised, source-independent description of a downloaded space (manifest.json in the cache folder).
    /// Everything the Unity runtime needs is referenced from here with relative paths.
    /// </summary>
    public sealed class SpaceManifest
    {
        public const string FileName = "manifest.json";
        public const int CurrentFormatVersion = 1;

        public int FormatVersion = CurrentFormatVersion;
        public string ModelId;
        public string Name;
        public string Source = "matterport";
        public string SourceUrl;
        public string CreatedUtc;
        public string Generator = "Matterportable";
        public CoordinateFrame Frame = CoordinateFrame.ZUpRightHanded;
        public MeshEntry Mesh;
        public List<TextureEntry> Textures = new List<TextureEntry>();
        public List<FloorEntry> Floors = new List<FloorEntry>();
        public List<SweepEntry> Sweeps = new List<SweepEntry>();
        public List<string> Warnings = new List<string>();
        public List<string> TilesetUrls = new List<string>();

        public int FindSweep(string id)
        {
            for (int i = 0; i < Sweeps.Count; i++) if (Sweeps[i].Id == id) return i;
            return -1;
        }

        public int FindFloor(string id)
        {
            for (int i = 0; i < Floors.Count; i++) if (Floors[i].Id == id) return i;
            return -1;
        }

        public string TextureFileFor(string material)
        {
            foreach (var t in Textures) if (t.Material == material) return t.File;
            return null;
        }

        public JsonValue ToJson()
        {
            var j = JsonValue.NewObject()
                .Set("formatVersion", FormatVersion)
                .Set("modelId", ModelId)
                .Set("name", Name)
                .Set("source", Source)
                .Set("sourceUrl", SourceUrl)
                .Set("createdUtc", CreatedUtc)
                .Set("generator", Generator)
                .Set("frame", Frames.ToToken(Frame));
            if (Mesh != null)
                j.Set("mesh", JsonValue.NewObject().Set("file", Mesh.File).Set("format", Mesh.Format).Set("resolution", Mesh.Resolution));

            var tex = JsonValue.NewArray();
            foreach (var t in Textures) tex.Add(JsonValue.NewObject().Set("material", t.Material).Set("file", t.File));
            j.Set("textures", tex);

            var floors = JsonValue.NewArray();
            foreach (var f in Floors) floors.Add(JsonValue.NewObject().Set("id", f.Id).Set("index", f.Index).Set("label", f.Label));
            j.Set("floors", floors);

            var sweeps = JsonValue.NewArray();
            foreach (var s in Sweeps)
            {
                var tiers = JsonValue.NewArray();
                foreach (var t in s.Tiers)
                {
                    var tj = JsonValue.NewObject().Set("size", t.Size).Set("dir", t.Directory).Set("ext", t.Extension);
                    if (t.Tiles > 1) tj.Set("tiles", t.Tiles);
                    tiers.Add(tj);
                }
                var sj = JsonValue.NewObject()
                    .Set("id", s.Id)
                    .Set("index", s.Index)
                    .Set("label", s.Label)
                    .Set("floor", s.FloorId)
                    .Set("room", s.RoomId)
                    .Set("position", JsonValue.FromArray(s.Position))
                    .Set("rotation", s.Rotation == null ? JsonValue.Null : JsonValue.FromArray(s.Rotation))
                    .Set("neighbors", JsonValue.FromArray(s.Neighbors))
                    .Set("tiers", tiers);
                sweeps.Add(sj);
            }
            j.Set("sweeps", sweeps);
            j.Set("warnings", JsonValue.FromArray(Warnings));
            if (TilesetUrls.Count > 0) j.Set("tilesets", JsonValue.FromArray(TilesetUrls));
            return j;
        }

        public static SpaceManifest FromJson(JsonValue j)
        {
            int version = j["formatVersion"].AsInt(0);
            if (version > CurrentFormatVersion)
                throw new MatterportException($"manifest.json format {version} is newer than this version of Matterportable supports ({CurrentFormatVersion}).");

            var m = new SpaceManifest
            {
                FormatVersion = version,
                ModelId = j["modelId"].AsString(),
                Name = j["name"].AsString(),
                Source = j["source"].AsString("matterport"),
                SourceUrl = j["sourceUrl"].AsString(),
                CreatedUtc = j["createdUtc"].AsString(),
                Generator = j["generator"].AsString(),
                Frame = Frames.FromToken(j["frame"].AsString()),
                Warnings = j["warnings"].AsStringList(),
                TilesetUrls = j["tilesets"].AsStringList(),
            };
            var mesh = j["mesh"];
            if (mesh.IsObject && !string.IsNullOrEmpty(mesh["file"].AsString()))
                m.Mesh = new MeshEntry { File = mesh["file"].AsString(), Format = mesh["format"].AsString("dam"), Resolution = mesh["resolution"].AsString() };

            foreach (var t in j["textures"].Items)
                m.Textures.Add(new TextureEntry { Material = t["material"].AsString(), File = t["file"].AsString() });

            foreach (var f in j["floors"].Items)
                m.Floors.Add(new FloorEntry { Id = f["id"].AsString(), Index = f["index"].AsInt(), Label = f["label"].AsString("") });

            foreach (var s in j["sweeps"].Items)
            {
                var sweep = new SweepEntry
                {
                    Id = s["id"].AsString(),
                    Index = s["index"].AsInt(),
                    Label = s["label"].AsString(""),
                    FloorId = s["floor"].AsString(),
                    RoomId = s["room"].AsString(),
                    Position = s["position"].AsDoubleArray(),
                    Rotation = s["rotation"].AsDoubleArray(),
                    Neighbors = s["neighbors"].AsStringList(),
                };
                foreach (var t in s["tiers"].Items)
                {
                    sweep.Tiers.Add(new PanoTier
                    {
                        Size = t["size"].AsInt(),
                        Directory = t["dir"].AsString(),
                        Extension = t["ext"].AsString("jpg"),
                        Tiles = Math.Max(1, t["tiles"].AsInt(1)),
                    });
                }
                sweep.Tiers.Sort((a, b) => a.Size.CompareTo(b.Size));
                if (sweep.Position != null && sweep.Position.Length >= 3) m.Sweeps.Add(sweep);
            }
            return m;
        }

        public void Save(string spaceDir) =>
            IoUtil.WriteAllTextAtomic(Path.Combine(spaceDir, FileName), ToJson().ToJson(true));

        public static SpaceManifest Load(string spaceDir)
        {
            var path = Path.Combine(spaceDir, FileName);
            if (!File.Exists(path)) throw new FileNotFoundException("No manifest.json found. Download the space first.", path);
            return FromJson(JsonValue.Parse(File.ReadAllText(path, Encoding.UTF8)));
        }

        public static bool Exists(string spaceDir) => !string.IsNullOrEmpty(spaceDir) && File.Exists(Path.Combine(spaceDir, FileName));
    }

    /// <summary>On-disk layout of the cache. Every space lives in its own folder named after the model ID.</summary>
    public static class CacheLayout
    {
        public const string DefaultFolderName = "MatterportCache";
        public const string MeshDir = "mesh";
        public const string TexturesDir = "textures";
        public const string PanosDir = "panos";
        public const string DebugDir = "debug";

        public static string SpaceDir(string cacheRoot, string modelId) => Path.Combine(cacheRoot, SafeName(modelId));

        public static string PanoTierDir(string sweepId, int size) =>
            PanosDir + "/" + SafeName(sweepId) + "/" + size.ToString(CultureInfo.InvariantCulture);

        public static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            return sb.ToString();
        }

        public static string Resolve(string spaceDir, string relative) =>
            Path.Combine(spaceDir, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    public static class IoUtil
    {
        public static void WriteAllTextAtomic(string path, string text) => WriteAllBytesAtomic(path, new UTF8Encoding(false).GetBytes(text));

        public static void WriteAllBytesAtomic(string path, byte[] bytes)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static bool NonEmptyFileExists(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length > 0; }
            catch (IOException) { return false; }
        }

        public static long DirectorySize(string dir)
        {
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch (IOException) { }
            }
            return total;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1L << 10) return (bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KB";
            return bytes + " B";
        }
    }
}
