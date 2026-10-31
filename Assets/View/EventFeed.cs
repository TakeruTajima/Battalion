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
        /// <summary>出来事の種類（色分け。P2c.2）：ふつう／中隊からの要請（目立つ色）／中隊からの宣言（独断の色）。</summary>
        public enum EntryKind { Normal, Request, Declaration }

        public readonly struct Entry
        {
            public readonly double TimeSec;
            public readonly string Text;
            /// <summary>読み手の注意を引く出来事（壊走・降伏・包囲・側面や背面を突かれた・逆襲）か。</summary>
            public readonly bool Important;
            public readonly EntryKind Kind;

            public Entry(double t, string text, bool important, EntryKind kind = EntryKind.Normal)
            {
                TimeSec = t;
                Text = text;
                Important = important;
                Kind = kind;
            }
        }

        /// <summary>駒の上に出す出来事の種類。</summary>
        public enum Kind
        {
            FlankHit, RearHit, Encircled, Routed, Surrendered, RallyStart, RallyComplete, Counterattack, Entrenched, LeftBattle,
            TargetBroken, PursuitEnd, Turn, Disarray, SeekSight, Receipt, ReceiptOverdue, StageAdvance,
            Independent, Autonomy, Formation,
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
        readonly HashSet<string> _sheetAborted = new HashSet<string>();
        /// <summary>制圧の出来事を最後に出した時刻（中隊と種類ごと。30秒以内の繰り返しは出さない。R1）。</summary>
        readonly Dictionary<string, double> _lastSuppression = new Dictionary<string, double>();
        const double SuppressionRepeatSec = 30;
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
                case "sheet_aborted":
                    // 壊走で命令書を中断した（P2b.2）。この直後の rout の行で「壊走（命令書は中断）」と出す
                    if (v["side"].String == "blue") _sheetAborted.Add(unit);
                    break;
                // E1：赤の守り方（敵の目に見える動きなので出す）
                case "defense_fallback":
                {
                    string r = v["reason"].String;
                    string why = r == "flank_fire" ? "側面から撃たれた" : $"迂回された：{DirJa(r)}";
                    Add(t, $"{unit} 後退（{why}）", false);
                    break;
                }
                case "defense_counterattack":
                    Add(t, $"{unit} 逆襲（{v["target"].String} へ）", true);
                    break;
                case "defense_return":
                    Add(t, v["spent"].Bool ? $"{unit} 逆襲の余力なし、戻る" : $"{unit} 逆襲を終えて戻る", false);
                    break;
                case "retake_order":
                    Add(t, $"赤 奪回の命令（{v["units"].String.Replace(",", "・")}）", true);
                    break;
                case "retake_attack":
                    Add(t, v.Has("from_place") && v.D("from_place") > 0
                        ? $"赤 奪回の攻撃（集まれず、その場から {v.D("from_place"):0}個）"
                        : $"赤 奪回の攻撃（集結 {v.D("assembled"):0}個）", true);
                    break;
                case "defense_assault_stop":
                {
                    string r = v.Has("reason") ? v["reason"].String : "left_trigger";
                    string what = v.Has("kind") && v["kind"].String == "retake" ? "奪回の突撃" : "逆襲";
                    string why = r == "stalled" ? "進めない" : r == "spent" ? "余力なし" : $"{v["target"].String} が引き金の円の外へ";
                    Add(t, $"{unit} {what}をやめる（{why}）", false);
                    break;
                }
                case "defense_last_hold":
                    Add(t, $"{unit} 最後の守り手として守る地点に残る", false);
                    break;
                case "defense_role_lost":
                    Add(t, $"{unit} 壊走で守りの役目を外れる", false);
                    break;
                case "retake_retarget":
                    Add(t, $"{unit} 奪回の目標を {v["target"].String} へ移す", false);
                    break;
                case "retake_occupy":
                    Add(t, $"{unit} 奪回：村に入って守る", false);
                    break;
                case "occupation_disarray":
                    Add(t, $"{unit} 占領の乱れ（{(v["position"].String == "village" ? "村" : "塹壕")}を取った直後）", false);
                    break;
                case "halt_resume":
                    if (v["side"].String == "blue") Add(t, $"{unit} 動揺から回復、止めていた段を再開", false);
                    break;
                case "rout":
                    EverRouted.Add(unit);
                    bool enc = v.TryGet("encircled", out var e) && e.Bool;
                    bool aborted = _sheetAborted.Remove(unit);
                    if (!enc)
                    {
                        Add(t, aborted ? $"{unit} 壊走（命令書は中断）" : $"{unit} 壊走", true);
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
                case "suppressed":
                case "suppression_end":
                {
                    string key = unit + "/" + type;
                    if (_lastSuppression.TryGetValue(key, out double last) && t - last < SuppressionRepeatSec) break;
                    _lastSuppression[key] = t;
                    if (type == "suppressed") Add(t, v["moving"].Bool ? $"{unit} 制圧下（伏せた）" : $"{unit} 制圧下（頭を下げた）", false);
                    else Add(t, $"{unit} 制圧が解けた", false);
                    break;
                }
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
                case "formation":
                {
                    // 隊形（F1）：縮合を始めた・展開を始めた（理由）。終わりは駒の進み具合で見えるので出さない
                    string what = v["event"].String;
                    string text = null;
                    if (what == "column_start") text = $"{unit} 縦隊になる";
                    else if (what == "column_cancel") text = $"{unit} 縦隊になるのをやめる";
                    else if (what == "deploy_start")
                    {
                        string why = v["reason"].String;
                        text = why == "fired_on" ? $"{unit} 縦隊で撃たれ、展開する"
                            : why == "enemy" ? $"{unit} 展開（{v["enemy"].String} を発見）"
                            : why == "near_goal" ? $"{unit} 展開（目的地の手前）"
                            : $"{unit} 展開（止まる）";
                    }
                    if (text == null) break;
                    if (v["side"].String != "blue") break;
                    Add(t, text, what == "deploy_start" && v["reason"].String == "fired_on");
                    Happen(t, Kind.Formation, unit, text.Substring(unit.Length + 1));
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
                case "flare":
                    if (v["side"].String != "blue") break;
                    Add(t, "信号弾 " + FlareJa(v["color"].String), true);
                    break;
                case "stage_advance":
                {
                    if (v["side"].String != "blue") break;
                    string why = v["reason"].String;
                    string text = why == "complete" ? "完了" : why == "time" ? "時刻" : why.StartsWith("flare_") ? "信号弾 " + FlareJa(why.Substring(6)) : why;
                    Add(t, $"{unit} 第{(int)v.D("stage")}段へ（{text}）", false);
                    Happen(t, Kind.StageAdvance, unit, $"第{(int)v.D("stage")}段へ");
                    break;
                }
                case "seek_sight":
                {
                    string how = v["how"].String == "side" ? "見える位置へ移る" : "前進";
                    Add(t, $"{unit}：目標 {v["target"].String} が見えないため{how}", false);
                    Happen(t, Kind.SeekSight, unit, v["target"].String);
                    break;
                }
                // ---- P2c：自律行動と独断
                // 独断の宣言は、中隊の伝令が本部に届いた時に出す（P2c.2。BattleApp.ProcessInbox）。
                // 独断の限度での停止は「命令を終えた」の報告で届く
                case "independent":
                case "independent_end":
                    break;
                case "auto_cover":
                    if (v["independent"].Bool) break; // 独断の後退は宣言で出した
                    if (v["side"].String != "blue") break;
                    Add(t, $"{unit} 物陰へ（開けた所で被弾）", false);
                    Happen(t, Kind.Autonomy, unit, "物陰へ");
                    break;
                case "scrape_done":
                    if (v["side"].String != "blue") break;
                    Add(t, $"{unit} 浅い掩体を掘り終えた（止まって静か）", false);
                    break;
                case "enter_position":
                    if (v["side"].String != "blue") break;
                    Add(t, $"{unit} 崩した敵の陣地に入る", false);
                    Happen(t, Kind.Autonomy, unit, "陣地に入る");
                    break;
                case "state_change":
                    if (v["to"].String == "shaken" && v["from"].String == "normal") Add(t, $"{unit} 動揺", false);
                    break;
            }
        }

        static string FlareJa(string c) => c == "red" ? "赤" : c == "green" ? "緑" : "白";

        static string PersonalityJa(string p) => p == "cautious" ? "慎重" : p == "reckless" ? "猪突" : p == "agile" ? "機敏" : "冷静";

        static string DirJa(string d)
        {
            switch (d)
            {
                case "east": return "東";
                case "northeast": return "北東";
                case "north": return "北";
                case "northwest": return "北西";
                case "west": return "西";
                case "southwest": return "南西";
                case "south": return "南";
                case "southeast": return "南東";
                default: return d;
            }
        }

        static string StateJa(string s) => s == "shaken" ? "動揺中" : s == "routed" ? "壊走中" : s == "normal" ? "正常" : s;

        void Add(double t, string text, bool important) => Entries.Add(new Entry(t, text, important));
        void Happen(double t, Kind k, string unit, string other) => Recent.Add(new Happening(t, k, unit, other));

        public static string FormatTime(double sec)
        {
            int s = (int)sec;
            return $"T+{s / 60:00}:{s % 60:00}";
        }
    }
}
