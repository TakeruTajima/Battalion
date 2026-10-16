using System.Collections.Generic;
using Battalion.Sim;

namespace Battalion.View
{
    /// <summary>
    /// Sim のイベントログ（JSON Lines）を受け取り、画面下部のログに出す主な出来事を拾う。
    /// 会戦中に一度でも壊走した中隊と、陣地の完成も数える。UnityEngine に依存しない。
    /// </summary>
    public sealed class EventFeed : IEventSink
    {
        public readonly struct Entry
        {
            public readonly double TimeSec;
            public readonly string Text;
            /// <summary>読み手の注意を引く出来事（壊走・降伏・包囲・逆襲）か。</summary>
            public readonly bool Important;

            public Entry(double t, string text, bool important)
            {
                TimeSec = t;
                Text = text;
                Important = important;
            }
        }

        public readonly List<Entry> Entries = new List<Entry>();
        public readonly HashSet<string> EverRouted = new HashSet<string>();
        /// <summary>陣地が完成した（地形のテクスチャを塗り直す合図）。</summary>
        public bool TerrainChanged;

        public bool Enabled => true;

        public void Write(string jsonLine)
        {
            // 毎10秒の unit_snapshot は読み飛ばす（数が多く、ログには出さない）
            if (jsonLine.Contains("\"type\":\"unit_snapshot\"")) return;
            var v = JsonValue.Parse(jsonLine);
            double t = v.D("t");
            string type = v["type"].String;
            string unit = v.TryGet("unit", out var u) ? u.String : "";
            switch (type)
            {
                case "rout":
                    EverRouted.Add(unit);
                    bool enc = v.TryGet("encircled", out var e) && e.Bool;
                    if (!enc) Add(t, $"{unit} 壊走", true);
                    break;
                case "surrender":
                    EverRouted.Add(unit);
                    Add(t, $"{unit} 降伏（包囲されて壊走）", true);
                    break;
                case "encircled_start":
                    Add(t, $"{unit} 包囲された（退路なし）", true);
                    break;
                case "encircled_end":
                    Add(t, $"{unit} 包囲が解けた", false);
                    break;
                case "rally":
                    Add(t, v["phase"].String == "start" ? $"{unit} 再集結を始めた" : $"{unit} 再集結して指揮下に戻った", false);
                    break;
                case "left_battle":
                    Add(t, $"{unit} 会戦から離脱", true);
                    break;
                case "reserve_counterattack":
                    Add(t, $"{unit}（予備）が {v["target"].String} の側面へ逆襲", true);
                    break;
                case "entrench_complete":
                    TerrainChanged = true;
                    Add(t, $"{unit} 陣地が完成", false);
                    break;
                case "state_change":
                    if (v["to"].String == "shaken" && v["from"].String == "normal") Add(t, $"{unit} 動揺", false);
                    break;
            }
        }

        void Add(double t, string text, bool important) => Entries.Add(new Entry(t, text, important));

        public static string FormatTime(double sec)
        {
            int s = (int)sec;
            return $"T+{s / 60:00}:{s % 60:00}";
        }
    }
}
