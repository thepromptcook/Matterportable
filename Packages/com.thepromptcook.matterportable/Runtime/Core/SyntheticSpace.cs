using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Matterportable.Core
{
    public sealed class SyntheticOptions
    {
        public string ModelId = "DemoSpace01";
        public string Name = "Matterportable demo house";
        /// <summary>Panorama tiers to write; panoramas are rendered at the largest and box-downsampled.</summary>
        public int[] PanoSizes = { 256, 1024 };
        public int TextureSize = 512;
        public int Seed = 7;
        /// <summary>Conventions used when writing the data. Defaults to Matterport's (Z-up, default face layout).</summary>
        public SpaceCalibration Truth = new SpaceCalibration { Method = SpaceCalibration.Synthetic };
        public bool QuantizedMesh;
        public bool RandomRotations = true;
        public string ImageExtension = "png";
        /// <summary>Also render every sweep without its furniture, written as the space's clean photos
        /// (<see cref="CleanPanoramas"/>): exact, where a real space's are painted in.</summary>
        public bool CleanPhotos;
    }

    /// <summary>
    /// A procedurally generated three-room house written in exactly the same format as a downloaded Matterport space
    /// (DAM mesh, textures, cube-face panoramas, manifest). Used for offline tests, the solver tests and the
    /// "Generate Demo Space" menu so the whole Unity pipeline can be tried without network access.
    /// </summary>
    public sealed class SyntheticSpace
    {
        public SyntheticOptions Options;
        /// <summary>Mesh in the source frame (as written to the .dam file).</summary>
        public MeshData SourceMesh;
        /// <summary>The same mesh in Unity space (used for rendering the panoramas).</summary>
        public SpaceGeometry Geometry;
        public Dictionary<string, RgbImage> Textures = new Dictionary<string, RgbImage>();
        public SpaceManifest Manifest;
        /// <summary>Per sweep, per tier (ascending), six faces in source order.</summary>
        public List<List<RgbImage[]>> Panoramas = new List<List<RgbImage[]>>();
        /// <summary>With <see cref="SyntheticOptions.CleanPhotos"/>, once written: per sweep, the largest tier's faces
        /// without the props the viewers find.</summary>
        public List<RgbImage[]> CleanFaces = new List<RgbImage[]>();
        /// <summary>World-from-pano rotation actually used for each sweep (Unity space).</summary>
        public List<Mat3> TrueRotations = new List<Mat3>();

        // ---- Scene construction (Unity space: +X right, +Y up, +Z forward) ----

        readonly List<Vec3> _pos = new List<Vec3>();
        readonly List<Vec2> _uv = new List<Vec2>();
        readonly Dictionary<string, List<int>> _parts = new Dictionary<string, List<int>>();
        readonly List<string> _partOrder = new List<string>();

        const float RoomHeight = 2.7f;

        string Mat(int i) => Options.ModelId + "_50k_" + i.ToString("000");

        void AddRect(Vec3 origin, Vec3 u, Vec3 v, float u0, float u1, float v0, float v1, string material, float tile)
        {
            // Front side faces along cross(v, u); triangles are clockwise as seen from the front (Unity convention).
            var a = origin + u * u0 + v * v0;
            var b = origin + u * u1 + v * v0;
            var c = origin + u * u1 + v * v1;
            var d = origin + u * u0 + v * v1;
            int baseIndex = _pos.Count;
            foreach (var p in new[] { a, b, c, d })
            {
                _pos.Add(p);
                _uv.Add(new Vec2(Vec3.Dot(p, u) / tile, Vec3.Dot(p, v) / tile));
            }
            if (!_parts.TryGetValue(material, out var list))
            {
                list = new List<int>();
                _parts[material] = list;
                _partOrder.Add(material);
            }
            list.AddRange(new[] { baseIndex, baseIndex + 3, baseIndex + 2, baseIndex, baseIndex + 2, baseIndex + 1 });
        }

        /// <summary>Vertical wall from (x0,z0) to (x1,z1) whose front faces <paramref name="normal"/>, with rectangular holes.</summary>
        void AddWall(Vec3 start, Vec3 end, Vec3 normal, string material, params (float from, float to, float bottom, float top)[] holes)
        {
            var along = end - start;
            float length = along.Length;
            var dir = along / length;
            var up = Vec3.Up;
            var u = Vec3.Cross(normal, up); // right as seen from the front
            bool reversed = Vec3.Dot(u, dir) < 0;
            // Work in "distance along u" coordinates measured from the wall's left end as seen from the front.
            var origin = reversed ? end : start;
            var cuts = new List<float> { 0f, length };
            foreach (var h in holes)
            {
                float f = reversed ? length - h.to : h.from, t = reversed ? length - h.from : h.to;
                cuts.Add(f);
                cuts.Add(t);
            }
            cuts.Sort();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                float a = cuts[i], b = cuts[i + 1];
                if (b - a < 1e-4f) continue;
                float mid = (a + b) * 0.5f;
                float midAlong = reversed ? length - mid : mid;
                var hole = Array.Find(holes, h => midAlong > h.from && midAlong < h.to);
                bool inHole = midAlong > hole.from && midAlong < hole.to && hole.top > hole.bottom;
                if (!inHole)
                {
                    AddRect(origin, u, up, a, b, 0, RoomHeight, material, 1.2f);
                }
                else
                {
                    if (hole.bottom > 0) AddRect(origin, u, up, a, b, 0, hole.bottom, material, 1.2f);
                    if (hole.top < RoomHeight) AddRect(origin, u, up, a, b, hole.top, RoomHeight, material, 1.2f);
                }
            }
        }

        /// <summary>Thick interior wall along +X or +Z with both faces, door reveals and lintels.</summary>
        void AddInteriorWall(Vec3 start, Vec3 end, string positiveSide, string negativeSide, params (float from, float to, float bottom, float top)[] holes)
        {
            const float half = 0.06f;
            var dir = (end - start).Normalized;
            var side = Vec3.Cross(dir, Vec3.Up); // horizontal, perpendicular to the wall
            var n = side.X < -0.5f || side.Z < -0.5f ? -side : side;
            AddWall(start + n * half, end + n * half, n, positiveSide, holes);
            AddWall(start - n * half, end - n * half, -n, negativeSide, holes);
            foreach (var h in holes)
            {
                var a = start + dir * h.from;
                var b = start + dir * h.to;
                // Front = cross(V, U): reveals face into the opening, the lintel faces down, the sill faces up.
                AddRect(a - side * half, side, Vec3.Up, 0, 2 * half, h.bottom, h.top, negativeSide, 1.2f);
                AddRect(b + side * half, -side, Vec3.Up, 0, 2 * half, h.bottom, h.top, negativeSide, 1.2f);
                AddRect(b - side * half + Vec3.Up * h.top, -dir, side, 0, h.to - h.from, 0, 2 * half, negativeSide, 1.2f);
                if (h.bottom > 0)
                    AddRect(a - side * half + Vec3.Up * h.bottom, dir, side, 0, h.to - h.from, 0, 2 * half, negativeSide, 1.2f);
            }
        }

        void AddBox(Vec3 min, Vec3 max, string material)
        {
            var size = max - min;
            // Outward-facing sides (front = outside), plus top.
            AddRect(new Vec3(min.X, min.Y, min.Z), Vec3.Right, Vec3.Up, 0, size.X, 0, size.Y, material, 0.6f);           // -Z face
            AddRect(new Vec3(max.X, min.Y, max.Z), -Vec3.Right, Vec3.Up, 0, size.X, 0, size.Y, material, 0.6f);          // +Z face
            AddRect(new Vec3(min.X, min.Y, max.Z), -Vec3.Forward, Vec3.Up, 0, size.Z, 0, size.Y, material, 0.6f);        // -X face
            AddRect(new Vec3(max.X, min.Y, min.Z), Vec3.Forward, Vec3.Up, 0, size.Z, 0, size.Y, material, 0.6f);         // +X face
            AddRect(new Vec3(min.X, max.Y, min.Z), Vec3.Right, Vec3.Forward, 0, size.X, 0, size.Z, material, 0.6f);      // top
        }

        void BuildHouse()
        {
            var back = new Vec3(0, 0, -1);
            var fwd = new Vec3(0, 0, 1);
            var left = new Vec3(-1, 0, 0);
            var right = new Vec3(1, 0, 0);

            // Floors (front faces up) and ceilings (front faces down).
            AddRect(Vec3.Zero, Vec3.Right, Vec3.Forward, 0, 6, 0, 5, Mat(0), 1.0f);
            AddRect(Vec3.Zero, Vec3.Right, Vec3.Forward, 6, 10, 0, 5, Mat(1), 1.0f);
            AddRect(Vec3.Zero, Vec3.Right, Vec3.Forward, 0, 10, 5, 9, Mat(0), 1.0f);
            AddRect(new Vec3(0, RoomHeight, 0), -Vec3.Right, Vec3.Forward, -10, 0, 0, 9, Mat(2), 1.5f);

            // Exterior walls facing inwards, some with windows.
            AddWall(new Vec3(0, 0, 0), new Vec3(10, 0, 0), fwd, Mat(3), (1.5f, 3.5f, 0.9f, 2.1f), (7f, 9f, 0.9f, 2.1f));
            AddWall(new Vec3(0, 0, 9), new Vec3(10, 0, 9), back, Mat(4), (4f, 6.5f, 0.8f, 2.2f));
            AddWall(new Vec3(0, 0, 0), new Vec3(0, 0, 9), right, Mat(5));
            AddWall(new Vec3(10, 0, 0), new Vec3(10, 0, 9), left, Mat(6), (6f, 8f, 1.0f, 2.0f));

            // Thick interior walls with doorways.
            AddInteriorWall(new Vec3(6, 0, 0), new Vec3(6, 0, 4.94f), Mat(5), Mat(6), (2f, 3f, 0f, 2.1f));
            AddInteriorWall(new Vec3(0, 0, 5), new Vec3(10, 0, 5), Mat(3), Mat(4), (2f, 3f, 0f, 2.1f), (7.5f, 8.5f, 0f, 2.1f));

            // Furniture.
            AddBox(new Vec3(2.2f, 0, 2.05f), new Vec3(3.8f, 0.78f, 2.95f), Mat(7));
            AddBox(new Vec3(8.6f, 0, 1.0f), new Vec3(9.6f, 0.85f, 4.0f), Mat(8));
            AddBox(new Vec3(0.3f, 0, 7.5f), new Vec3(1.0f, 1.9f, 8.7f), Mat(7));
            AddBox(new Vec3(5.8f, 0, 6.8f), new Vec3(6.2f, RoomHeight, 7.2f), Mat(8));
        }

        static readonly Vec3[] SweepPositions =
        {
            new Vec3(1.2f, 1.55f, 1.2f), new Vec3(4.8f, 1.55f, 1.0f), new Vec3(1.5f, 1.55f, 4.2f), new Vec3(4.6f, 1.55f, 4.0f),
            new Vec3(7.2f, 1.55f, 1.5f), new Vec3(7.5f, 1.55f, 3.8f),
            new Vec3(2.5f, 1.55f, 6.5f), new Vec3(4.6f, 1.55f, 8.0f), new Vec3(8.0f, 1.55f, 6.5f), new Vec3(8.5f, 1.55f, 8.2f),
        };

        // ---- Procedural textures ----

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }

        /// <summary>Tileable value noise with the given period (cells per texture).</summary>
        static float Noise(float x, float y, int period, int seed)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            int Wrap(int v) => ((v % period) + period) % period;
            float a = Hash(Wrap(x0), Wrap(y0), seed), b = Hash(Wrap(x0 + 1), Wrap(y0), seed);
            float c = Hash(Wrap(x0), Wrap(y0 + 1), seed), d = Hash(Wrap(x0 + 1), Wrap(y0 + 1), seed);
            return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
        }

        static RgbImage MakeTexture(int kind, int size, int seed)
        {
            var img = new RgbImage(size, size);
            var rng = new Random(seed);
            float hue = (float)rng.NextDouble();
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size, v = y / (float)size;
                float n1 = Noise(u * 4, v * 4, 4, seed), n2 = Noise(u * 16, v * 16, 16, seed + 1), n3 = Noise(u * 48, v * 48, 48, seed + 2);
                float r, g, b;
                switch (kind)
                {
                    case 0: // wood floor planks
                    {
                        int plank = (int)(v * 8);
                        float shade = 0.55f + 0.35f * Hash(plank, 3, seed);
                        float grain = 0.85f + 0.15f * Noise(u * 64, v * 4, 64, seed + plank);
                        float gap = (v * 8 - plank) < 0.04f ? 0.4f : 1f;
                        r = 0.62f * shade * grain * gap; g = 0.42f * shade * grain * gap; b = 0.25f * shade * grain * gap;
                        break;
                    }
                    case 1: // tiles
                    {
                        bool line = (u * 6 % 1) < 0.05f || (v * 6 % 1) < 0.05f;
                        float t = 0.75f + 0.2f * Hash((int)(u * 6), (int)(v * 6), seed);
                        r = g = b = line ? 0.35f : t;
                        b += 0.05f;
                        break;
                    }
                    case 2: // ceiling
                    {
                        r = g = b = 0.86f + 0.08f * n2;
                        float lampDx = (u % 0.5f) - 0.25f, lampDy = (v % 0.5f) - 0.25f;
                        if (lampDx * lampDx + lampDy * lampDy < 0.004f) { r = g = b = 1f; }
                        break;
                    }
                    case 7:
                    case 8: // furniture fabric/wood with stripes
                    {
                        float stripe = ((int)(u * 10) % 2 == 0) ? 1f : 0.7f;
                        HsvToRgb(hue, 0.65f, (0.45f + 0.4f * n1) * stripe, out r, out g, out b);
                        break;
                    }
                    default: // painted walls with blobs and "pictures"
                    {
                        HsvToRgb(hue, 0.35f + 0.25f * n1, 0.55f + 0.3f * n2 + 0.1f * n3, out r, out g, out b);
                        float px = u * 2 % 1, py = v * 2 % 1;
                        if (px > 0.3f && px < 0.7f && py > 0.35f && py < 0.75f)
                        {
                            HsvToRgb((hue + 0.5f + 0.3f * Hash((int)(u * 2), (int)(v * 2), seed)) % 1f, 0.8f, 0.3f + 0.6f * n3, out r, out g, out b);
                        }
                        break;
                    }
                }
                img.SetLinear(x, y, r, g, b);
            }
            return img;
        }

        static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
        {
            h = (h % 1f + 1f) % 1f * 6f;
            int i = (int)h;
            float f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
            switch (i)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
        }

        static void Sky(Vec3 d, out float r, out float g, out float b)
        {
            float t = Math.Max(0, d.Normalized.Y);
            r = 0.55f + 0.25f * (1 - t);
            g = 0.70f + 0.15f * (1 - t);
            b = 0.95f;
            if (d.Y < 0) { r = 0.35f; g = 0.45f; b = 0.25f; } // grass below the horizon
        }

        // ---- Generation ----

        public static SyntheticSpace Generate(SyntheticOptions options = null)
        {
            var s = new SyntheticSpace { Options = options ?? new SyntheticOptions() };
            s.Build();
            return s;
        }

        void Build()
        {
            var o = Options;
            var truth = o.Truth;
            BuildHouse();

            // Unity-space mesh → source frame (inverse of the Unity conversion; the matrices are orthogonal).
            var toSource = Frames.ToUnity(truth.MeshFrame).Transposed;
            var positions = new float[_pos.Count * 3];
            var uvs = new float[_uv.Count * 2];
            for (int i = 0; i < _pos.Count; i++)
            {
                var p = toSource * _pos[i];
                positions[3 * i] = p.X;
                positions[3 * i + 1] = p.Y;
                positions[3 * i + 2] = p.Z;
                uvs[2 * i] = _uv[i].X;
                uvs[2 * i + 1] = truth.FlipTextureV ? 1f - _uv[i].Y : _uv[i].Y;
            }
            SourceMesh = new MeshData { Positions = positions, Uvs = uvs };
            // Mirroring into a right-handed frame already turns Unity's clockwise fronts into counter-clockwise
            // ones (the natural convention there); only simulate broken winding when asked to.
            bool flipWinding = truth.FlipWinding;
            foreach (var name in _partOrder)
            {
                var idx = _parts[name].ToArray();
                if (flipWinding)
                    for (int t = 0; t < idx.Length; t += 3) { int tmp = idx[t + 1]; idx[t + 1] = idx[t + 2]; idx[t + 2] = tmp; }
                SourceMesh.Parts.Add(new MeshPart { Name = "chunk_" + name, Material = name, Indices = idx });
            }
            Geometry = SpaceGeometry.Build(SourceMesh, truth);

            // Textures.
            int kindIndex = 0;
            foreach (var name in _partOrder)
            {
                int kind = int.Parse(name.Substring(name.Length - 3));
                Textures[name] = MakeTexture(kind, o.TextureSize, o.Seed * 31 + kind);
                kindIndex++;
            }

            // Sweeps and panoramas.
            var rng = new Random(o.Seed);
            var toSweepSource = Frames.ToUnity(truth.SweepFrame).Transposed;
            Manifest = new SpaceManifest
            {
                ModelId = o.ModelId,
                Name = o.Name,
                Source = "synthetic",
                SourceUrl = "synthetic://" + o.ModelId,
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                Frame = truth.SweepFrame,
                Mesh = new MeshEntry { File = CacheLayout.MeshDir + "/" + o.ModelId + "_50k.dam", Format = "dam", Resolution = "50k" },
                Floors = new List<FloorEntry> { new FloorEntry { Id = "floor0", Index = 0, Label = "Ground floor" } },
            };
            foreach (var name in _partOrder)
                Manifest.Textures.Add(new TextureEntry { Material = name, File = CacheLayout.TexturesDir + "/" + name + ".png" });

            var sizes = (int[])o.PanoSizes.Clone();
            Array.Sort(sizes);
            int renderSize = sizes[sizes.Length - 1];
            for (int i = 0; i < SweepPositions.Length; i++)
            {
                var pos = SweepPositions[i];
                Mat3 rot = Mat3.Identity;
                double[] storedQuat;
                if (truth.Rotation != RotationMode.Identity && o.RandomRotations)
                {
                    double yaw = rng.NextDouble() * Math.PI * 2;
                    double tiltX = (rng.NextDouble() - 0.5) * 0.06, tiltZ = (rng.NextDouble() - 0.5) * 0.06;
                    var q = Quat.AxisAngle(Vec3.Up, yaw) * Quat.AxisAngle(Vec3.Right, tiltX) * Quat.AxisAngle(Vec3.Forward, tiltZ);
                    rot = q.ToMat3();
                    // Stored quaternion such that SpaceCalibration.WorldFromPano reproduces 'rot'.
                    var m = Frames.ToUnity(truth.SweepFrame);
                    var r = truth.Rotation == RotationMode.Direct ? rot : rot.Transposed;
                    storedQuat = Quat.FromMat3(m.Transposed * r * m).ToArray();
                }
                else
                {
                    storedQuat = truth.Rotation == RotationMode.Identity
                        ? Quat.AxisAngle(Vec3.Up, rng.NextDouble() * 6.28).ToArray() // must be ignored
                        : Quat.Identity.ToArray();
                }
                TrueRotations.Add(rot);

                var faces = RenderPano(pos, rot, truth.Faces, renderSize);
                var tiers = new List<RgbImage[]>();
                var entry = new SweepEntry
                {
                    Id = "sweep" + i.ToString("00"),
                    Index = i,
                    Label = "",
                    FloorId = "floor0",
                    RoomId = i < 4 ? "roomA" : i < 6 ? "roomB" : "roomC",
                    Position = ToArray(toSweepSource * pos),
                    Rotation = storedQuat,
                };
                foreach (var size in sizes)
                {
                    var tierFaces = new RgbImage[6];
                    for (int f = 0; f < 6; f++) tierFaces[f] = faces[f].Downsample(renderSize / size);
                    tiers.Add(tierFaces);
                    entry.Tiers.Add(new PanoTier { Size = size, Directory = CacheLayout.PanoTierDir(entry.Id, size), Extension = o.ImageExtension });
                }
                Panoramas.Add(tiers);
                Manifest.Sweeps.Add(entry);
            }
            // Neighbour graph like Matterport's (adjacent sweeps that can see each other).
            for (int i = 0; i < Manifest.Sweeps.Count; i++)
            for (int j = 0; j < Manifest.Sweeps.Count; j++)
            {
                if (i == j) continue;
                var a = SweepPositions[i];
                var b = SweepPositions[j];
                if (Vec3.Distance(a, b) < 6.5f && !Geometry.Bvh.Occluded(a, b, 0.1f)) Manifest.Sweeps[i].Neighbors.Add(Manifest.Sweeps[j].Id);
            }
        }

        static double[] ToArray(Vec3 v) => new double[] { v.X, v.Y, v.Z };

        RgbImage[] RenderPano(Vec3 origin, Mat3 worldFromPano, FaceBasis[] layout, int size, SpaceGeometry geo = null, bool[] skip = null)
        {
            geo = geo ?? Geometry;
            var faces = new RgbImage[6];
            var light = new Vec3(0.3f, 1f, 0.2f).Normalized;
            for (int f = 0; f < 6; f++)
            {
                var img = new RgbImage(size, size);
                var basis = layout[f];
                Parallel.For(0, size, y =>
                {
                    for (int x = 0; x < size; x++)
                    {
                        var local = basis.Direction((x + 0.5f) / size, (y + 0.5f) / size);
                        var d = (worldFromPano * local).Normalized;
                        float r, g, b;
                        if (geo.Bvh.Raycast(origin, d, 100f, out var hit, skip))
                        {
                            var uv = geo.UvAt(hit, out int part);
                            // Geometry UVs are already corrected to Unity's convention.
                            Textures[geo.Parts[part].Material].SampleWrapped(uv.X, uv.Y, out r, out g, out b);
                            float shade = 0.78f + 0.22f * Math.Abs(Vec3.Dot(hit.Normal, light));
                            shade *= 1f - Math.Min(0.25f, hit.Distance * 0.015f);
                            r *= shade; g *= shade; b *= shade;
                        }
                        else Sky(d, out r, out g, out b);
                        img.SetLinear(x, y, r, g, b);
                    }
                });
                faces[f] = img;
            }
            return faces;
        }

        /// <summary>Writes the space in cache layout. <paramref name="encode"/> turns an image into file bytes.</summary>
        public void WriteTo(string spaceDir, Func<RgbImage, byte[]> encode = null, bool writeCalibration = true)
        {
            encode = encode ?? Png.Encode;
            Directory.CreateDirectory(spaceDir);
            IoUtil.WriteAllBytesAtomic(CacheLayout.Resolve(spaceDir, Manifest.Mesh.File), DamWriter.Write(SourceMesh, Options.QuantizedMesh));
            foreach (var t in Manifest.Textures)
                IoUtil.WriteAllBytesAtomic(CacheLayout.Resolve(spaceDir, t.File), Png.Encode(Textures[t.Material]));
            for (int i = 0; i < Manifest.Sweeps.Count; i++)
            {
                var entry = Manifest.Sweeps[i];
                for (int k = 0; k < entry.Tiers.Count; k++)
                for (int f = 0; f < 6; f++)
                    IoUtil.WriteAllBytesAtomic(CacheLayout.Resolve(spaceDir, entry.Tiers[k].FacePath(f)), encode(Panoramas[i][k][f]));
            }
            Manifest.Save(spaceDir);
            if (writeCalibration)
            {
                var cal = Options.Truth.Clone();
                cal.Method = SpaceCalibration.Synthetic;
                cal.Save(spaceDir);
            }
            if (Options.CleanPhotos && writeCalibration) WriteCleanPhotos(spaceDir, encode);
        }

        /// <summary>The clean photos as the reconstruction writes them (recon/clean: faces, painted-prop maps from the
        /// props the viewers find, clean.json), so the demo shows what a perfect reconstruction would.</summary>
        void WriteCleanPhotos(string spaceDir, Func<RgbImage, byte[]> encode)
        {
            var model = SpaceModel.Load(spaceDir, SpaceManifest.Load(spaceDir), SpaceCalibration.LoadOrDefault(spaceDir), null); // finds the props
            var pieces = model.PieceOfTriangle;
            var skip = new bool[pieces.Length];
            for (int t = 0; t < skip.Length; t++) skip[t] = pieces[t] > 0;
            int size = Options.PanoSizes.Max();
            CleanFaces.Clear();
            for (int s = 0; s < SweepPositions.Length; s++)
                CleanFaces.Add(RenderPano(SweepPositions[s], TrueRotations[s], Options.Truth.Faces, size, model.Geometry, skip));
            const int mapSize = 256;
            var faces = JsonValue.NewArray();
            for (int s = 0; s < CleanFaces.Count; s++)
                for (int f = 0; f < 6; f++)
                {
                    var mask = PropMasks.PieceMask(model, s, f, mapSize);
                    var map = new RgbImage(mapSize, mapSize);
                    bool any = false;
                    for (int i = 0; i < mask.Length; i++)
                    {
                        if (mask[i] <= 0 || mask[i] > 255) continue;
                        map.Set(i % mapSize, i / mapSize, (byte)mask[i], (byte)mask[i], (byte)mask[i]);
                        any = true;
                    }
                    if (!any) continue;
                    var ext = Options.ImageExtension;
                    string file = $"{s:D3}/face{f}.{ext}", painted = $"{s:D3}/painted{f}.png";
                    IoUtil.WriteAllBytesAtomic(Path.Combine(spaceDir, CleanPanoramas.Folder, file), encode(CleanFaces[s][f]));
                    IoUtil.WriteAllBytesAtomic(Path.Combine(spaceDir, CleanPanoramas.Folder, painted), Png.Encode(map));
                    faces.Add(JsonValue.NewObject().Set("sweep", s).Set("face", f).Set("file", file).Set("painted", painted));
                }
            IoUtil.WriteAllTextAtomic(CleanPanoramas.IndexPath(spaceDir), JsonValue.NewObject().Set("faces", faces).ToJson(true));
        }

        /// <summary>Builds solver input straight from memory (no files), as if decoded from disk.</summary>
        public AlignmentInput ToAlignmentInput(SpaceCalibration assumed, int sweepCount = 6)
        {
            var cal = assumed.Clone();
            cal.FlipTextureV = false;
            cal.FlipWinding = false;
            var input = new AlignmentInput
            {
                Geometry = SpaceGeometry.Build(SourceMesh, cal),
                SweepFrame = cal.SweepFrame,
            };
            foreach (var kv in Textures) input.Textures[kv.Key] = kv.Value.ToLuma();
            var m = Frames.ToUnity(cal.SweepFrame);
            foreach (int i in PanoAlignmentSolver.PickSweeps(Manifest.Sweeps.Count, sweepCount))
            {
                var e = Manifest.Sweeps[i];
                var faces = new LumaImage[6];
                for (int f = 0; f < 6; f++) faces[f] = Panoramas[i][0][f].ToLuma();
                input.Sweeps.Add(new AlignmentSweepInput
                {
                    SweepIndex = i,
                    Position = m * new Vec3(e.Position[0], e.Position[1], e.Position[2]),
                    Rotation = e.Rotation,
                    Faces = faces,
                });
            }
            return input;
        }
    }
}
