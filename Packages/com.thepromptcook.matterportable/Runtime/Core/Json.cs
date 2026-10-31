using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Matterportable.Core
{
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// Small JSON DOM used for GraphQL responses and the on-disk cache format.
    /// Lookups on missing members return <see cref="Null"/> so they can be chained safely:
    /// <c>json["data"]["model"]["name"].AsString()</c>.
    /// </summary>
    public sealed class JsonValue
    {
        public static readonly JsonValue Null = new JsonValue(JsonKind.Null);
        static readonly JsonValue True = new JsonValue(true);
        static readonly JsonValue False = new JsonValue(false);

        public JsonKind Kind { get; }

        readonly bool _bool;
        readonly double _number;
        readonly string _string;
        readonly List<JsonValue> _array;
        readonly Dictionary<string, JsonValue> _members;
        readonly List<string> _keys;

        JsonValue(JsonKind kind)
        {
            Kind = kind;
            if (kind == JsonKind.Array) _array = new List<JsonValue>();
            if (kind == JsonKind.Object)
            {
                _members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                _keys = new List<string>();
            }
        }

        JsonValue(bool b) { Kind = JsonKind.Bool; _bool = b; }
        JsonValue(double d) { Kind = JsonKind.Number; _number = d; }
        JsonValue(string s) { Kind = JsonKind.String; _string = s; }

        public static JsonValue NewObject() => new JsonValue(JsonKind.Object);
        public static JsonValue NewArray() => new JsonValue(JsonKind.Array);
        public static JsonValue From(bool b) => b ? True : False;
        public static JsonValue From(double d) => new JsonValue(d);
        public static JsonValue From(int i) => new JsonValue(i);
        public static JsonValue From(long l) => new JsonValue(l);

        /// <summary>Stores the shortest round-trippable form of a float so files stay readable.</summary>
        public static JsonValue From(float f) =>
            new JsonValue(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));

        public static JsonValue From(string s) => s == null ? Null : new JsonValue(s);

        public static JsonValue FromArray(IEnumerable<double> values)
        {
            var a = NewArray();
            if (values != null) foreach (var v in values) a.Add(From(v));
            return a;
        }

        public static JsonValue FromArray(IEnumerable<string> values)
        {
            var a = NewArray();
            if (values != null) foreach (var v in values) a.Add(From(v));
            return a;
        }

        public bool IsNull => Kind == JsonKind.Null;
        public bool IsObject => Kind == JsonKind.Object;
        public bool IsArray => Kind == JsonKind.Array;
        public bool IsString => Kind == JsonKind.String;
        public bool IsNumber => Kind == JsonKind.Number;

        public int Count => Kind == JsonKind.Array ? _array.Count : Kind == JsonKind.Object ? _keys.Count : 0;
        public IReadOnlyList<string> Keys => _keys ?? (IReadOnlyList<string>)Array.Empty<string>();
        public IReadOnlyList<JsonValue> Items => _array ?? (IReadOnlyList<JsonValue>)Array.Empty<JsonValue>();

        public JsonValue this[string key]
        {
            get
            {
                if (Kind != JsonKind.Object || key == null) return Null;
                return _members.TryGetValue(key, out var v) ? v : Null;
            }
        }

        public JsonValue this[int index]
        {
            get
            {
                if (Kind != JsonKind.Array || index < 0 || index >= _array.Count) return Null;
                return _array[index];
            }
        }

        public bool Has(string key) => Kind == JsonKind.Object && _members.ContainsKey(key);

        public JsonValue Set(string key, JsonValue value)
        {
            if (Kind != JsonKind.Object) throw new InvalidOperationException("Set() requires a JSON object.");
            if (!_members.ContainsKey(key)) _keys.Add(key);
            _members[key] = value ?? Null;
            return this;
        }

        public JsonValue Set(string key, string value) => Set(key, From(value));
        public JsonValue Set(string key, double value) => Set(key, From(value));
        public JsonValue Set(string key, float value) => Set(key, From(value));
        public JsonValue Set(string key, int value) => Set(key, From(value));
        public JsonValue Set(string key, bool value) => Set(key, From(value));

        public JsonValue Add(JsonValue value)
        {
            if (Kind != JsonKind.Array) throw new InvalidOperationException("Add() requires a JSON array.");
            _array.Add(value ?? Null);
            return this;
        }

        public string AsString(string fallback = null)
        {
            switch (Kind)
            {
                case JsonKind.String: return _string;
                case JsonKind.Number: return _number.ToString("R", CultureInfo.InvariantCulture);
                case JsonKind.Bool: return _bool ? "true" : "false";
                default: return fallback;
            }
        }

        public double AsDouble(double fallback = 0)
        {
            if (Kind == JsonKind.Number) return _number;
            if (Kind == JsonKind.String &&
                double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return fallback;
        }

        public int AsInt(int fallback = 0)
        {
            if (Kind == JsonKind.Number) return (int)Math.Round(_number);
            if (Kind == JsonKind.String &&
                int.TryParse(_string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
            return fallback;
        }

        public bool AsBool(bool fallback = false)
        {
            if (Kind == JsonKind.Bool) return _bool;
            if (Kind == JsonKind.String) return string.Equals(_string, "true", StringComparison.OrdinalIgnoreCase);
            if (Kind == JsonKind.Number) return Math.Abs(_number) > 0;
            return fallback;
        }

        /// <summary>Reads an array of numbers (e.g. [x,y,z]); returns null if this is not a numeric array.</summary>
        public double[] AsDoubleArray()
        {
            if (Kind != JsonKind.Array) return null;
            var r = new double[_array.Count];
            for (int i = 0; i < r.Length; i++)
            {
                if (!_array[i].IsNumber && !_array[i].IsString) return null;
                r[i] = _array[i].AsDouble();
            }
            return r;
        }

        public List<string> AsStringList()
        {
            var r = new List<string>();
            if (Kind != JsonKind.Array) return r;
            foreach (var v in _array)
            {
                var s = v.IsObject ? (v["id"].AsString() ?? v["sweep"].AsString()) : v.AsString();
                if (!string.IsNullOrEmpty(s)) r.Add(s);
            }
            return r;
        }

        public override string ToString() => ToJson(false);

        public string ToJson(bool pretty = true)
        {
            var sb = new StringBuilder();
            Write(sb, pretty, 0);
            return sb.ToString();
        }

        void Write(StringBuilder sb, bool pretty, int depth)
        {
            switch (Kind)
            {
                case JsonKind.Null: sb.Append("null"); break;
                case JsonKind.Bool: sb.Append(_bool ? "true" : "false"); break;
                case JsonKind.Number:
                    if (double.IsNaN(_number) || double.IsInfinity(_number)) sb.Append("null");
                    else sb.Append(_number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonKind.String: WriteString(sb, _string); break;
                case JsonKind.Array:
                {
                    if (_array.Count == 0) { sb.Append("[]"); break; }
                    bool inline = true;
                    foreach (var v in _array)
                        if (v.Kind == JsonKind.Object || v.Kind == JsonKind.Array) { inline = false; break; }
                    sb.Append('[');
                    for (int i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) sb.Append(pretty && inline ? ", " : ",");
                        if (pretty && !inline) NewLine(sb, depth + 1);
                        _array[i].Write(sb, pretty, depth + 1);
                    }
                    if (pretty && !inline) NewLine(sb, depth);
                    sb.Append(']');
                    break;
                }
                case JsonKind.Object:
                {
                    if (_keys.Count == 0) { sb.Append("{}"); break; }
                    sb.Append('{');
                    for (int i = 0; i < _keys.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (pretty) NewLine(sb, depth + 1);
                        WriteString(sb, _keys[i]);
                        sb.Append(pretty ? ": " : ":");
                        _members[_keys[i]].Write(sb, pretty, depth + 1);
                    }
                    if (pretty) NewLine(sb, depth);
                    sb.Append('}');
                    break;
                }
            }
        }

        static void NewLine(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            WriteString(sb, s);
            return sb.ToString();
        }

        public static JsonValue Parse(string text) => new JsonParser(text).ParseDocument();

        public static bool TryParse(string text, out JsonValue value, out string error)
        {
            try
            {
                value = Parse(text);
                error = null;
                return true;
            }
            catch (FormatException e)
            {
                value = Null;
                error = e.Message;
                return false;
            }
        }

        sealed class JsonParser
        {
            readonly string _s;
            int _i;

            public JsonParser(string s) { _s = s ?? throw new ArgumentNullException(nameof(s)); }

            public JsonValue ParseDocument()
            {
                SkipWs();
                // Tolerate a UTF-8 BOM decoded as U+FEFF.
                if (_i < _s.Length && _s[_i] == '﻿') { _i++; SkipWs(); }
                var v = ParseValue(0);
                SkipWs();
                if (_i != _s.Length) throw Error("Unexpected trailing characters");
                return v;
            }

            JsonValue ParseValue(int depth)
            {
                if (depth > 512) throw Error("JSON nested too deeply");
                SkipWs();
                if (_i >= _s.Length) throw Error("Unexpected end of JSON");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ParseObject(depth);
                    case '[': return ParseArray(depth);
                    case '"': return new JsonValue(ParseString());
                    case 't': Expect("true"); return True;
                    case 'f': Expect("false"); return False;
                    case 'n': Expect("null"); return Null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                        throw Error($"Unexpected character '{c}'");
                }
            }

            JsonValue ParseObject(int depth)
            {
                var obj = NewObject();
                _i++; // {
                SkipWs();
                if (Peek() == '}') { _i++; return obj; }
                while (true)
                {
                    SkipWs();
                    if (Peek() != '"') throw Error("Expected property name");
                    var key = ParseString();
                    SkipWs();
                    if (Peek() != ':') throw Error("Expected ':'");
                    _i++;
                    obj.Set(key, ParseValue(depth + 1));
                    SkipWs();
                    char c = Peek();
                    _i++;
                    if (c == ',') continue;
                    if (c == '}') return obj;
                    throw Error("Expected ',' or '}'");
                }
            }

            JsonValue ParseArray(int depth)
            {
                var arr = NewArray();
                _i++; // [
                SkipWs();
                if (Peek() == ']') { _i++; return arr; }
                while (true)
                {
                    arr.Add(ParseValue(depth + 1));
                    SkipWs();
                    char c = Peek();
                    _i++;
                    if (c == ',') continue;
                    if (c == ']') return arr;
                    throw Error("Expected ',' or ']'");
                }
            }

            string ParseString()
            {
                _i++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw Error("Unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (_i >= _s.Length) throw Error("Unterminated escape");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("Bad unicode escape");
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw Error($"Bad escape '\\{e}'");
                    }
                }
            }

            JsonValue ParseNumber()
            {
                int start = _i;
                if (Peek() == '-') _i++;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') _i++;
                    else break;
                }
                var token = _s.Substring(start, _i - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw Error($"Bad number '{token}'");
                return new JsonValue(d);
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error($"Expected '{word}'");
                _i += word.Length;
            }

            char Peek() => _i < _s.Length ? _s[_i] : '\0';

            void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                    else break;
                }
            }

            FormatException Error(string message)
            {
                int line = 1, col = 1;
                for (int k = 0; k < _i && k < _s.Length; k++)
                {
                    if (_s[k] == '\n') { line++; col = 1; }
                    else col++;
                }
                return new FormatException($"{message} at line {line}, column {col}.");
            }
        }
    }
}
