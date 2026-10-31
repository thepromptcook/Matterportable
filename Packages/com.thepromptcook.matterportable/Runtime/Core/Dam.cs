using System;
using System.Collections.Generic;
using System.Text;

namespace Matterportable.Core
{
    public sealed class MeshPart
    {
        public string Name;
        public string Material;
        /// <summary>Triangle list into <see cref="MeshData.Positions"/> (global vertex indices).</summary>
        public int[] Indices;
    }

    /// <summary>Raw decoded mesh in its source coordinate frame.</summary>
    public sealed class MeshData
    {
        public float[] Positions = Array.Empty<float>();
        /// <summary>Two floats per vertex, or empty if the mesh has no UVs.</summary>
        public float[] Uvs = Array.Empty<float>();
        public List<MeshPart> Parts = new List<MeshPart>();

        public int VertexCount => Positions.Length / 3;
        public bool HasUvs => Uvs.Length == VertexCount * 2 && VertexCount > 0;

        public int TriangleCount
        {
            get
            {
                int n = 0;
                foreach (var p in Parts) n += p.Indices.Length / 3;
                return n;
            }
        }

        public Vec3 Vertex(int i) => new Vec3(Positions[3 * i], Positions[3 * i + 1], Positions[3 * i + 2]);
    }

    /// <summary>
    /// Decoder for Matterport ".dam" meshes: a protobuf "binary_mesh" made of chunks
    /// (field 1: simple chunks with float xyz/uv + uint32 faces; field 2: quantized chunks).
    /// Quantized chunks are decoded with plausibility checks because their encoding details vary.
    /// </summary>
    public static class DamDecoder
    {
        public static MeshData Decode(byte[] data, ILogSink log = null)
        {
            if (data == null || data.Length == 0) throw new FormatException("The mesh file is empty.");
            var pos = new List<float>();
            var uv = new List<float>();
            var parts = new List<MeshPart>();
            bool anyUv = false;
            int chunks = 0;

            // Pass 1: read chunks; quantized ones stay raw until their coding is decided for the whole file.
            var pending = new List<object>();
            var r = new ProtoReader(data);
            while (r.TryReadTag(out int field, out int wire))
            {
                if (wire != ProtoReader.LengthDelimited) { r.Skip(wire); continue; }
                var chunk = r.ReadMessage();
                if (field == 1) pending.Add(ReadSimpleChunk(chunk));
                else if (field == 2)
                {
                    var raw = ReadQuantizedChunk(chunk);
                    if (raw != null) pending.Add(raw);
                }
            }
            bool delta = DecideDeltaCoding(pending, log);

            // Pass 2: concatenate.
            foreach (var item in pending)
            {
                var c = item as Chunk ?? Finish((RawQuantized)item, delta);
                chunks++;

                int baseVertex = pos.Count / 3;
                int n = c.Xyz.Length / 3;
                pos.AddRange(c.Xyz);
                if (c.Uv != null && c.Uv.Length == n * 2)
                {
                    if (!anyUv && baseVertex > 0) uv.AddRange(new float[baseVertex * 2]);
                    anyUv = true;
                    uv.AddRange(c.Uv);
                }
                else if (anyUv) uv.AddRange(new float[n * 2]);

                var idx = new int[c.Faces.Length];
                for (int i = 0; i < idx.Length; i++) idx[i] = c.Faces[i] + baseVertex;
                parts.Add(new MeshPart { Name = c.Name, Material = c.Material, Indices = idx });
            }

            if (chunks == 0 || pos.Count == 0)
                throw new FormatException("No mesh chunks found in the .dam file. Structure:\n" + ProtoInspector.Describe(data));

            var mesh = new MeshData { Positions = pos.ToArray(), Uvs = anyUv ? uv.ToArray() : Array.Empty<float>(), Parts = parts };
            log?.Info($"Decoded mesh: {chunks} chunks, {mesh.VertexCount:N0} vertices, {mesh.TriangleCount:N0} triangles, UVs: {mesh.HasUvs}.");
            return mesh;
        }

        /// <summary>Writes debug/dam-structure.txt (field layout of the file) - used when something can't be decoded.</summary>
        public static void WriteDiagnostics(string spaceDir, byte[] data)
        {
            try { IoUtil.WriteAllTextAtomic(System.IO.Path.Combine(spaceDir, CacheLayout.DebugDir, "dam-structure.txt"), ProtoInspector.Describe(data, 4, 400)); }
            catch (System.IO.IOException) { }
        }

        /// <summary>Distinct material names in file order (used to work out which textures to download).</summary>
        public static List<string> ReadMaterialNames(byte[] data)
        {
            var names = new List<string>();
            var r = new ProtoReader(data);
            while (r.TryReadTag(out int field, out int wire))
            {
                if (wire != ProtoReader.LengthDelimited || (field != 1 && field != 2)) { r.Skip(wire); continue; }
                var chunk = r.ReadMessage();
                int materialField = field == 1 ? 4 : 2;
                while (chunk.TryReadTag(out int f, out int w))
                {
                    if (f == materialField && w == ProtoReader.LengthDelimited)
                    {
                        var s = chunk.ReadString();
                        if (!string.IsNullOrEmpty(s) && !names.Contains(s)) names.Add(s);
                    }
                    else chunk.Skip(w);
                }
            }
            return names;
        }

        sealed class Chunk
        {
            public float[] Xyz;
            public float[] Uv;
            public int[] Faces;
            public string Name, Material;
        }

        static Chunk ReadSimpleChunk(ProtoReader r)
        {
            var xyz = new List<float>();
            var faces = new List<ulong>();
            // Every other float array we meet, in preference order, as texture-coordinate candidates
            // (the field that holds UVs differs between .dam versions).
            var candidates = new List<List<float>>();
            var vertexFloats = new SortedDictionary<int, List<float>>();
            string name = null, material = null;
            while (r.TryReadTag(out int f, out int w))
            {
                if (f == 1 && w == ProtoReader.LengthDelimited)
                {
                    var v = r.ReadMessage();
                    while (v.TryReadTag(out int vf, out int vw))
                    {
                        if (vf == 1) { v.ReadFloats(vw, xyz); continue; }
                        if (vw != ProtoReader.LengthDelimited && vw != ProtoReader.Fixed32) { v.Skip(vw); continue; }
                        if (!vertexFloats.TryGetValue(vf, out var list)) vertexFloats[vf] = list = new List<float>();
                        if (!TryReadFloats(v, vw, list)) vertexFloats.Remove(vf);
                    }
                }
                else if (f == 2 && w == ProtoReader.LengthDelimited)
                {
                    var fr = r.ReadMessage();
                    while (fr.TryReadTag(out int ff, out int fw))
                    {
                        if (ff == 1) fr.ReadVarints(fw, faces);
                        else fr.Skip(fw);
                    }
                }
                else if (f == 3 && w == ProtoReader.LengthDelimited) name = r.ReadString();
                else if (f == 4 && w == ProtoReader.LengthDelimited) material = r.ReadString();
                else if (w == ProtoReader.LengthDelimited)
                {
                    var list = new List<float>();
                    if (TryReadFloats(r, w, list)) candidates.Add(list);
                }
                else r.Skip(w);
            }
            if (vertexFloats.TryGetValue(4, out var uv4)) candidates.Insert(0, uv4);
            int insertAt = candidates.Count > 0 && vertexFloats.ContainsKey(4) ? 1 : 0;
            foreach (var kv in vertexFloats) if (kv.Key != 4) candidates.Insert(insertAt++, kv.Value);

            if (xyz.Count % 3 != 0) throw new FormatException($"Chunk '{name}' has {xyz.Count} position floats (not a multiple of 3).");
            int n = xyz.Count / 3;
            if (faces.Count % 3 != 0) throw new FormatException($"Chunk '{name}' has {faces.Count} face indices (not a multiple of 3).");
            var idx = new int[faces.Count];
            for (int i = 0; i < idx.Length; i++)
            {
                if (faces[i] >= (ulong)n) throw new FormatException($"Chunk '{name}' references vertex {faces[i]} but has only {n}.");
                idx[i] = (int)faces[i];
            }

            // Per-vertex UVs.
            foreach (var c in candidates)
                if (c.Count == n * 2 && LooksLikeUvs(c))
                    return new Chunk { Xyz = xyz.ToArray(), Uv = c.ToArray(), Faces = idx, Name = name, Material = material };

            // Per-corner UVs: un-index the chunk so every triangle corner gets its own vertex.
            foreach (var c in candidates)
            {
                if (c.Count != idx.Length * 2 || idx.Length == 0 || !LooksLikeUvs(c)) continue;
                var px = new float[idx.Length * 3];
                var corners = new int[idx.Length];
                for (int k = 0; k < idx.Length; k++)
                {
                    px[3 * k] = xyz[3 * idx[k]];
                    px[3 * k + 1] = xyz[3 * idx[k] + 1];
                    px[3 * k + 2] = xyz[3 * idx[k] + 2];
                    corners[k] = k;
                }
                return new Chunk { Xyz = px, Uv = c.ToArray(), Faces = corners, Name = name, Material = material };
            }

            return new Chunk { Xyz = xyz.ToArray(), Uv = null, Faces = idx, Name = name, Material = material };
        }

        static bool TryReadFloats(ProtoReader r, int wireType, List<float> into)
        {
            if (wireType == ProtoReader.LengthDelimited)
            {
                var probe = r.ReadMessage();
                if (probe.Remaining % 4 != 0) return false;
                while (!probe.AtEnd) into.Add(probe.ReadFloat());
                return true;
            }
            if (wireType == ProtoReader.Fixed32) { into.Add(r.ReadFloat()); return true; }
            r.Skip(wireType);
            return false;
        }

        /// <summary>Texture coordinates are finite and of moderate size (wrapping UVs allowed).</summary>
        static bool LooksLikeUvs(List<float> c)
        {
            if (c.Count == 0) return false;
            int inside = 0, step = Math.Max(1, c.Count / 2000);
            for (int i = 0; i < c.Count; i += step)
            {
                float v = c[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) return false;
                if (v > -64f && v < 64f) inside++;
            }
            return inside >= 0.9 * ((c.Count + step - 1) / step);
        }

        sealed class Quantized
        {
            public float Scale = 1f;
            public List<float> Offset = new List<float>();
            public List<long>[] Components = { new List<long>(), new List<long>(), new List<long>() };
        }

        static Quantized ReadQuantized(ProtoReader r, int components)
        {
            var q = new Quantized();
            while (r.TryReadTag(out int f, out int w))
            {
                if (f == 1 && w == ProtoReader.Fixed32) q.Scale = r.ReadFloat();
                else if (f == 2) r.ReadFloats(w, q.Offset);
                else if (f >= 3 && f < 3 + components)
                {
                    var raw = new List<ulong>();
                    r.ReadVarints(w, raw);
                    var list = q.Components[f - 3];
                    foreach (var v in raw) list.Add(ProtoReader.ZigZag(v));
                }
                else r.Skip(w);
            }
            return q;
        }

        static float[] Dequantize(Quantized q, int components, bool delta)
        {
            int n = q.Components[0].Count;
            for (int c = 1; c < components; c++)
                if (q.Components[c].Count != n) throw new FormatException("Quantized component arrays differ in length.");
            var result = new float[n * components];
            for (int c = 0; c < components; c++)
            {
                long acc = 0;
                float offset = c < q.Offset.Count ? q.Offset[c] : 0f;
                var list = q.Components[c];
                for (int i = 0; i < n; i++)
                {
                    acc = delta ? acc + list[i] : list[i];
                    result[i * components + c] = acc * q.Scale + offset;
                }
            }
            return result;
        }

        sealed class RawQuantized
        {
            public Quantized Verts, Uvs;
            public int[] Faces;
            public string Name, Material;
        }

        /// <summary>
        /// Decides absolute vs delta coding for all quantized chunks at once (weighted by triangle count),
        /// because tiny chunks alone are ambiguous.
        /// </summary>
        static bool DecideDeltaCoding(List<object> pending, ILogSink log)
        {
            double abs = 0, del = 0, weight = 0;
            foreach (var item in pending)
            {
                if (!(item is RawQuantized q) || q.Faces.Length < 3) continue;
                double w = q.Faces.Length / 3;
                abs += w * EdgeRatio(Dequantize(q.Verts, 3, false), q.Faces);
                del += w * EdgeRatio(Dequantize(q.Verts, 3, true), q.Faces);
                weight += w;
            }
            if (weight == 0) return false;
            bool delta = del < abs;
            log?.Info($"Quantized mesh chunks use {(delta ? "delta" : "absolute")} coding (edge ratio {Math.Min(abs, del) / weight:0.000} vs {Math.Max(abs, del) / weight:0.000}).");
            return delta;
        }

        static Chunk Finish(RawQuantized q, bool delta)
        {
            int n = q.Verts.Components[0].Count;
            // UVs are assumed to use the same coding as the positions.
            float[] uv = q.Uvs != null && q.Uvs.Components[0].Count == n ? Dequantize(q.Uvs, 2, delta) : null;
            return new Chunk { Xyz = Dequantize(q.Verts, 3, delta), Uv = uv, Faces = q.Faces, Name = q.Name, Material = q.Material };
        }

        static RawQuantized ReadQuantizedChunk(ProtoReader r)
        {
            Quantized verts = null, uvs = null;
            var faces = new List<ulong>();
            string name = null, material = null;
            while (r.TryReadTag(out int f, out int w))
            {
                if (f == 1 && w == ProtoReader.LengthDelimited) name = r.ReadString();
                else if (f == 2 && w == ProtoReader.LengthDelimited) material = r.ReadString();
                else if (f == 3 && w == ProtoReader.LengthDelimited) verts = ReadQuantized(r.ReadMessage(), 3);
                else if (f == 4 && w == ProtoReader.LengthDelimited) uvs = ReadQuantized(r.ReadMessage(), 2);
                else if (f == 5 && w == ProtoReader.LengthDelimited)
                {
                    var fr = r.ReadMessage();
                    while (fr.TryReadTag(out int ff, out int fw))
                    {
                        if (ff == 1) fr.ReadVarints(fw, faces);
                        else fr.Skip(fw);
                    }
                }
                else r.Skip(w);
            }
            if (verts == null) return null;
            int n = verts.Components[0].Count;

            // Faces: plain indices, or zig-zag deltas.
            int[] idx = new int[faces.Count];
            bool plainOk = true;
            for (int i = 0; i < idx.Length && plainOk; i++)
            {
                if (faces[i] >= (ulong)n) plainOk = false;
                else idx[i] = (int)faces[i];
            }
            if (!plainOk)
            {
                long acc = 0;
                for (int i = 0; i < idx.Length; i++)
                {
                    acc += ProtoReader.ZigZag(faces[i]);
                    if (acc < 0 || acc >= n) throw new FormatException($"Quantized chunk '{name}' has out-of-range face indices.");
                    idx[i] = (int)acc;
                }
            }
            return new RawQuantized { Verts = verts, Uvs = uvs, Faces = idx, Name = name, Material = material };
        }

        /// <summary>
        /// Mean triangle edge length relative to the mean distance between unrelated vertices. Real meshes connect
        /// nearby vertices (ratio well below 1); a wrong decoding scatters them (ratio near 1).
        /// </summary>
        static double EdgeRatio(float[] xyz, int[] idx)
        {
            int n = xyz.Length / 3;
            if (idx.Length < 3 || n < 2) return 0;
            Vec3 V(int i) => new Vec3(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]);
            double edges = 0;
            int edgeCount = 0;
            int step = Math.Max(1, idx.Length / 3 / 4000) * 3;
            for (int t = 0; t + 2 < idx.Length; t += step)
            {
                edges += (V(idx[t]) - V(idx[t + 1])).Length + (V(idx[t + 1]) - V(idx[t + 2])).Length;
                edgeCount += 2;
            }
            double pairs = 0;
            int pairCount = 0;
            var rng = new Random(n);
            for (int k = 0; k < 4000; k++)
            {
                pairs += (V(rng.Next(n)) - V(rng.Next(n))).Length;
                pairCount++;
            }
            return edges / Math.Max(1, edgeCount) / Math.Max(1e-12, pairs / pairCount);
        }
    }

    /// <summary>Writes .dam files (simple chunks, optionally quantized) - used by the synthetic test space and tests.</summary>
    public static class DamWriter
    {
        public static byte[] Write(MeshData mesh, bool quantized = false, float quantum = 0.0005f)
        {
            var root = new ProtoWriter();
            foreach (var part in mesh.Parts)
            {
                // Each part becomes a self-contained chunk with its own (compacted) vertex list.
                var map = new Dictionary<int, int>();
                var local = new List<int>();
                foreach (var vi in part.Indices)
                {
                    if (!map.TryGetValue(vi, out int li)) { li = map.Count; map[vi] = li; local.Add(vi); }
                }
                var xyz = new List<float>(local.Count * 3);
                var uv = new List<float>(local.Count * 2);
                foreach (var vi in local)
                {
                    xyz.Add(mesh.Positions[3 * vi]);
                    xyz.Add(mesh.Positions[3 * vi + 1]);
                    xyz.Add(mesh.Positions[3 * vi + 2]);
                    if (mesh.HasUvs)
                    {
                        uv.Add(mesh.Uvs[2 * vi]);
                        uv.Add(mesh.Uvs[2 * vi + 1]);
                    }
                }
                var faces = new List<ulong>(part.Indices.Length);
                foreach (var vi in part.Indices) faces.Add((ulong)map[vi]);

                var chunk = new ProtoWriter();
                if (!quantized)
                {
                    var verts = new ProtoWriter();
                    verts.PackedFloats(1, xyz);
                    if (uv.Count > 0) verts.PackedFloats(4, uv);
                    chunk.Message(1, verts);
                    var f = new ProtoWriter();
                    f.PackedVarints(1, faces);
                    chunk.Message(2, f);
                    if (part.Name != null) chunk.String(3, part.Name);
                    if (part.Material != null) chunk.String(4, part.Material);
                    root.Message(1, chunk);
                }
                else
                {
                    if (part.Name != null) chunk.String(1, part.Name);
                    if (part.Material != null) chunk.String(2, part.Material);
                    chunk.Message(3, QuantizeDelta(xyz, 3, quantum));
                    if (uv.Count > 0) chunk.Message(4, QuantizeDelta(uv, 2, 1f / 65536f));
                    var f = new ProtoWriter();
                    f.PackedVarints(1, faces);
                    chunk.Message(5, f);
                    root.Message(2, chunk);
                }
            }
            return root.ToArray();
        }

        static ProtoWriter QuantizeDelta(List<float> values, int components, float quantum)
        {
            var w = new ProtoWriter();
            w.FloatField(1, quantum);
            var offset = new float[components];
            int n = values.Count / components;
            for (int c = 0; c < components; c++)
            {
                float mn = float.MaxValue;
                for (int i = 0; i < n; i++) mn = Math.Min(mn, values[i * components + c]);
                offset[c] = n > 0 ? mn : 0f;
            }
            w.PackedFloats(2, offset);
            for (int c = 0; c < components; c++)
            {
                var deltas = new List<ulong>(n);
                long prev = 0;
                for (int i = 0; i < n; i++)
                {
                    long q = (long)Math.Round((values[i * components + c] - offset[c]) / quantum);
                    deltas.Add(ProtoWriter.ZigZag(q - prev));
                    prev = q;
                }
                w.PackedVarints(3 + c, deltas);
            }
            return w;
        }
    }
}
