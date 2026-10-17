using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// シナリオAの両ステージで、概算シミュレーション（docs/reference/sketch/battle_sketch_v3.py）と同じ5つの作戦を
    /// 各20回（シード1〜20）回し、結果を概算の結果と並べて出力する。合否は付けない（例外なく終わることだけを確かめる）。
    /// </summary>
    public class ScenarioAReportTests
    {
        public const int Runs = 20;

        /// <summary>作戦の命令の種類（P1e で拘束・攻撃を足した）。</summary>
        public enum PlannedKind { Advance, PinFar, PinNear, AttackFlank }

        /// <summary>作戦の命令：時刻（秒）、中隊、目的地、到着後の向き（度）。拘束・攻撃では目標の中隊。</summary>
        public readonly struct PlannedOrder
        {
            public readonly double AtSec;
            public readonly string Unit;
            public readonly double X, Y, FacingDeg;
            public readonly PlannedKind Kind;
            public readonly string Target;
            /// <summary>条件つきの命令（P1e.1）：この中隊が指揮下でなくなったら出す（null なら時刻だけで出す）。</summary>
            public readonly string WhenBroken;

            public PlannedOrder(double atSec, string unit, double x, double y, double facingDeg,
                PlannedKind kind = PlannedKind.Advance, string target = null, string whenBroken = null)
            {
                WhenBroken = whenBroken;
                AtSec = atSec;
                Unit = unit;
                X = x;
                Y = y;
                FacingDeg = facingDeg;
                Kind = kind;
                Target = target;
            }
        }

        static PlannedOrder O(double at, string u, double x, double y, double f) => new PlannedOrder(at, u, x, y, f);
        static PlannedOrder T(double at, string u, PlannedKind kind, string target) => new PlannedOrder(at, u, 0, 0, 0, kind, target);

        /// <summary>
        /// 「拘束＋迂回」（P1e の6）：B2 が R1 を拘束し、B1 が R1 を側面の範囲で攻撃、B4 が 2分後に続く。B3 は B2 の南で火力支援。
        /// 攻撃は追撃なし（確保）。R1 が指揮下でなくなったら、B3 と B4 は村の中心へ前進する（P1e.1）。
        /// </summary>
        public static PlannedOrder[] PinAndFlank(int stage, bool near)
        {
            double fx = stage == 1 ? 1450 : 1350, fy = stage == 1 ? 800 : 900;
            return new[]
            {
                T(0, "B2", near ? PlannedKind.PinNear : PlannedKind.PinFar, "R1"),
                T(0, "B1", PlannedKind.AttackFlank, "R1"),
                T(120, "B4", PlannedKind.AttackFlank, "R1"),
                O(0, "B3", fx, fy, 0),
                new PlannedOrder(0, "B3", MapScenario.VillageCenter.X, MapScenario.VillageCenter.Y, 0, whenBroken: "R1"),
                new PlannedOrder(0, "B4", MapScenario.VillageCenter.X, MapScenario.VillageCenter.Y, 0, whenBroken: "R1"),
            };
        }

        /// <summary>battle_sketch_v3.py の plans と同じ（名前、順番、時刻、目的地、向き）。</summary>
        public static List<(string name, PlannedOrder[] orders)> Plans(int stage)
        {
            double fx = stage == 1 ? 1450 : 1350; // 稜線の塹壕から約375m
            var plans = new List<(string, PlannedOrder[])>
            {
                ("総員突撃", new[] { O(0, "B1", 1975, 1150, 0), O(0, "B2", 1975, 1000, 0), O(0, "B3", 1975, 850, 0), O(0, "B4", 1975, 1000, 0) }),
                ("正面火力戦", new[] { O(0, "B1", fx, 1200, 0), O(0, "B2", fx, 1000, 0), O(0, "B3", fx, 800, 0), O(0, "B4", 900, 1000, 0) }),
            };
            if (stage == 1)
            {
                plans.Add(("側面攻撃（1個中隊）", new[] { O(0, "B2", 1450, 1000, 0), O(0, "B1", 1975, 1250, -90), O(0, "B3", 900, 800, 0), O(0, "B4", 900, 1150, 0) }));
                plans.Add(("側面攻撃（2個中隊）", new[] { O(0, "B2", 1450, 1050, 0), O(0, "B3", 1450, 850, 0), O(0, "B1", 2075, 1300, -90), O(0, "B4", 1875, 1300, -90) }));
                plans.Add(("両翼包囲（鶴翼）", new[] { O(0, "B2", 1450, 1000, 0), O(0, "B1", 1975, 1250, -90), O(0, "B3", 1975, 750, 90), O(1200, "B4", 1450, 1200, 0) }));
            }
            else
            {
                plans.Add(("側面攻撃（1個中隊）", new[] { O(0, "B2", 1350, 1100, 0), O(0, "B3", 900, 900, 0), O(0, "B4", 900, 1100, 0), O(0, "B1", 1725, 1400, -90) }));
                plans.Add(("側面攻撃（2個中隊）", new[] { O(0, "B2", 1350, 1100, 0), O(0, "B3", 1350, 900, 0), O(0, "B1", 1825, 1400, -90), O(0, "B4", 1625, 1400, -90) }));
                plans.Add(("両翼包囲（鶴翼）", new[] { O(0, "B2", 1350, 1000, 0), O(0, "B1", 1725, 1400, -90), O(0, "B3", 1725, 600, 90), O(1200, "B4", 1350, 1200, 0) }));
            }
            return plans;
        }

        /// <summary>5作戦に「拘束＋迂回」の遠い版・近い版を足したもの（P1e の6）。</summary>
        public static List<(string name, PlannedOrder[] orders)> PlansWithPinAndFlank(int stage)
        {
            var plans = Plans(stage);
            plans.Add(("拘束＋迂回（遠い拘束）", PinAndFlank(stage, false)));
            plans.Add(("拘束＋迂回（近い拘束）", PinAndFlank(stage, true)));
            return plans;
        }

        public sealed class RunResult
        {
            public bool Win;
            public double EndMin;
            public double BlueLoss;
            /// <summary>会戦中に一度でも壊走（降伏を含む）した中隊の数。</summary>
            public int BlueBroken, RedBroken;
            /// <summary>終了時に指揮下にない（壊走中・再集結中・降伏・離脱）中隊の数。</summary>
            public int BlueOutAtEnd, RedOutAtEnd;
            public int RedSurrendered;
            /// <summary>
            /// 赤が全て同時に指揮下にない（壊走中・降伏・離脱）状態になった最初の時刻（分）。概算シミュレーションは壊走した中隊を
            /// 戻さず、この状態で会戦を終えて勝ちとするので、その勝ち方に当たる。なければ null。
            /// </summary>
            public double? RedAllBrokenMin;
            /// <summary>中隊ごとの最初の壊走の時刻、最初の向き直りの時刻、攻撃で発起点に着いた（方角を固定した）時刻（分）。</summary>
            public readonly Dictionary<string, double> FirstRoutMin = new Dictionary<string, double>();
            public readonly Dictionary<string, double> FirstTurnMin = new Dictionary<string, double>();
            public readonly Dictionary<string, double> AttackFixedMin = new Dictionary<string, double>();
        }

        sealed class RoutCounter : IEventSink
        {
            public readonly HashSet<string> Routed = new HashSet<string>();
            /// <summary>最初の壊走・向き直りの時刻（秒）。</summary>
            public readonly Dictionary<string, double> RoutAt = new Dictionary<string, double>();
            public readonly Dictionary<string, double> TurnAt = new Dictionary<string, double>();
            public bool Enabled => true;

            public void Write(string jsonLine)
            {
                // "type":"rout" と "type":"turn_start" の行だけ見る（"unit" はその直後に出る）
                Note(jsonLine, "\"type\":\"rout\",\"unit\":\"", RoutAt, Routed);
                Note(jsonLine, "\"type\":\"turn_start\",\"unit\":\"", TurnAt, null);
            }

            static void Note(string line, string key, Dictionary<string, double> at, HashSet<string> set)
            {
                int k = line.IndexOf(key, StringComparison.Ordinal);
                if (k < 0) return;
                int s = k + key.Length;
                string unit = line.Substring(s, line.IndexOf('"', s) - s);
                set?.Add(unit);
                if (!at.ContainsKey(unit)) at[unit] = JsonValue.Parse(line).D("t");
            }
        }

        public static RunResult RunOne(MapScenario sc, PlannedOrder[] plan, ulong seed)
        {
            var counter = new RoutCounter();
            var sim = sc.Build(TestUtil.Params, seed, counter);
            var orders = plan.Where(o => o.WhenBroken == null).OrderBy(o => o.AtSec).ToList();
            var conditional = plan.Where(o => o.WhenBroken != null).ToList();
            int next = 0;
            double? redAllBroken = null;
            var fixedAt = new Dictionary<string, double>();
            double endSec = sc.TimeLimitMin * 60;
            while (sim.Time < endSec)
            {
                while (next < orders.Count && orders[next].AtSec <= sim.Time)
                {
                    var o = orders[next++];
                    Issue(sim, o);
                }
                for (int k = conditional.Count - 1; k >= 0; k--)
                {
                    var o = conditional[k];
                    if (o.AtSec > sim.Time || sim.Find(o.WhenBroken).UnderCommand) continue;
                    conditional.RemoveAt(k);
                    Issue(sim, o);
                }
                sim.Step();
                foreach (var c in sim.Companies)
                    if (c.AttackFixed && c.CurrentOrder.Type == OrderType.Attack && !fixedAt.ContainsKey(c.Name)) fixedAt[c.Name] = sim.Time / 60;
                if (!redAllBroken.HasValue && sim.Companies.Where(c => c.Side == Side.Red).All(c => !c.UnderCommand))
                    redAllBroken = sim.Time / 60;
                bool redLeft = sim.Companies.Any(c => c.Side == Side.Red && c.OnBoard);
                bool blueLeft = sim.Companies.Any(c => c.Side == Side.Blue && c.OnBoard);
                if (!redLeft || !blueLeft) break;
            }
            return Summarize(sc, sim, counter, redAllBroken, fixedAt);
        }

        static void Issue(Simulation sim, PlannedOrder o)
        {
            var c = sim.Find(o.Unit);
            switch (o.Kind)
            {
                case PlannedKind.PinFar: sim.Issue(Order.Pin(c.Id, sim.Find(o.Target).Id, PinRange.Far)); break;
                case PlannedKind.PinNear: sim.Issue(Order.Pin(c.Id, sim.Find(o.Target).Id, PinRange.Near)); break;
                case PlannedKind.AttackFlank: sim.Issue(Order.Attack(c.Id, sim.Find(o.Target).Id, AttackAspect.Flank)); break;
                default: sim.Issue(Order.Advance(c.Id, new Vec2(o.X, o.Y), o.FacingDeg * Angles.DegToRad)); break;
            }
        }

        static RunResult Summarize(MapScenario sc, Simulation sim, RoutCounter counter, double? redAllBroken, Dictionary<string, double> fixedAt)
        {
            var blue = sim.Companies.Where(c => c.Side == Side.Blue).ToList();
            var red = sim.Companies.Where(c => c.Side == Side.Red).ToList();
            var result = new RunResult
            {
                Win = sc.BlueWins(sim),
                EndMin = sim.Time / 60,
                BlueLoss = blue.Sum(c => c.InitialStrength - c.Strength) / (double)blue.Sum(c => c.InitialStrength),
                BlueBroken = blue.Count(c => counter.Routed.Contains(c.Name)),
                RedBroken = red.Count(c => counter.Routed.Contains(c.Name)),
                BlueOutAtEnd = blue.Count(c => !c.UnderCommand),
                RedOutAtEnd = red.Count(c => !c.UnderCommand),
                RedSurrendered = red.Count(c => c.State == CompanyState.Surrendered),
                RedAllBrokenMin = redAllBroken,
            };
            foreach (var kv in counter.RoutAt) result.FirstRoutMin[kv.Key] = kv.Value / 60;
            foreach (var kv in counter.TurnAt) result.FirstTurnMin[kv.Key] = kv.Value / 60;
            foreach (var kv in fixedAt) result.AttackFixedMin[kv.Key] = kv.Value;
            return result;
        }

        [Test]
        public void FivePlansOnBothStages()
        {
            var sketch = JsonValue.Parse(File.ReadAllText(TestUtil.PathOf("docs/reference/sketch/battle_sketch_v3_results.json")));
            var sb = new StringBuilder();
            sb.AppendLine("| ステージ | 作戦 | 勝率 | 赤が全て崩れた割合 | 青の損害率 | 崩れた青（終了時） | 崩れた赤（終了時） | 降伏した赤 | 終了時刻の中央値 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (int stage in new[] { 1, 2 })
            {
                var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                foreach (var (name, plan) in PlansWithPinAndFlank(stage))
                {
                    var rs = Enumerable.Range(1, Runs).Select(s => RunOne(sc, plan, (ulong)s)).ToList();
                    // 「拘束＋迂回」は概算シミュレーションにない
                    bool hasSketch = sketch.TryGet($"stage{stage}:{name}", out var k);
                    string Ref(string key, string fmt) => hasSketch ? "［" + k.D(key).ToString(fmt) + "］" : "";
                    sb.AppendLine($"| {stage} | {name} | " +
                                  $"{rs.Average(r => r.Win ? 1.0 : 0.0):0.00}{Ref("win", "0.00")} | " +
                                  $"{rs.Average(r => r.RedAllBrokenMin.HasValue ? 1.0 : 0.0):0.00} | " +
                                  $"{rs.Average(r => r.BlueLoss):0.000}{Ref("blue_loss", "0.000")} | " +
                                  $"{rs.Average(r => r.BlueBroken):0.00}（{rs.Average(r => r.BlueOutAtEnd):0.00}）{Ref("blue_out", "0.00")} | " +
                                  $"{rs.Average(r => r.RedBroken):0.00}（{rs.Average(r => r.RedOutAtEnd):0.00}）{Ref("red_out", "0.00")} | " +
                                  $"{rs.Average(r => r.RedSurrendered):0.00}{Ref("red_surr", "0.00")} | " +
                                  $"{TestUtil.Median(rs.Select(r => r.EndMin).ToList()):0.0}分{Ref("t_end", "0.0")} |");
                }
            }
            TestContext.WriteLine("［ ］内は概算シミュレーションの値。崩れた中隊は、会戦中に一度でも壊走した数（ ）内は終了時に指揮下にない数");
            TestContext.WriteLine(sb.ToString());
            Assert.Pass();
        }

        /// <summary>
        /// 「拘束＋迂回」の経過（P1e の6）：拘束役 B2 が崩れた割合と時刻、迂回 B1・B4 が発起点に着いた時刻、R1 の向き直り、
        /// R1 が崩れた割合と時刻、R3 の逆襲の割合。時刻は起きた回の中央値（分）。
        /// </summary>
        [Test]
        public void PinAndFlankTimeline()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| ステージ | 拘束 | B2 壊走 | B1 着 | B4 着 | R1 向き直り | R1 壊走 | R2 壊走 | B1 壊走 | B4 壊走 | 勝率 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (int stage in new[] { 1, 2 })
            {
                var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                foreach (bool near in new[] { false, true })
                {
                    var rs = Enumerable.Range(1, Runs).Select(s => RunOne(sc, PinAndFlank(stage, near), (ulong)s)).ToList();
                    string Cell(Func<RunResult, Dictionary<string, double>> pick, string unit)
                    {
                        var ts = rs.Where(r => pick(r).ContainsKey(unit)).Select(r => pick(r)[unit]).ToList();
                        return ts.Count == 0 ? "0/20" : $"{ts.Count}/{Runs}・{TestUtil.Median(ts):0.0}分";
                    }
                    sb.AppendLine($"| {stage} | {(near ? "近い" : "遠い")} | {Cell(r => r.FirstRoutMin, "B2")} | " +
                                  $"{Cell(r => r.AttackFixedMin, "B1")} | {Cell(r => r.AttackFixedMin, "B4")} | " +
                                  $"{Cell(r => r.FirstTurnMin, "R1")} | {Cell(r => r.FirstRoutMin, "R1")} | {Cell(r => r.FirstRoutMin, "R2")} | " +
                                  $"{Cell(r => r.FirstRoutMin, "B1")} | {Cell(r => r.FirstRoutMin, "B4")} | " +
                                  $"{rs.Average(r => r.Win ? 1.0 : 0.0):0.00} |");
                }
            }
            TestContext.WriteLine("各欄は「起きた回数/20・その時刻の中央値」");
            TestContext.WriteLine(sb.ToString());
            Assert.Pass();
        }
    }
}
