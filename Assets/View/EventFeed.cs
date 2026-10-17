using System.Collections.Generic;
using Battalion.Sim;

namespace Battalion.View
{
    /// <summary>
    /// Sim のイベントログ（JSON Lines）を受け取り、画面下部のログに出す主な出来事を拾う。
    /// 駒の上に出す大きな出来事（<see cref="Recent"/>）も同時に積む。会戦中に一度でも壊走した中隊と、陣地の完成も数える。
    /// UnityEngine に依存しない。
    /// </summary>
    public sealed class EventFeed : IEventSink
    {
        public readonly struct Entry
        {
            public readonly double TimeSec;
            public readonly string Text;
            /// <summary>読み手の注意を引く出来事（壊走・降伏・包囲・側面や背面を突かれた・逆襲）か。</summary>
            public readonly bool Important;

            public Entry(double t, string text, bool important)
            {
                TimeSec = t;
                Text = text;
                Important = important;
            }
        }

        /// <summary>駒の上に出す出来事の種類。</summary>
        public enum Kind
        {
            FlankHit, RearHit, Encircled, Routed, Surrendered, RallyStart, RallyComplete, Counterattack, Entrenched, LeftBattle,
            TargetBroken, PursuitEnd, Turn, Disarray, SeekSight, Receipt, ReceiptOverdue,
        }

        /// <summary>駒の上に出す出来事。表示側が読んだら <see cref="Recent"/> を空にする。</summary>
        public readonly struct Happening
        {
            public readonly double TimeSec;
            public readonly Kind Kind;
            public readonly string Unit;
            /// <summary>逆襲の目標など（なければ null）。</summary>
            public readonly string Other;

            public Happening(double t, Kind kind, string unit, string other)
            {
                TimeSec = t;
                Kind = kind;
                Unit = unit;
                Other = other;
            }
        }

        public readonly List<Entry> Entries = new List<Entry>();
        public readonly List<Happening> Recent = new List<Happening>();
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
                case "first_flank_hit":
                    Add(t, $"{unit} 側面を突かれた（{v["by"].String} から）", true);
                    Happen(t, Kind.FlankHit, unit, v["by"].String);
                    break;
                case "first_rear_hit":
                    Add(t, $"{unit} 背面を突かれた（{v["by"].String} から）", true);
                    Happen(t, Kind.RearHit, unit, v["by"].String);
                    break;
                case "rout":
                    EverRouted.Add(unit);
                    bool enc = v.TryGet("encircled", out var e) && e.Bool;
                    if (!enc)
                    {
                        Add(t, $"{unit} 壊走", true);
                        Happen(t, Kind.Routed, unit, null);
                    }
                    break;
                case "surrender":
                    EverRouted.Add(unit);
                    Add(t, $"{unit} 降伏（包囲されて壊走）", true);
                    Happen(t, Kind.Surrendered, unit, null);
                    break;
                case "encircled_start":
                    Add(t, $"{unit} 包囲された（退路なし）", true);
                    Happen(t, Kind.Encircled, unit, null);
                    break;
                case "encircled_end":
                    Add(t, $"{unit} 包囲が解けた", false);
                    break;
                case "rally":
                    bool start = v["phase"].String == "start";
                    Add(t, start ? $"{unit} 再集結を始めた" : $"{unit} 再集結して指揮下に戻った", false);
                    Happen(t, start ? Kind.RallyStart : Kind.RallyComplete, unit, null);
                    break;
                case "left_battle":
                    Add(t, $"{unit} 会戦から離脱", true);
                    Happen(t, Kind.LeftBattle, unit, null);
                    break;
                case "reserve_counterattack":
                    Add(t, $"{unit}（予備）が {v["target"].String} の側面へ逆襲", true);
                    Happen(t, Kind.Counterattack, unit, v["target"].String);
                    break;
                case "entrench_complete":
                    TerrainChanged = true;
                    Add(t, $"{unit} 陣地が完成", false);
                    Happen(t, Kind.Entrenched, unit, null);
                    break;
                case "target_broken":
                {
                    bool pursue = v["decision"].String == "pursue";
                    Add(t, $"{unit} の攻撃の目標 {v["target"].String} が崩れた：{(pursue ? "追撃する" : "確保する")}", true);
                    Happen(t, Kind.TargetBroken, unit, pursue ? "pursue" : "secure");
                    break;
                }
                case "pursuit_end":
                {
                    string why = v["reason"].String;
                    string text = why == "limit" ? "限界まで追った" : why == "enemy_near" ? "敵が近づいた" : "目標が逃げ切った";
                    Add(t, $"{unit} 追撃をやめて確保（{text}）", false);
                    Happen(t, Kind.PursuitEnd, unit, null);
                    break;
                }
                case "turn_start":
                    Add(t, $"{unit} {v["toward"].String} の方へ向き直る", false);
                    Happen(t, Kind.Turn, unit, v["toward"].String);
                    break;
                case "disarray":
                    Add(t, $"{unit} 隊形が乱れた（壊走中の {v["by"].String} にすり抜けられた）", true);
                    Happen(t, Kind.Disarray, unit, v["by"].String);
                    break;
                case "runner_returned":
                    // 伝令の出来事は青だけ（赤の本部の事情は青から分からない）
                    if (v["side"].String != "blue") break;
                    Add(t, v["late"].Bool ? $"{unit} 命令受領（遅れて届いた報告）" : $"{unit} 命令受領", false);
                    Happen(t, Kind.Receipt, unit, null);
                    break;
                case "receipt_overdue":
                    if (v["side"].String != "blue") break;
                    Add(t, $"{unit} 受領報告なし（伝令が倒れた可能性）", true);
                    Happen(t, Kind.ReceiptOverdue, unit, null);
                    break;
                case "seek_sight":
                {
                    string how = v["how"].String == "side" ? "見える位置へ移る" : "前進";
                    Add(t, $"{unit}：目標 {v["target"].String} が見えないため{how}", false);
                    Happen(t, Kind.SeekSight, unit, v["target"].String);
                    break;
                }
                case "state_change":
                    if (v["to"].String == "shaken" && v["from"].String == "normal") Add(t, $"{unit} 動揺", false);
                    break;
            }
        }

        void Add(double t, string text, bool important) => Entries.Add(new Entry(t, text, important));
        void Happen(double t, Kind k, string unit, string other) => Recent.Add(new Happening(t, k, unit, other));

        public static string FormatTime(double sec)
        {
            int s = (int)sec;
            return $"T+{s / 60:00}:{s % 60:00}";
        }
    }
}
