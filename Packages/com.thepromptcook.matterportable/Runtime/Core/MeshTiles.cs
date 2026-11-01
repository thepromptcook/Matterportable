using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Matterportable.Core
{
    /// <summary>One textured piece of a Matterport mesh tile, in the Core frame.</summary>
    public sealed class TilePrimitive
    {
        public Vec3[] Positions;
        public Vec2[] Uvs; // bottom-up, like the rest of the Core
        public int[] Indices;
        /// <summary>The texture's file name, next to the tile (Matterport serves a JPEG for every KTX2).</summary>
        public string Texture;
    }

    /// <summary>
    /// Matterport's 3D-tiles mesh (the finest level of its tileset is ~2.4× the triangles of the 50k DAM, with 4096 px
    /// textures): reads tile GLBs (glTF 2.0, KHR_draco_mesh_compression). glTF is Y-up right-handed; the Core is the
    /// same with Z mirrored, and its texture rows are bottom-up, so v becomes 1 - v.
    /// </summary>
    public static class MeshTiles
    {
        /// <summary>The tileset's finest content: the leaf tiles' paths (with REPLACE refinement they cover the model).</summary>
        public static List<string> Leaves(JsonValue tileset)
        {
            var leaves = new List<string>();
            void Visit(JsonValue node)
            {
                var children = node["children"].Items;
                var uri = node["content"]["uri"].AsString() ?? node["content"]["url"].AsString();
                if (children.Count == 0 && !string.IsNullOrEmpty(uri)) leaves.Add(uri);
                foreach (var c in children) Visit(c);
            }
            Visit(tileset["root"]);
            return leaves;
        }

        /// <summary>The JPEG file names of a tile's textures (next to the tile).</summary>
        public static List<string> Textures(byte[] glb)
        {
            var names = new List<string>();
            foreach (var image in Json(glb, out _)["images"].Items) names.Add(Path.ChangeExtension(image["uri"].AsString(), ".jpg"));
            return names;
        }

        /// <summary>
        /// The tiles as one geometry with a part per texture (Material = the texture's path in the space folder).
        /// Positions are in the Core frame like the 50k mesh's (they line up within ~1 cm).
        /// </summary>
        public static SpaceGeometry LoadGeometry(string spaceDir, IReadOnlyList<string> tiles)
        {
            var positions = new List<Vec3>();
            var uvs = new List<Vec2>();
            var parts = new Dictionary<string, List<int>>();
            foreach (var rel in tiles)
            {
                string folder = rel.Substring(0, rel.LastIndexOf('/') + 1);
                foreach (var p in ReadGlb(File.ReadAllBytes(CacheLayout.Resolve(spaceDir, rel))))
                {
                    int first = positions.Count;
                    positions.AddRange(p.Positions);
                    uvs.AddRange(p.Uvs);
                    if (!parts.TryGetValue(folder + p.Texture, out var list)) parts[folder + p.Texture] = list = new List<int>();
                    foreach (int i in p.Indices) list.Add(first + i);
                }
            }
            var g = new SpaceGeometry { Positions = positions.ToArray(), Uvs = uvs.ToArray() };
            var all = new List<int>();
            foreach (var kv in parts)
            {
                g.Parts.Add(new GeometryPart { Material = kv.Key, Indices = kv.Value.ToArray(), FirstTriangle = all.Count / 3 });
                all.AddRange(kv.Value);
            }
            g.BoundsMin = g.BoundsMax = g.Positions.Length > 0 ? g.Positions[0] : Vec3.Zero;
            foreach (var p in g.Positions) { g.BoundsMin = Vec3.Min(g.BoundsMin, p); g.BoundsMax = Vec3.Max(g.BoundsMax, p); }
            g.Bvh = new Bvh(g.Positions, all.ToArray());
            return g;
        }

        static JsonValue Json(byte[] glb, out int bin)
        {
            if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != 0x46546C67) throw new FormatException("Not a GLB file.");
            int jsonLength = BitConverter.ToInt32(glb, 12);
            bin = 20 + jsonLength + 8;
            return JsonValue.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength));
        }

        public static List<TilePrimitive> ReadGlb(byte[] glb)
        {
            var json = Json(glb, out int bin);
            var result = new List<TilePrimitive>();
            foreach (var mesh in json["meshes"].Items)
                foreach (var prim in mesh["primitives"].Items)
                {
                    var draco = prim["extensions"]["KHR_draco_mesh_compression"];
                    if (!draco.IsObject) throw new NotSupportedException("Uncompressed tile primitives");
                    var view = json["bufferViews"].Items[(int)draco["bufferView"].AsDouble()];
                    var decoded = Draco.Decode(glb, bin + (int)view["byteOffset"].AsDouble(0), (int)view["byteLength"].AsDouble());
                    var pos = decoded.Attributes[(int)draco["attributes"]["POSITION"].AsDouble()].values;
                    var uv = decoded.Attributes[(int)draco["attributes"]["TEXCOORD_0"].AsDouble()].values;
                    var p = new TilePrimitive { Positions = new Vec3[decoded.PointCount], Uvs = new Vec2[decoded.PointCount], Indices = decoded.Indices };
                    for (int i = 0; i < decoded.PointCount; i++)
                    {
                        p.Positions[i] = new Vec3(pos[3 * i], pos[3 * i + 1], -pos[3 * i + 2]);
                        p.Uvs[i] = new Vec2(uv[2 * i], 1f - uv[2 * i + 1]);
                    }
                    // Mirroring Z flips the winding back to the Core's convention.
                    for (int t = 0; t < p.Indices.Length; t += 3) (p.Indices[t + 1], p.Indices[t + 2]) = (p.Indices[t + 2], p.Indices[t + 1]);
                    var material = json["materials"].Items[(int)prim["material"].AsDouble()];
                    var texture = json["textures"].Items[(int)material["pbrMetallicRoughness"]["baseColorTexture"]["index"].AsDouble()];
                    var source = texture["extensions"]["KHR_texture_basisu"]["source"];
                    var image = json["images"].Items[(int)(source.IsNull ? texture["source"] : source).AsDouble()];
                    p.Texture = Path.ChangeExtension(image["uri"].AsString(), ".jpg");
                    result.Add(p);
                }
            return result;
        }
    }
}
