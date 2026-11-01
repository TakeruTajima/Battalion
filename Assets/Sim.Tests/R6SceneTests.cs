using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// R6：突撃の最後の一歩の検証の場面（仕様「R6：突撃の最後の一歩（突入と白兵戦、射撃の延伸）（確定）」の表）。各 20シード。
    /// S7（45分）・S8・S30 はここで、S28 は <see cref="P3Tests.AssaultAfterShelling"/>、S18・S19 は <see cref="E1SceneTests"/> の走らせ方を使う。
    /// </summary>
    public class R6SceneTests
    {
        public const int Seeds = 20;

        /// <summary>S7・S8・S30 の集計：45分以内に守備が崩れた割合と、攻める側が接近戦の距離に入ってから崩れるまでの分（崩した回）。</summary>
        public sealed class AssaultSummary
        {
            public double BrokenRate, LossMean;
            public readonly List<double> CloseToBreakMin = new List<double>();
            public readonly List<double> BrokenMin = new List<double>();
            public int Entries, Repulses;
            public double? CloseToBreakMedian => CloseToBreakMin.Count == 0 ? (double?)null : TestUtil.Median(CloseToBreakMin);
        }

        /// <summary>
        /// 場面を回す（45分、守備が崩れるか攻める側が全て指揮下でなくなるまで）。接近戦の距離に入った時刻は、青のどれかが初めて守備と
        /// 接近戦の距離に入った時刻。突入・跳ね返しの回数はログから数える。
        /// </summary>
        public static AssaultSummary Run(Scenario sc, SimParams p, int seeds = Seeds)
        {
            var s = new AssaultSummary();
            for (int seed = 1; seed <= seeds; seed++)
            {
                var log = new StringEventSink();
                var sim = sc.Build(p, (ulong)seed, log);
                var blue = sim.Companies.Where(c => c.Side == Side.Blue).ToList();
                var red = sim.Find("R");
                int next = 0;
                double? closeAt = null;
                bool broken = false;
                while (sim.Time < 45 * 60 - 1e-9)
                {
                    sc.IssueDue(sim, ref next);
                    sim.Step();
                    if (!closeAt.HasValue && blue.Any(b => b.UnderCommand && sim.InCloseCombat(b, red))) closeAt = sim.Time;
                    if (!red.UnderCommand) { broken = true; break; }
                    if (blue.All(c => !c.UnderCommand)) break;
                }
                if (broken)
                {
                    s.BrokenRate += 1.0 / seeds;
                    s.BrokenMin.Add(sim.Time / 60);
                    if (closeAt.HasValue) s.CloseToBreakMin.Add((sim.Time - closeAt.Value) / 60);
                }
                s.LossMean += blue.Sum(c => c.InitialStrength - c.Strength) / (double)blue.Sum(c => c.InitialStrength) / seeds;
                string text = log.ToString();
                s.Entries += Count(text, "\"type\":\"melee_entry\"");
                s.Repulses += Count(text, "\"type\":\"melee_repulsed\"");
            }
            return s;
        }

        static int Count(string text, string key)
        {
            int n = 0;
            for (int k = text.IndexOf(key, StringComparison.Ordinal); k >= 0; k = text.IndexOf(key, k + key.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        static string Min(double? m) => m.HasValue ? $"{m.Value:0.0}分" : "—";

        public static string Line(string name, AssaultSummary s) =>
            $"{name} 崩れる {s.BrokenRate:0.00}（時刻の中央値 {Min(s.BrokenMin.Count == 0 ? (double?)null : TestUtil.Median(s.BrokenMin))}、" +
            $"接近戦から崩れるまで {Min(s.CloseToBreakMedian)}）・攻めの損害 {s.LossMean:0.000}・突入 {s.Entries}・跳ね返し {s.Repulses}";

        /// <summary>場面の期待（S7・S8・S28・S30。S18・S19 は E1 の期待のまま）。満たさないものを返す。</summary>
        public static List<string> Unmet(SimParams p, StringBuilder line = null)
        {
            var u = new List<string>();
            var s7 = Run(Scenarios.S7_assault_front(), p);
            var s8 = Run(Scenarios.S8_suppress_then_assault(), p);
            var s30 = Run(Scenarios.S30_late_assault(), p);
            var a = P3Tests.AssaultAfterShelling(true, p);
            var b = P3Tests.AssaultAfterShelling(false, p);
            double? aClose = a.CloseToRoutMin.Count == 0 ? (double?)null : TestUtil.Median(a.CloseToRoutMin);
            if (s7.BrokenRate > 0.2 + 1e-9) u.Add($"S7 崩れる {s7.BrokenRate:0.00} > 0.2");
            if (s8.BrokenRate < 0.9 - 1e-9) u.Add($"S8 崩れる {s8.BrokenRate:0.00} < 0.9");
            if (!(s8.CloseToBreakMedian <= 5 + 1e-9)) u.Add($"S8 接近戦から崩れるまで {Min(s8.CloseToBreakMedian)} > 5分");
            if (!(aClose <= 5 + 1e-9)) u.Add($"S28 (a) 接近戦から崩れるまで {Min(aClose)} > 5分");
            if (a.RedRouted < 0.7 - 1e-9) u.Add($"S28 (a) 崩す {a.RedRouted:0.00} < 0.7");
            if (b.RedRouted > 0.3 + 1e-9) u.Add($"S28 (b) 崩す {b.RedRouted:0.00} > 0.3");
            if (s30.BrokenRate > s8.BrokenRate - 0.3 + 1e-9) u.Add($"S30 崩す {s30.BrokenRate:0.00} が S8 {s8.BrokenRate:0.00} より 0.3 以上低くない");
            line?.Append($"| {s7.BrokenRate:0.00} | {s8.BrokenRate:0.00}（{Min(s8.CloseToBreakMedian)}） | {a.RedRouted:0.00}（{Min(aClose)}） | {b.RedRouted:0.00} | {s30.BrokenRate:0.00} |");
            TestContext.WriteLine(Line("S7", s7));
            TestContext.WriteLine(Line("S8", s8));
            TestContext.WriteLine(Line("S30", s30));
            TestContext.WriteLine($"S28 (a) 崩す {a.RedRouted:0.00}・接近戦から崩れるまで {Min(aClose)}・詰めた {a.Closed:0.00}・詰めるまでの損害 {a.LossToClose:0.0}・青が崩れた {a.BlueRouted:0.00}");
            TestContext.WriteLine($"S28 (b) 崩す {b.RedRouted:0.00}・詰めた {b.Closed:0.00}・詰めるまでの損害 {b.LossToClose:0.0}・青が崩れた {b.BlueRouted:0.00}");
            return u;
        }

        [Test]
        public void ScenesMeetTheExpectations()
        {
            var u = Unmet(TestUtil.Params);
            TestContext.WriteLine(u.Count == 0 ? "期待：全て満たす" : "満たさない：" + string.Join("／", u));
            Assert.That(u, Is.Empty);
        }

        /// <summary>
        /// 会戦の経過（報告用）：場面 <paramref name="scene"/> のシード <paramref name="seed"/> を回し、突入・跳ね返し・白兵戦・制圧・壊走の
        /// 出来事と、30秒ごとの青と守備の距離（矩形の隙間）・士気・兵力を出す。
        /// </summary>
        static readonly string[] TraceKeys = { "melee_", "\"rout\"", "\"surrender\"", "\"suppressed\"", "suppression_end", "state_change" };

        /// <summary>10秒ごとに呼ぶ：新しい出来事（突入・白兵戦・制圧・壊走など）と、30秒ごとの守備 <paramref name="red"/> と攻める側の様子を出す。</summary>
        static void TraceTick(Simulation sim, StringEventSink log, Company red, List<Company> blue, ref int printed)
        {
            if (Math.Abs(sim.Time % 10) > 1e-9) return;
            if (Math.Abs(sim.Time % 30) < 1e-9)
                TestContext.WriteLine($"{sim.Time / 60:0.0}分 {red.Name} 兵 {red.Strength} 士気 {red.Morale:0} {(red.Suppressed ? "伏せ" : "")} | " +
                                      string.Join(" | ", blue.Select(b => $"{b.Name} 隙間 {UnitRect.Gap(sim.RectOf(b), sim.RectOf(red)):0}m 兵 {b.Strength} 士気 {b.Morale:0} {b.State}" +
                                                                          $"{(b.TargetId >= 0 ? " 撃つ" : "")}{(sim.PinnedDown(b) ? " 伏せ" : "")}")));
            var lines = log.ToString().Split('\n');
            for (; printed < lines.Length - 1; printed++)
            {
                string line = lines[printed];
                if (TraceKeys.Any(k => line.Contains(k))) TestContext.WriteLine("  " + line);
            }
        }

        /// <summary>
        /// シナリオA の作戦の会戦の経過（報告用。赤の砲兵なし）：R6 ありとなし（<see cref="TestUtil.WithoutR6"/>）で同じシードを回し、
        /// 突入・跳ね返し・白兵戦・壊走・降伏・目標の確保の出来事を並べる。
        /// </summary>
        [Test, Explicit("会戦の経過")]
        public void TraceScenarioA([Values(1, 2)] int stage, [Values("拘束＋迂回（近い拘束）", "拘束＋迂回（遠い拘束）", "総員突撃")] string plan,
            [Values(1ul, 2ul, 3ul)] ulong seed)
        {
            var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
            var orders = ScenarioAReportTests.PlansWithPinAndFlank(stage).First(x => x.name == plan).orders;
            var keys = new[] { "melee_", "\"rout\"", "\"surrender\"", "objective", "reserve_counterattack", "defense_assault", "retake_attack", "pursuit" };
            foreach (bool r6 in new[] { true, false })
            {
                var p = TestUtil.ParamsWith();
                if (!r6) TestUtil.WithoutR6(p);
                string text = ScenarioLogHashTests.RunAndLog(sc, orders, seed, out _, s => s.ClearArtillery(Side.Red), null, p);
                TestContext.WriteLine(r6 ? "## R6 あり" : "## R6 なし");
                foreach (var line in text.Split('\n'))
                    if (keys.Any(k => line.Contains(k)))
                        TestContext.WriteLine("  " + System.Text.RegularExpressions.Regex.Replace(line, @"""morale_causes"":\{[^}]*\},?", ""));
            }
        }

        /// <summary>教程2 の作戦の会戦の経過（報告用）。</summary>
        [Test, Explicit("会戦の経過")]
        public void TraceTutorial2([Values("総員突撃", "抑えてから詰める")] string plan, [Values(1ul, 2ul, 3ul)] ulong seed)
        {
            var sc = MapScenario.LoadFile(TestUtil.PathOf("maps/tutorial_2.json"));
            var orders = Tutorial2ReportTests.Plans().First(x => x.name == plan).orders;
            var log = new StringEventSink();
            int printed = 0;
            Company red = null;
            List<Company> blue = null;
            var r = TutorialReportTests.RunOne(sc, orders, seed, log: log, tick: s =>
            {
                red = red ?? s.Find("R1");
                blue = blue ?? s.Companies.Where(c => c.Side == Side.Blue).ToList();
                TraceTick(s, log, red, blue, ref printed);
            });
            TestContext.WriteLine($"勝ち {r.Win}");
        }

        [Test, Explicit("会戦の経過")]
        public void Trace([Values("S7_assault_front", "S8_suppress_then_assault", "S30_late_assault", "S18_counter_right_after_capture")] string scene, [Values(1ul, 2ul, 3ul)] ulong seed)
        {
            var sc = Scenarios.ByName(scene);
            var log = new StringEventSink();
            var sim = sc.Build(TestUtil.Params, seed, log);
            // 守備：S18・S19 は塹壕の青 A、ほかは塹壕の赤 R
            var red = sim.Find(scene.StartsWith("S18", StringComparison.Ordinal) ? "A" : "R");
            var blue = sim.Companies.Where(c => c.Side != red.Side).ToList();
            int next = 0;
            var keys = new[] { "melee_", "\"rout\"", "\"surrender\"", "\"suppressed\"", "suppression_end", "state_change" };
            int printed = 0;
            while (sim.Time < 45 * 60 - 1e-9 && red.UnderCommand && blue.Any(c => c.UnderCommand))
            {
                sc.IssueDue(sim, ref next);
                sim.Step();
                if (Math.Abs(sim.Time % 10) > 1e-9) continue;
                if (Math.Abs(sim.Time % 30) < 1e-9)
                    TestContext.WriteLine($"{sim.Time / 60:0.0}分 R 兵 {red.Strength} 士気 {red.Morale:0} {(red.Suppressed ? "伏せ" : "")} | " +
                                          string.Join(" | ", blue.Select(b => $"{b.Name} 隙間 {UnitRect.Gap(sim.RectOf(b), sim.RectOf(red)):0}m 兵 {b.Strength} 士気 {b.Morale:0} {b.State}" +
                                                                              $"{(b.TargetId >= 0 ? " 撃つ" : "")}{(sim.PinnedDown(b) ? " 伏せ" : "")}")));
                var lines = log.ToString().Split('\n');
                for (; printed < lines.Length - 1; printed++)
                    if (keys.Any(k => lines[printed].Contains(k))) TestContext.WriteLine("  " + lines[printed]);
            }
            TestContext.WriteLine($"終わり {sim.Time / 60:0.0}分 {red.Name} {red.State}");
        }

        /// <summary>仕様の掃き出し：melee.defense_mult.trench（1.5・2.0・3.0）× melee.morale_defender（15・20・30）。期待を外れた時だけ回す。</summary>
        [Test, Explicit("R6 の掃き出し")]
        public void Sweep()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| 塹壕の守る力 | 守り手の士気 | S7 崩れる | S8 崩れる（接近戦から） | S28 (a) 崩す（接近戦から） | S28 (b) 崩す | S30 崩す | 満たさないもの |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (double trench in new[] { 1.5, 2.0, 3.0 })
                foreach (double morale in new[] { 15.0, 20.0, 30.0 })
                {
                    var p = TestUtil.ParamsWith();
                    p.Melee.DefenseTrench = trench;
                    p.Melee.MoraleDefender = morale;
                    var line = new StringBuilder($"| {trench:0.0} | {morale:0} ");
                    var u = Unmet(p, line);
                    sb.AppendLine(line + $" {(u.Count == 0 ? "—" : string.Join("／", u))} |");
                }
            TestContext.WriteLine(sb.ToString());
        }
    }
}
