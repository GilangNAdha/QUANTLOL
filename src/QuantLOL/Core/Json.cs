using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace QuantLOL.Core
{
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// Minimal, dependency-free JSON reader for externally supplied level files.
    /// Deliberately strict: depth/size limited, no implicit conversions, no culture
    /// dependent number parsing. Keeps the cBot free of third-party assemblies.
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind { get; private set; }
        public double Number { get; private set; }
        public bool Bool { get; private set; }
        public string Text { get; private set; }
        public List<JsonValue> Array { get; private set; }
        public Dictionary<string, JsonValue> Object { get; private set; }

        public bool IsNull => Kind == JsonKind.Null;

        public JsonValue Get(string key)
        {
            if (Kind != JsonKind.Object || Object == null) return null;
            return Object.TryGetValue(key, out var v) ? v : null;
        }

        public IEnumerable<KeyValuePair<string, JsonValue>> Members
            => Object ?? (IEnumerable<KeyValuePair<string, JsonValue>>)Array.Empty<KeyValuePair<string, JsonValue>>();

        public double? Num(string key)
        {
            var v = Get(key);
            if (v == null || v.Kind != JsonKind.Number) return null;
            return v.Number;
        }

        public string Str(string key)
        {
            var v = Get(key);
            return v != null && v.Kind == JsonKind.String ? v.Text : null;
        }

        public bool? Flag(string key)
        {
            var v = Get(key);
            return v != null && v.Kind == JsonKind.Bool ? v.Bool : (bool?)null;
        }

        public static JsonValue Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new JsonException("empty document");
            if (text.Length > 4_000_000) throw new JsonException("document too large");
            int i = 0;
            var v = ParseValue(text, ref i, 0);
            SkipWs(text, ref i);
            if (i < text.Length) throw new JsonException("trailing content at " + i);
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
                break;
            }
        }

        static JsonValue ParseValue(string s, ref int i, int depth)
        {
            if (depth > 32) throw new JsonException("nesting too deep");
            SkipWs(s, ref i);
            if (i >= s.Length) throw new JsonException("unexpected end");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i, depth);
                case '[': return ParseArray(s, ref i, depth);
                case '"': return new JsonValue { Kind = JsonKind.String, Text = ParseString(s, ref i) };
                case 't': Expect(s, ref i, "true"); return new JsonValue { Kind = JsonKind.Bool, Bool = true };
                case 'f': Expect(s, ref i, "false"); return new JsonValue { Kind = JsonKind.Bool, Bool = false };
                case 'n': Expect(s, ref i, "null"); return new JsonValue { Kind = JsonKind.Null };
                default: return ParseNumber(s, ref i);
            }
        }

        static void Expect(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0)
                throw new JsonException("expected '" + lit + "' at " + i);
            i += lit.Length;
        }

        static JsonValue ParseObject(string s, ref int i, int depth)
        {
            var o = new Dictionary<string, JsonValue>(StringComparer.OrdinalIgnoreCase);
            i++; SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return new JsonValue { Kind = JsonKind.Object, Object = o }; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new JsonException("expected key at " + i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new JsonException("expected ':' at " + i);
                i++;
                o[key] = ParseValue(s, ref i, depth + 1);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new JsonException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; break; }
                throw new JsonException("expected ',' or '}' at " + i);
            }
            return new JsonValue { Kind = JsonKind.Object, Object = o };
        }

        static JsonValue ParseArray(string s, ref int i, int depth)
        {
            var a = new List<JsonValue>();
            i++; SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return new JsonValue { Kind = JsonKind.Array, Array = a }; }
            while (true)
            {
                a.Add(ParseValue(s, ref i, depth + 1));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new JsonException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; break; }
                throw new JsonException("expected ',' or ']' at " + i);
            }
            if (a.Count > 10000) throw new JsonException("array too large");
            return new JsonValue { Kind = JsonKind.Array, Array = a };
        }

        static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) break;
                    char e = s[i++];
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
                            if (i + 4 > s.Length) throw new JsonException("bad unicode escape");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4; break;
                        default: throw new JsonException("bad escape \\" + e);
                    }
                }
                else if (c < ' ') throw new JsonException("control char in string");
                else sb.Append(c);
            }
            throw new JsonException("unterminated string");
        }

        static JsonValue ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
            if (i == start) throw new JsonException("invalid value at " + start);
            var span = s.Substring(start, i - start);
            if (!double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw new JsonException("bad number '" + span + "'");
            return new JsonValue { Kind = JsonKind.Number, Number = d };
        }
    }

    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }
}
