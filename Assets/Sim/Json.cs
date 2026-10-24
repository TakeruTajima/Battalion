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
                if (!JsonNumber.TryParse(tok, out double d))
                    throw Error($"不正な数値 '{tok}'");
                return new JsonValue { Kind = JsonKind.Number, Number = d };
            }
        }
    }

    /// <summary>
    /// JSON の数の読み書き（V1 タスク0）。実行環境（.NET と Unity の Mono）によらず同じビット列・同じ文字になるようにする。
    /// Mono の <c>double.Parse</c> は正しく丸めないことがあり、<c>ToString("R")</c> は往復できる値でも 17桁で書くことがあるため。
    /// </summary>
    public static class JsonNumber
    {
        // 10⁰〜10²²：double で正確に表せる
        static readonly double[] Pow10 =
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
            1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22,
        };

        /// <summary>
        /// 正しく丸める読み方で読めなかった（実行環境の <c>double.Parse</c> に任せた）数の数（テストで、設定や地図の数が
        /// 全て正しく丸める読み方で読めることを確かめるため）。
        /// </summary>
        public static int FallbackCount;

        /// <summary>
        /// 数を読む。有効数字 15桁以下で、10 の指数が ±22 以内なら「整数 × 10ⁿ」か「整数 ÷ 10ⁿ」の1回の演算で読む
        /// （整数も 10ⁿ も double で正確に表せるので、結果は正しく丸まり、実行環境によらない）。それ以外は <c>double.Parse</c>。
        /// </summary>
        public static bool TryParse(string tok, out double value)
        {
            if (TryParseExact(tok, out value)) return true;
            FallbackCount++;
            return double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>正しく丸める読み方（上の1回の演算）で読めれば true。</summary>
        public static bool TryParseExact(string tok, out double value)
        {
            value = 0;
            int i = 0, n = tok.Length;
            bool neg = false;
            if (i < n && (tok[i] == '-' || tok[i] == '+')) neg = tok[i++] == '-';
            long mant = 0;
            int digits = 0, exp10 = 0;
            bool any = false, dot = false;
            for (; i < n; i++)
            {
                char c = tok[i];
                if (c == '.')
                {
                    if (dot) return false;
                    dot = true;
                    continue;
                }
                if (c < '0' || c > '9') break;
                any = true;
                if (mant == 0 && c == '0')
                {
                    if (dot) exp10--; // 小数点の後の先頭の 0
                    continue;
                }
                if (digits >= 15)
                {
                    // 16桁目以降は 0 だけ許す（末尾の 0）
                    if (c != '0') return false;
                    if (!dot) exp10++;
                    continue;
                }
                mant = mant * 10 + (c - '0');
                digits++;
                if (dot) exp10--;
            }
            if (!any) return false;
            if (i < n)
            {
                if (tok[i] != 'e' && tok[i] != 'E') return false;
                i++;
                bool eneg = false;
                if (i < n && (tok[i] == '-' || tok[i] == '+')) eneg = tok[i++] == '-';
                int e = 0;
                bool edig = false;
                for (; i < n; i++)
                {
                    char c = tok[i];
                    if (c < '0' || c > '9') return false;
                    edig = true;
                    if (e > 10000) return false;
                    e = e * 10 + (c - '0');
                }
                if (!edig) return false;
                exp10 += eneg ? -e : e;
            }
            if (mant == 0)
            {
                value = neg ? -0.0 : 0.0;
                return true;
            }
            // 末尾の 0 を指数へ寄せる（「1500」は 15×10²）
            while (mant % 10 == 0)
            {
                mant /= 10;
                exp10++;
            }
            if (exp10 < -22 || exp10 > 22) return false;
            double m = mant;
            double v = exp10 >= 0 ? m * Pow10[exp10] : m / Pow10[-exp10];
            value = neg ? -v : v;
            return true;
        }

        /// <summary>
        /// ログに数を書く：小数第6位で丸め（0 から遠い方へ）、.NET の <c>ToString("R")</c> と同じ文字を、実行環境によらずに書く。
        /// 丸めた値を 100万倍した整数から「整数部.小数部（末尾の 0 を落とす）」で書く。0.0001 未満は「1.2E-05」の形。
        /// 10億以上（ログには出ない）は <c>ToString("R")</c> に任せる。
        /// </summary>
        public static void Append(StringBuilder sb, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                sb.Append("null");
                return;
            }
            double scaled = Math.Round(value * 1e6, MidpointRounding.AwayFromZero);
            if (scaled == 0)
            {
                sb.Append('0'); // -0 も 0
                return;
            }
            if (Math.Abs(scaled) >= 1e15)
            {
                sb.Append((scaled / 1e6).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            long m = (long)scaled;
            if (m < 0)
            {
                sb.Append('-');
                m = -m;
            }
            if (m < 100)
            {
                // 0.0001 未満：.NET の "R" と同じ指数の形（1.2E-05、5E-06）
                int zeros = 0;
                while (m % 10 == 0)
                {
                    m /= 10;
                    zeros++;
                }
                string ds = m.ToString(CultureInfo.InvariantCulture);
                int e = ds.Length - 1 + zeros - 6;
                sb.Append(ds[0]);
                if (ds.Length > 1) sb.Append('.').Append(ds, 1, ds.Length - 1);
                sb.Append('E').Append(e < 0 ? '-' : '+').Append(Math.Abs(e).ToString("00", CultureInfo.InvariantCulture));
                return;
            }
            long ip = m / 1000000, fp = m % 1000000;
            sb.Append(ip.ToString(CultureInfo.InvariantCulture));
            if (fp == 0) return;
            string f = fp.ToString("000000", CultureInfo.InvariantCulture).TrimEnd('0');
            sb.Append('.').Append(f);
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
            // 表示とハッシュを安定させるため、小数第6位で丸め、実行環境によらない書き方で書く（V1 タスク0）
            JsonNumber.Append(sb, value);
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
