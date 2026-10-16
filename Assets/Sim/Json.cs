using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Battalion.Sim
{
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// 最小限の JSON 値。パラメータの読み込み専用。外部ライブラリに依存させないために自前で持つ。
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind { get; private set; }
        public bool Bool { get; private set; }
        public double Number { get; private set; }
        public string String { get; private set; }
        public List<JsonValue> Array { get; private set; }
        /// <summary>キーの出現順を保つ。</summary>
        public List<KeyValuePair<string, JsonValue>> Object { get; private set; }

        public static readonly JsonValue NullValue = new JsonValue { Kind = JsonKind.Null };

        public bool Has(string key) => TryGet(key, out _);

        public bool TryGet(string key, out JsonValue value)
        {
            if (Kind == JsonKind.Object)
            {
                foreach (var kv in Object)
                {
                    if (kv.Key == key)
                    {
                        value = kv.Value;
                        return true;
                    }
                }
            }
            value = null;
            return false;
        }

        public JsonValue this[string key]
        {
            get
            {
                if (!TryGet(key, out var v))
                    throw new KeyNotFoundException($"JSON にキー '{key}' がない");
                return v;
            }
        }

        public double D(string key)
        {
            var v = this[key];
            if (v.Kind != JsonKind.Number) throw new FormatException($"'{key}' は数値ではない");
            return v.Number;
        }

        public static JsonValue Parse(string text)
        {
            var p = new Parser(text);
            p.SkipWs();
            var v = p.ParseValue();
            p.SkipWs();
            if (!p.AtEnd) throw p.Error("値の後に余分な文字がある");
            return v;
        }

        sealed class Parser
        {
            readonly string _s;
            int _i;

            public Parser(string s)
            {
                _s = s;
                _i = 0;
            }

            public bool AtEnd => _i >= _s.Length;

            public FormatException Error(string msg) => new FormatException($"JSON の解析に失敗（位置 {_i}）：{msg}");

            public void SkipWs()
            {
                while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r' || _s[_i] == '﻿'))
                    _i++;
            }

            char Peek() => _i < _s.Length ? _s[_i] : '\0';

            void Expect(char c)
            {
                if (Peek() != c) throw Error($"'{c}' が必要");
                _i++;
            }

            public JsonValue ParseValue()
            {
                char c = Peek();
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return new JsonValue { Kind = JsonKind.String, String = ParseString() };
                    case 't': ExpectWord("true"); return new JsonValue { Kind = JsonKind.Bool, Bool = true };
                    case 'f': ExpectWord("false"); return new JsonValue { Kind = JsonKind.Bool, Bool = false };
                    case 'n': ExpectWord("null"); return NullValue;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                        throw Error($"予期しない文字 '{c}'");
                }
            }

            void ExpectWord(string w)
            {
                if (string.CompareOrdinal(_s, _i, w, 0, w.Length) != 0) throw Error($"'{w}' が必要");
                _i += w.Length;
            }

            JsonValue ParseObject()
            {
                Expect('{');
                var list = new List<KeyValuePair<string, JsonValue>>();
                SkipWs();
                if (Peek() == '}')
                {
                    _i++;
                    return new JsonValue { Kind = JsonKind.Object, Object = list };
                }
                while (true)
                {
                    SkipWs();
                    string key = ParseString();
                    SkipWs();
                    Expect(':');
                    SkipWs();
                    var v = ParseValue();
                    list.Add(new KeyValuePair<string, JsonValue>(key, v));
                    SkipWs();
                    if (Peek() == ',') { _i++; continue; }
                    Expect('}');
                    return new JsonValue { Kind = JsonKind.Object, Object = list };
                }
            }

            JsonValue ParseArray()
            {
                Expect('[');
                var list = new List<JsonValue>();
                SkipWs();
                if (Peek() == ']')
                {
                    _i++;
                    return new JsonValue { Kind = JsonKind.Array, Array = list };
                }
                while (true)
                {
                    SkipWs();
                    list.Add(ParseValue());
                    SkipWs();
                    if (Peek() == ',') { _i++; continue; }
                    Expect(']');
                    return new JsonValue { Kind = JsonKind.Array, Array = list };
                }
            }

            string ParseString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw Error("文字列が閉じていない");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
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
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw Error($"不正なエスケープ '\\{e}'");
                    }
                }
            }

            JsonValue ParseNumber()
            {
                int start = _i;
                if (Peek() == '-') _i++;
                while (_i < _s.Length && "0123456789.eE+-".IndexOf(_s[_i]) >= 0) _i++;
                string tok = _s.Substring(start, _i - start);
                if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    throw Error($"不正な数値 '{tok}'");
                return new JsonValue { Kind = JsonKind.Number, Number = d };
            }
        }
    }

    /// <summary>
    /// 1行ぶんの JSON オブジェクトを組み立てる。数値の書式は InvariantCulture・往復可能な表記に固定し、
    /// 同じ入力からは同じバイト列が出るようにする（ログの SHA256 比較のため）。
    /// </summary>
    public sealed class JsonLineBuilder
    {
        readonly StringBuilder _sb = new StringBuilder(256);
        bool _first = true;

        public JsonLineBuilder()
        {
            _sb.Append('{');
        }

        void Key(string key)
        {
            if (!_first) _sb.Append(',');
            _first = false;
            AppendString(_sb, key);
            _sb.Append(':');
        }

        public JsonLineBuilder Add(string key, string value)
        {
            Key(key);
            if (value == null) _sb.Append("null");
            else AppendString(_sb, value);
            return this;
        }

        public JsonLineBuilder Add(string key, double value)
        {
            Key(key);
            AppendNumber(_sb, value);
            return this;
        }

        public JsonLineBuilder Add(string key, int value)
        {
            Key(key);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonLineBuilder Add(string key, bool value)
        {
            Key(key);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        /// <summary>入れ子のオブジェクトを追加する。</summary>
        public JsonLineBuilder Add(string key, JsonLineBuilder nested)
        {
            Key(key);
            _sb.Append(nested.ToString());
            return this;
        }

        public JsonLineBuilder AddStringArray(string key, IEnumerable<string> values)
        {
            Key(key);
            _sb.Append('[');
            bool first = true;
            foreach (var v in values)
            {
                if (!first) _sb.Append(',');
                first = false;
                AppendString(_sb, v);
            }
            _sb.Append(']');
            return this;
        }

        public override string ToString() => _sb.ToString() + "}";

        public static void AppendNumber(StringBuilder sb, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                sb.Append("null");
                return;
            }
            // 表示とハッシュを安定させるため、小数第6位で丸める
            double r = Math.Round(value, 6, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0; // -0 を 0 にそろえる
            sb.Append(r.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void AppendString(StringBuilder sb, string s)
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
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
