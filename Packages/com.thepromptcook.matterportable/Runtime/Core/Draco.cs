using System;
using System.Collections.Generic;

namespace Matterportable.Core
{
    /// <summary>A decoded Draco mesh: triangle indices and float attributes by Draco unique id.</summary>
    public sealed class DracoMesh
    {
        public int PointCount;
        public int[] Indices;
        public readonly Dictionary<int, (float[] values, int components)> Attributes = new Dictionary<int, (float[], int)>();
    }

    /// <summary>
    /// Decoder for Draco-compressed triangle meshes (Google's format, used by Matterport's 3D tiles), bitstream 2.2:
    /// the sequential encoding with quantized, delta-predicted attributes and rANS-coded symbols. Other variants
    /// (edgebreaker, parallelogram prediction, normals) throw <see cref="NotSupportedException"/>.
    /// </summary>
    public static class Draco
    {
        public static DracoMesh Decode(byte[] data, int offset, int length)
        {
            var r = new Reader(data, offset, length);
            if (r.Byte() != 'D' || r.Byte() != 'R' || r.Byte() != 'A' || r.Byte() != 'C' || r.Byte() != 'O') throw new FormatException("Not a Draco stream.");
            int major = r.Byte(), minor = r.Byte(), encoder = r.Byte(), method = r.Byte(), flags = r.UInt16();
            if (major != 2 || minor < 2) throw new NotSupportedException($"Draco bitstream {major}.{minor}");
            if (encoder != 1) throw new NotSupportedException("Draco point clouds");
            if (method != 0) throw new NotSupportedException("Draco edgebreaker meshes (only sequential ones are decoded)");
            if ((flags & 0x8000) != 0) throw new NotSupportedException("Draco metadata");

            var mesh = new DracoMesh();
            int faces = (int)r.VarUInt(), points = mesh.PointCount = (int)r.VarUInt();
            var indices = mesh.Indices = new int[faces * 3];
            if (r.Byte() == 0) // compressed: zigzag deltas
            {
                var symbols = new uint[indices.Length];
                DecodeSymbols(ref r, symbols.Length, 1, symbols);
                for (int i = 0, last = 0; i < indices.Length; i++)
                {
                    int diff = (int)(symbols[i] >> 1);
                    indices[i] = last += (symbols[i] & 1) != 0 ? -diff : diff;
                }
            }
            else
                for (int i = 0; i < indices.Length; i++)
                    indices[i] = points < 256 ? r.Byte() : points < 1 << 16 ? r.UInt16() : points < 1 << 21 ? (int)r.VarUInt() : r.Int32();

            int decoders = r.Byte();
            var groups = new List<(int id, int components, int decoder)[]>();
            for (int d = 0; d < decoders; d++)
            {
                var atts = new (int id, int components, int decoder)[(int)r.VarUInt()];
                for (int j = 0; j < atts.Length; j++)
                {
                    r.Byte(); // attribute type (position, tex coord…): callers map by unique id
                    int dataType = r.Byte(), components = r.Byte();
                    r.Byte(); // normalized
                    atts[j] = ((int)r.VarUInt(), components, 0);
                    if (dataType != 9) throw new NotSupportedException($"Draco attribute data type {dataType}");
                }
                for (int j = 0; j < atts.Length; j++) atts[j].decoder = r.Byte();
                groups.Add(atts);
            }
            foreach (var atts in groups)
            {
                // All values of the group first, then the data each attribute's transform needs.
                var values = new int[atts.Length][];
                for (int j = 0; j < atts.Length; j++)
                {
                    if (atts[j].decoder != 2) throw new NotSupportedException($"Draco attribute decoder {atts[j].decoder} (only quantized attributes)");
                    values[j] = DecodeValues(ref r, points, atts[j].components);
                }
                for (int j = 0; j < atts.Length; j++)
                {
                    int c = atts[j].components;
                    var min = new float[c];
                    for (int k = 0; k < c; k++) min[k] = r.Float();
                    float range = r.Float();
                    int bits = r.Byte();
                    float delta = range / ((1 << bits) - 1);
                    var floats = new float[values[j].Length];
                    for (int i = 0; i < floats.Length; i++) floats[i] = values[j][i] * delta + min[i % c];
                    mesh.Attributes[atts[j].id] = (floats, c);
                }
            }
            return mesh;
        }

        /// <summary>Integer values of one attribute: symbols, back to signed, then the inverse delta prediction
        /// (each point predicted from the previous one, wrapped into the encoder's value range).</summary>
        static int[] DecodeValues(ref Reader r, int points, int components)
        {
            int method = (sbyte)r.Byte(), transform = method != -2 ? (sbyte)r.Byte() : -1;
            if (method != -2 && (method != 0 || transform != 1)) throw new NotSupportedException($"Draco prediction scheme {method} with transform {transform}");
            var values = new int[points * components];
            if (r.Byte() != 0)
            {
                var symbols = new uint[values.Length];
                DecodeSymbols(ref r, values.Length, components, symbols);
                for (int i = 0; i < values.Length; i++) values[i] = (int)symbols[i];
            }
            else
            {
                int bytes = r.Byte();
                for (int i = 0; i < values.Length; i++)
                {
                    int v = 0;
                    for (int b = 0; b < bytes; b++) v |= r.Byte() << (8 * b);
                    values[i] = v;
                }
            }
            for (int i = 0; i < values.Length; i++)
            {
                uint u = (uint)values[i];
                values[i] = (u & 1) == 0 ? (int)(u >> 1) : -(int)(u >> 1) - 1;
            }
            if (method == -2) return values;
            int lo = r.Int32(), hi = r.Int32(), span = hi - lo + 1;
            for (int i = 0; i < values.Length; i += components)
                for (int c = 0; c < components; c++)
                {
                    int predicted = i == 0 ? 0 : values[i - components + c];
                    predicted = predicted < lo ? lo : predicted > hi ? hi : predicted;
                    int v = predicted + values[i + c];
                    values[i + c] = v > hi ? v - span : v < lo ? v + span : v;
                }
            return values;
        }

        static void DecodeSymbols(ref Reader r, int count, int components, uint[] output)
        {
            if (count == 0) return;
            int scheme = r.Byte();
            if (scheme == 0) // tagged: an rANS-coded bit length per value tuple, then the raw bits
            {
                var tags = new Rans(ref r, 5);
                var bits = new BitReader(r.Data, r.Position, r.End);
                for (int i = 0; i < count; i += components)
                {
                    int length = (int)tags.Read();
                    for (int c = 0; c < components; c++) output[i + c] = bits.Read(length);
                }
                r.Position = bits.BytePosition;
            }
            else if (scheme == 1)
            {
                var rans = new Rans(ref r, r.Byte());
                for (int i = 0; i < count; i++) output[i] = rans.Read();
            }
            else throw new FormatException($"Draco symbol coding {scheme}");
        }

        /// <summary>rANS symbol decoder: a run-length-coded probability table, then the coded bytes (read backwards).</summary>
        struct Rans
        {
            readonly byte[] _data;
            readonly int _precision, _start;
            readonly uint[] _prob, _cumulative;
            readonly int[] _lookup;
            int _offset;
            uint _state;

            public Rans(ref Reader r, int symbolBits)
            {
                int unclamped = 3 * symbolBits / 2;
                _precision = 1 << (unclamped < 12 ? 12 : unclamped > 20 ? 20 : unclamped);
                int n = (int)r.VarUInt();
                _prob = new uint[n];
                _cumulative = new uint[n];
                for (int i = 0; i < n; i++)
                {
                    int head = r.Byte(), token = head & 3;
                    if (token == 3) { i += head >> 2; continue; } // a run of zero probabilities
                    uint p = (uint)(head >> 2);
                    for (int b = 0; b < token; b++) p |= (uint)r.Byte() << (8 * (b + 1) - 2);
                    _prob[i] = p;
                }
                _lookup = new int[_precision];
                uint total = 0;
                for (int i = 0; i < n; i++)
                {
                    _cumulative[i] = total;
                    if (total + _prob[i] > _precision) throw new FormatException("Bad Draco probability table.");
                    for (uint j = total; j < total + _prob[i]; j++) _lookup[j] = i;
                    total += _prob[i];
                }
                if (n > 0 && total != _precision) throw new FormatException("Bad Draco probability table.");
                int bytes = (int)r.VarUInt();
                _data = r.Data;
                _start = r.Position;
                r.Position += bytes;
                if (bytes < 1) throw new FormatException("Empty Draco rANS data.");
                int x = _data[_start + bytes - 1] >> 6;
                _offset = bytes - 1 - x;
                _state = 0;
                for (int k = x; k >= 0; k--) _state = _state << 8 | _data[_start + _offset + k];
                _state &= (1u << (6 + 8 * x)) - 1;
                _state += (uint)(_precision * 4);
            }

            public uint Read()
            {
                uint baseState = (uint)(_precision * 4);
                while (_state < baseState && _offset > 0) _state = _state * 256 + _data[_start + --_offset];
                uint quotient = _state / (uint)_precision, remainder = _state % (uint)_precision;
                int symbol = _lookup[remainder];
                _state = quotient * _prob[symbol] + remainder - _cumulative[symbol];
                return (uint)symbol;
            }
        }

        struct BitReader
        {
            readonly byte[] _data;
            readonly int _start, _end;
            long _bit;

            public BitReader(byte[] data, int start, int end) { _data = data; _start = start; _end = end; _bit = 0; }
            public int BytePosition => _start + (int)((_bit + 7) / 8);

            public uint Read(int count)
            {
                uint v = 0;
                for (int i = 0; i < count; i++, _bit++)
                {
                    int at = _start + (int)(_bit >> 3);
                    if (at < _end) v |= (uint)((_data[at] >> (int)(_bit & 7)) & 1) << i;
                }
                return v;
            }
        }

        struct Reader
        {
            public readonly byte[] Data;
            public readonly int End;
            public int Position;

            public Reader(byte[] data, int offset, int length) { Data = data; Position = offset; End = offset + length; }

            public int Byte()
            {
                if (Position >= End) throw new FormatException("Truncated Draco stream.");
                return Data[Position++];
            }

            public int UInt16() => Byte() | Byte() << 8;
            public int Int32() => Byte() | Byte() << 8 | Byte() << 16 | Byte() << 24;
            public float Float() => BitConverter.Int32BitsToSingle(Int32());

            public ulong VarUInt()
            {
                ulong v = 0;
                for (int shift = 0; ; shift += 7)
                {
                    int b = Byte();
                    v |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) return v;
                }
            }
        }
    }
}
