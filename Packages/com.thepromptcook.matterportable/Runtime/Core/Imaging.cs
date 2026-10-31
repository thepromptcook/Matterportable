using System;
using System.IO;
using System.IO.Compression;

namespace Matterportable.Core
{
    /// <summary>8-bit RGB image. Rows are stored bottom-up (row 0 = bottom), matching Unity's texture layout.</summary>
    public sealed class RgbImage
    {
        public readonly int Width, Height;
        public readonly byte[] Pixels;

        public RgbImage(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 3];
        }

        public RgbImage(int width, int height, byte[] pixels)
        {
            if (pixels.Length != width * height * 3) throw new ArgumentException("Pixel buffer size mismatch.");
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public void Set(int x, int y, byte r, byte g, byte b)
        {
            int i = (y * Width + x) * 3;
            Pixels[i] = r;
            Pixels[i + 1] = g;
            Pixels[i + 2] = b;
        }

        public void SetLinear(int x, int y, float r, float g, float b) =>
            Set(x, y, ToByte(r), ToByte(g), ToByte(b));

        static byte ToByte(float v) => (byte)Math.Max(0, Math.Min(255, (int)(v * 255f + 0.5f)));

        /// <summary>Bilinear sample with (u, v) in [0,1], v = 0 at the bottom; clamps at the edges. Returns 0..1.</summary>
        public void Sample(float u, float v, out float r, out float g, out float b)
        {
            float fx = u * Width - 0.5f, fy = v * Height - 0.5f;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            r = g = b = 0;
            for (int k = 0; k < 4; k++)
            {
                int x = Clamp(x0 + (k & 1), Width), y = Clamp(y0 + (k >> 1), Height);
                float w = ((k & 1) == 1 ? tx : 1 - tx) * ((k >> 1) == 1 ? ty : 1 - ty);
                int i = (y * Width + x) * 3;
                r += Pixels[i] * w;
                g += Pixels[i + 1] * w;
                b += Pixels[i + 2] * w;
            }
            r /= 255f;
            g /= 255f;
            b /= 255f;
        }

        static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;

        public void SampleWrapped(float u, float v, out float r, out float g, out float b) =>
            Sample(u - (float)Math.Floor(u), v - (float)Math.Floor(v), out r, out g, out b);

        /// <summary>Box-filtered downsample by an integer factor.</summary>
        public RgbImage Downsample(int factor)
        {
            if (factor <= 1) return this;
            int w = Width / factor, h = Height / factor;
            var r = new RgbImage(w, h);
            int area = factor * factor;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sr = 0, sg = 0, sb = 0;
                for (int yy = 0; yy < factor; yy++)
                for (int xx = 0; xx < factor; xx++)
                {
                    int i = ((y * factor + yy) * Width + x * factor + xx) * 3;
                    sr += Pixels[i];
                    sg += Pixels[i + 1];
                    sb += Pixels[i + 2];
                }
                r.Set(x, y, (byte)(sr / area), (byte)(sg / area), (byte)(sb / area));
            }
            return r;
        }

        public LumaImage ToLuma()
        {
            var l = new LumaImage(Width, Height);
            for (int i = 0, p = 0; i < l.Pixels.Length; i++, p += 3)
                l.Pixels[i] = (0.299f * Pixels[p] + 0.587f * Pixels[p + 1] + 0.114f * Pixels[p + 2]) / 255f;
            return l;
        }
    }

    /// <summary>Single-channel float image (0..1), bottom-up rows. Used by the alignment solver.</summary>
    public sealed class LumaImage
    {
        public readonly int Width, Height;
        public readonly float[] Pixels;

        public LumaImage(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new float[width * height];
        }

        public float this[int x, int y]
        {
            get => Pixels[y * Width + x];
            set => Pixels[y * Width + x] = value;
        }

        /// <summary>Box-filtered downsample to the given size (sizes need not divide evenly).</summary>
        public LumaImage Resize(int width, int height)
        {
            var r = new LumaImage(width, height);
            for (int y = 0; y < height; y++)
            {
                int y0 = y * Height / height, y1 = Math.Max(y0 + 1, (y + 1) * Height / height);
                for (int x = 0; x < width; x++)
                {
                    int x0 = x * Width / width, x1 = Math.Max(x0 + 1, (x + 1) * Width / width);
                    float sum = 0;
                    for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++) sum += Pixels[yy * Width + xx];
                    r.Pixels[y * width + x] = sum / ((x1 - x0) * (y1 - y0));
                }
            }
            return r;
        }

        /// <summary>Bilinear sample with (u, v) in [0,1], v = 0 at the bottom; clamps at the edges.</summary>
        public float Sample(float u, float v)
        {
            float fx = u * Width - 0.5f, fy = v * Height - 0.5f;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            int xa = Math.Max(0, Math.Min(Width - 1, x0)), xb = Math.Max(0, Math.Min(Width - 1, x0 + 1));
            int ya = Math.Max(0, Math.Min(Height - 1, y0)), yb = Math.Max(0, Math.Min(Height - 1, y0 + 1));
            float a = Pixels[ya * Width + xa], b = Pixels[ya * Width + xb];
            float c = Pixels[yb * Width + xa], d = Pixels[yb * Width + xb];
            return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
        }

        /// <summary>Repeating sample (for mesh textures whose UVs may wrap).</summary>
        public float SampleWrapped(float u, float v)
        {
            u -= (float)Math.Floor(u);
            v -= (float)Math.Floor(v);
            return Sample(u, v);
        }
    }

    /// <summary>Reads image dimensions from PNG/JPEG headers without decoding.</summary>
    public static class ImageInfo
    {
        public static bool TryReadSize(string path, out int width, out int height)
        {
            width = height = 0;
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var buf = new byte[Math.Min(fs.Length, 1 << 18)];
                    int read = 0;
                    while (read < buf.Length)
                    {
                        int n = fs.Read(buf, read, buf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return TryReadSize(buf, read, out width, out height);
                }
            }
            catch (IOException) { return false; }
        }

        public static bool TryReadSize(byte[] b, int length, out int width, out int height)
        {
            width = height = 0;
            if (length >= 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
            {
                width = BigEndian32(b, 16);
                height = BigEndian32(b, 20);
                return true;
            }
            if (length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    byte marker = b[i + 1];
                    if (marker == 0xFF) { i++; continue; }
                    if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                    int segLen = (b[i + 2] << 8) | b[i + 3];
                    bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                    if (sof)
                    {
                        height = (b[i + 5] << 8) | b[i + 6];
                        width = (b[i + 7] << 8) | b[i + 8];
                        return true;
                    }
                    i += 2 + segLen;
                }
            }
            return false;
        }

        static int BigEndian32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
    }

    /// <summary>Minimal PNG codec (8-bit gray/RGB/RGBA, non-interlaced) for synthetic data and tests.</summary>
    public static class Png
    {
        static readonly uint[] CrcTable = BuildCrcTable();

        public static byte[] Encode(RgbImage img)
        {
            int stride = img.Width * 3;
            var raw = new byte[(stride + 1) * img.Height];
            for (int y = 0; y < img.Height; y++)
            {
                int srcRow = (img.Height - 1 - y) * stride; // PNG is top-down
                int dst = y * (stride + 1);
                raw[dst] = 1; // Sub filter
                for (int x = 0; x < stride; x++)
                {
                    byte left = x >= 3 ? img.Pixels[srcRow + x - 3] : (byte)0;
                    raw[dst + 1 + x] = (byte)(img.Pixels[srcRow + x] - left);
                }
            }

            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var ihdr = new byte[13];
                WriteBE(ihdr, 0, (uint)img.Width);
                WriteBE(ihdr, 4, (uint)img.Height);
                ihdr[8] = 8;  // bit depth
                ihdr[9] = 2;  // RGB
                WriteChunk(ms, "IHDR", ihdr);
                WriteChunk(ms, "IDAT", Zlib(raw));
                WriteChunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        public static RgbImage Decode(byte[] data)
        {
            if (data.Length < 8 || data[0] != 0x89 || data[1] != 'P') throw new FormatException("Not a PNG file.");
            int pos = 8, width = 0, height = 0, colorType = 0;
            using (var idat = new MemoryStream())
            {
                while (pos + 8 <= data.Length)
                {
                    int len = (int)ReadBE(data, pos);
                    string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
                    int body = pos + 8;
                    if (type == "IHDR")
                    {
                        width = (int)ReadBE(data, body);
                        height = (int)ReadBE(data, body + 4);
                        if (data[body + 8] != 8) throw new NotSupportedException("Only 8-bit PNGs are supported.");
                        colorType = data[body + 9];
                        if (data[body + 12] != 0) throw new NotSupportedException("Interlaced PNGs are not supported.");
                    }
                    else if (type == "IDAT") idat.Write(data, body, len);
                    else if (type == "IEND") break;
                    pos = body + len + 4;
                }

                int channels = colorType == 0 ? 1 : colorType == 2 ? 3 : colorType == 4 ? 2 : colorType == 6 ? 4 : -1;
                if (channels < 0) throw new NotSupportedException($"PNG color type {colorType} is not supported.");
                int stride = width * channels;
                var raw = Inflate(idat.ToArray(), (stride + 1) * height);
                var cur = new byte[stride];
                var prev = new byte[stride];
                var img = new RgbImage(width, height);
                for (int y = 0; y < height; y++)
                {
                    int f = raw[y * (stride + 1)];
                    Buffer.BlockCopy(raw, y * (stride + 1) + 1, cur, 0, stride);
                    Unfilter(f, cur, prev, channels);
                    int dstRow = (height - 1 - y) * width * 3;
                    for (int x = 0; x < width; x++)
                    {
                        int s = x * channels;
                        byte r = cur[s], g = channels >= 3 ? cur[s + 1] : cur[s], b = channels >= 3 ? cur[s + 2] : cur[s];
                        img.Pixels[dstRow + x * 3] = r;
                        img.Pixels[dstRow + x * 3 + 1] = g;
                        img.Pixels[dstRow + x * 3 + 2] = b;
                    }
                    var t = prev; prev = cur; cur = t;
                }
                return img;
            }
        }

        static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
        {
            for (int i = 0; i < cur.Length; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0;
                int b = prev[i];
                int c = i >= bpp ? prev[i - bpp] : 0;
                int add;
                switch (filter)
                {
                    case 0: add = 0; break;
                    case 1: add = a; break;
                    case 2: add = b; break;
                    case 3: add = (a + b) >> 1; break;
                    case 4:
                    {
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        add = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                        break;
                    }
                    default: throw new FormatException($"Bad PNG filter {filter}.");
                }
                cur[i] = (byte)(cur[i] + add);
            }
        }

        static byte[] Zlib(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
                    deflate.Write(raw, 0, raw.Length);
                uint a = 1, b = 0;
                foreach (var x in raw)
                {
                    a = (a + x) % 65521;
                    b = (b + a) % 65521;
                }
                var adler = new byte[4];
                WriteBE(adler, 0, (b << 16) | a);
                ms.Write(adler, 0, 4);
                return ms.ToArray();
            }
        }

        static byte[] Inflate(byte[] zlib, int expected)
        {
            var result = new byte[expected];
            using (var ms = new MemoryStream(zlib, 2, zlib.Length - 2))
            using (var inflate = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < expected)
                {
                    int n = inflate.Read(result, read, expected - read);
                    if (n <= 0) throw new FormatException("PNG data ended early.");
                    read += n;
                }
            }
            return result;
        }

        static void WriteChunk(Stream s, string type, byte[] body)
        {
            var header = new byte[8];
            WriteBE(header, 0, (uint)body.Length);
            for (int i = 0; i < 4; i++) header[4 + i] = (byte)type[i];
            s.Write(header, 0, 8);
            s.Write(body, 0, body.Length);
            uint crc = 0xFFFFFFFF;
            for (int i = 4; i < 8; i++) crc = CrcTable[(crc ^ header[i]) & 0xFF] ^ (crc >> 8);
            foreach (var x in body) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            var c = new byte[4];
            WriteBE(c, 0, crc ^ 0xFFFFFFFF);
            s.Write(c, 0, 4);
        }

        static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        static void WriteBE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        static uint ReadBE(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    }
}
