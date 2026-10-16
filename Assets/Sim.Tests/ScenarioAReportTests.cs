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

        /// <summary>作戦の命令：時刻（秒）、中隊、目的地、到着後の向き（度）。全て前進の命令。</summary>
        public readonly struct PlannedOrder
        {
            public readonly double AtSec;
            public readonly string Unit;
            public readonly double X, Y, FacingDeg;

            public PlannedOrder(double atSec, string unit, double x, double y, double facingDeg)
            {
                AtSec = atSec;
                Unit = unit;
                X = x;
                Y = y;
                FacingDeg = facingDeg;
            }
        }

        static PlannedOrder O(double at, string u, double x, double y, double f) => new PlannedOrder(at, u, x, y, f);

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
        }

        sealed class RoutCounter : IEventSink
        {
            public readonly HashSet<string> Routed = new HashSet<string>();
            public bool Enabled => true;

            public void Write(string jsonLine)
            {
                // "type":"rout" の行だけ数える（"unit" はその直後に出る）
                const string key = "\"type\":\"rout\",\"unit\":\"";
                int k = jsonLine.IndexOf(key, StringComparison.Ordinal);
                if (k < 0) return;
                int s = k + key.Length;
                Routed.Add(jsonLine.Substring(s, jsonLine.IndexOf('"', s) - s));
            }
        }

        public static RunResult RunOne(MapScenario sc, PlannedOrder[] plan, ulong seed)
        {
            var counter = new RoutCounter();
            var sim = sc.Build(TestUtil.Params, seed, counter);
            var orders = plan.OrderBy(o => o.AtSec).ToList();
            int next = 0;
            double? redAllBroken = null;
            double endSec = sc.TimeLimitMin * 60;
            while (sim.Time < endSec)
            {
                while (next < orders.Count && orders[next].AtSec <= sim.Time)
                {
                    var o = orders[next++];
                    var c = sim.Find(o.Unit);
                    sim.Issue(Order.Advance(c.Id, new Vec2(o.X, o.Y), o.FacingDeg * Angles.DegToRad));
                }
                sim.Step();
                if (!redAllBroken.HasValue && sim.Companies.Where(c => c.Side == Side.Red).All(c => !c.UnderCommand))
                    redAllBroken = sim.Time / 60;
                bool redLeft = sim.Companies.Any(c => c.Side == Side.Red && c.OnBoard);
                bool blueLeft = sim.Companies.Any(c => c.Side == Side.Blue && c.OnBoard);
                if (!redLeft || !blueLeft) break;
            }
            var blue = sim.Companies.Where(c => c.Side == Side.Blue).ToList();
            var red = sim.Companies.Where(c => c.Side == Side.Red).ToList();
            return new RunResult
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
                foreach (var (name, plan) in Plans(stage))
                {
                    var rs = Enumerable.Range(1, Runs).Select(s => RunOne(sc, plan, (ulong)s)).ToList();
                    var k = sketch[$"stage{stage}:{name}"];
                    sb.AppendLine($"| {stage} | {name} | " +
                                  $"{rs.Average(r => r.Win ? 1.0 : 0.0):0.00}［{k.D("win"):0.00}］ | " +
                                  $"{rs.Average(r => r.RedAllBrokenMin.HasValue ? 1.0 : 0.0):0.00} | " +
                                  $"{rs.Average(r => r.BlueLoss):0.000}［{k.D("blue_loss"):0.000}］ | " +
                                  $"{rs.Average(r => r.BlueBroken):0.00}（{rs.Average(r => r.BlueOutAtEnd):0.00}）［{k.D("blue_out"):0.00}］ | " +
                                  $"{rs.Average(r => r.RedBroken):0.00}（{rs.Average(r => r.RedOutAtEnd):0.00}）［{k.D("red_out"):0.00}］ | " +
                                  $"{rs.Average(r => r.RedSurrendered):0.00}［{k.D("red_surr"):0.00}］ | " +
                                  $"{TestUtil.Median(rs.Select(r => r.EndMin).ToList()):0.0}分［{k.D("t_end"):0.0}］ |");
                }
            }
            TestContext.WriteLine("［ ］内は概算シミュレーションの値。崩れた中隊は、会戦中に一度でも壊走した数（ ）内は終了時に指揮下にない数");
            TestContext.WriteLine(sb.ToString());
            Assert.Pass();
        }
    }
}
