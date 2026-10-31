using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Matterportable.Core
{
    /// <summary>Minimal protocol-buffers wire-format reader (no schema/codegen needed).</summary>
    public sealed class ProtoReader
    {
        public const int Varint = 0, Fixed64 = 1, LengthDelimited = 2, Fixed32 = 5;

        readonly byte[] _buf;
        readonly int _end;
        int _pos;

        public ProtoReader(byte[] buffer) : this(buffer, 0, buffer.Length) { }

        public ProtoReader(byte[] buffer, int offset, int length)
        {
            _buf = buffer ?? throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || length < 0 || offset + length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(length));
            _pos = offset;
            _end = offset + length;
        }

        public bool AtEnd => _pos >= _end;
        public int Position => _pos;
        public int Remaining => _end - _pos;

        /// <summary>An independent reader over the unread remainder.</summary>
        public ProtoReader Clone() => new ProtoReader(_buf, _pos, _end - _pos);

        public bool TryReadTag(out int field, out int wireType)
        {
            field = wireType = 0;
            if (_pos >= _end) return false;
            ulong tag = ReadVarint();
            field = (int)(tag >> 3);
            wireType = (int)(tag & 7);
            if (field <= 0) throw new FormatException($"Invalid protobuf field number {field} at offset {_pos}.");
            return true;
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (_pos >= _end) throw new FormatException("Truncated protobuf varint.");
                byte b = _buf[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new FormatException("Malformed protobuf varint.");
        }

        public static long ZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

        public uint ReadFixed32()
        {
            Need(4);
            uint v = (uint)(_buf[_pos] | _buf[_pos + 1] << 8 | _buf[_pos + 2] << 16 | _buf[_pos + 3] << 24);
            _pos += 4;
            return v;
        }

        public ulong ReadFixed64()
        {
            ulong lo = ReadFixed32();
            ulong hi = ReadFixed32();
            return lo | hi << 32;
        }

        public float ReadFloat()
        {
            Need(4);
            float f = BitConverter.ToSingle(_buf, _pos);
            _pos += 4;
            return f;
        }

        public double ReadDouble()
        {
            Need(8);
            double d = BitConverter.ToDouble(_buf, _pos);
            _pos += 8;
            return d;
        }

        public ProtoReader ReadMessage()
        {
            int len = ReadLength();
            var r = new ProtoReader(_buf, _pos, len);
            _pos += len;
            return r;
        }

        public string ReadString()
        {
            int len = ReadLength();
            var s = Encoding.UTF8.GetString(_buf, _pos, len);
            _pos += len;
            return s;
        }

        public void Skip(int wireType)
        {
            switch (wireType)
            {
                case Varint: ReadVarint(); break;
                case Fixed64: Need(8); _pos += 8; break;
                case LengthDelimited: { int len = ReadLength(); _pos += len; break; }
                case Fixed32: Need(4); _pos += 4; break;
                default: throw new FormatException($"Unsupported protobuf wire type {wireType} at offset {_pos}.");
            }
        }

        /// <summary>Reads a float field that may be packed (wire type 2) or a single value (wire type 5).</summary>
        public void ReadFloats(int wireType, List<float> into)
        {
            if (wireType == Fixed32) { into.Add(ReadFloat()); return; }
            if (wireType != LengthDelimited) { Skip(wireType); return; }
            int len = ReadLength();
            if (len % 4 != 0) throw new FormatException($"Packed float field length {len} is not a multiple of 4.");
            int end = _pos + len;
            if (into.Capacity < into.Count + len / 4) into.Capacity = into.Count + len / 4;
            while (_pos < end) into.Add(ReadFloat());
        }

        /// <summary>Reads a varint field that may be packed (wire type 2) or a single value (wire type 0).</summary>
        public void ReadVarints(int wireType, List<ulong> into)
        {
            if (wireType == Varint) { into.Add(ReadVarint()); return; }
            if (wireType != LengthDelimited) { Skip(wireType); return; }
            int len = ReadLength();
            int end = _pos + len;
            var inner = new ProtoReader(_buf, _pos, len);
            while (!inner.AtEnd) into.Add(inner.ReadVarint());
            _pos = end;
        }

        int ReadLength()
        {
            ulong len = ReadVarint();
            if (len > (ulong)(_end - _pos)) throw new FormatException($"Protobuf length {len} exceeds the remaining {_end - _pos} bytes.");
            return (int)len;
        }

        void Need(int n)
        {
            if (_end - _pos < n) throw new FormatException("Truncated protobuf data.");
        }
    }

    public sealed class ProtoWriter
    {
        readonly MemoryStream _ms = new MemoryStream();

        public void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

        public void Varint(ulong v)
        {
            while (v >= 0x80)
            {
                _ms.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            _ms.WriteByte((byte)v);
        }

        public static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));

        public void Float(float f)
        {
            var b = BitConverter.GetBytes(f);
            _ms.Write(b, 0, 4);
        }

        public void FloatField(int field, float value)
        {
            Tag(field, ProtoReader.Fixed32);
            Float(value);
        }

        public void PackedFloats(int field, IList<float> values)
        {
            Tag(field, ProtoReader.LengthDelimited);
            Varint((ulong)(values.Count * 4));
            foreach (var v in values) Float(v);
        }

        public void PackedVarints(int field, IEnumerable<ulong> values)
        {
            var inner = new ProtoWriter();
            foreach (var v in values) inner.Varint(v);
            Bytes(field, inner.ToArray());
        }

        public void String(int field, string s) => Bytes(field, Encoding.UTF8.GetBytes(s));

        public void Bytes(int field, byte[] data)
        {
            Tag(field, ProtoReader.LengthDelimited);
            Varint((ulong)data.Length);
            _ms.Write(data, 0, data.Length);
        }

        public void Message(int field, ProtoWriter sub) => Bytes(field, sub.ToArray());

        public byte[] ToArray() => _ms.ToArray();
    }

    /// <summary>Human-readable structure dump of unknown protobuf data, written next to failed decodes for diagnosis.</summary>
    public static class ProtoInspector
    {
        public static string Describe(byte[] data, int maxDepth = 4, int maxLines = 200)
        {
            var sb = new StringBuilder();
            int lines = 0;
            try { Walk(new ProtoReader(data), 0, maxDepth, sb, ref lines, maxLines); }
            catch (FormatException e) { sb.Append("<stopped: ").Append(e.Message).Append(">\n"); }
            return sb.ToString();
        }

        static void Walk(ProtoReader r, int depth, int maxDepth, StringBuilder sb, ref int lines, int maxLines)
        {
            int lastField = -1, repeat = 0;
            while (r.TryReadTag(out int field, out int wire))
            {
                if (lines >= maxLines) { sb.Append(' ', depth * 2).Append("…\n"); return; }
                if (field == lastField && repeat++ > 3) { r.Skip(wire); continue; }
                if (field != lastField) repeat = 0;
                lastField = field;
                sb.Append(' ', depth * 2).Append("field ").Append(field).Append(" wire ").Append(wire);
                lines++;
                if (wire == ProtoReader.LengthDelimited)
                {
                    var sub = r.ReadMessage();
                    sb.Append(" len ").Append(sub.Remaining);
                    if (depth < maxDepth && LooksLikeMessage(sub.Clone()))
                    {
                        sb.Append('\n');
                        Walk(sub, depth + 1, maxDepth, sb, ref lines, maxLines);
                        continue;
                    }
                }
                else r.Skip(wire);
                sb.Append('\n');
            }
        }

        static bool LooksLikeMessage(ProtoReader probe)
        {
            // Copy-free heuristic: try to parse the whole payload as fields.
            try
            {
                int fields = 0;
                while (probe.TryReadTag(out int f, out int w))
                {
                    if (f > 1000 || w == 3 || w == 4 || w > 5) return false;
                    probe.Skip(w);
                    fields++;
                }
                return fields > 0;
            }
            catch (FormatException) { return false; }
        }
    }
}
