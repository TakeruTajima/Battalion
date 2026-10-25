using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// 検算テスト：S1〜S6 をシード 1〜300 で回し、壊走までの時間の中央値と各中隊の平均損害率が正解値の ±15% 以内に入ることを
    /// 確かめる（損害率が 0 の中隊は対象外。正解値が 0.05 未満の損害率は、差が ±0.01 以内でも可）。
    /// 正解値で全回降伏している中隊（S6 の R）は、本実装でも全回降伏することを確かめる。
    /// 正解値は reference_results.json（v4。R1 の制圧を入れたもの）。R1 の規則を止めた値は、v3 の正解値（テストの中に持つ）と
    /// 比べる（R1 がほかを変えていないことの見張り）。
    /// </summary>
    [Category("Slow")]
    public class ReferenceVerificationTests
    {
        const double Tolerance = 0.15;
        /// <summary>正解値がこれ未満の損害率は、差が <see cref="SmallLossAbsTolerance"/> 以内でも可（P0 の合格の行。v4 で足した）。</summary>
        const double SmallLoss = 0.05;
        const double SmallLossAbsTolerance = 0.01;
        const int Runs = 300;

        static JsonValue _ref;
        static JsonValue Ref => _ref ?? (_ref = JsonValue.Parse(File.ReadAllText(TestUtil.PathOf(TestUtil.ReferenceResultsRelPath))));

        static readonly string[] ScenarioNames = Scenarios.Reference;

        /// <summary>
        /// v3 の正解値（`combat_reference.py --v3`。R1 の前の reference_results.json と同じ）：壊走までの中央値（分）、損害率、
        /// 全回降伏する中隊。
        /// </summary>
        static readonly Dictionary<string, (double median, (string unit, double loss)[] losses, string[] allSurrender)> V3 =
            new Dictionary<string, (double, (string, double)[], string[])>
            {
                ["S1_1v1_front"] = (35.40, new[] { ("A", 0.347), ("R", 0.341) }, new string[0]),
                ["S2_2v1_front_front"] = (17.65, new[] { ("A", 0.168), ("B", 0.0), ("R", 0.445) }, new string[0]),
                ["S3_2v1_front_flank"] = (7.90, new[] { ("A", 0.082), ("B", 0.0), ("R", 0.256) }, new string[0]),
                ["S4_2v1_front_rear"] = (4.12, new[] { ("A", 0.047), ("B", 0.0), ("R", 0.168) }, new string[0]),
                ["S5_vet_vs_reg"] = (30.01, new[] { ("V", 0.299), ("R", 0.399) }, new string[0]),
                ["S6_4v1_surrounded"] = (1.32, new[] { ("A", 0.016), ("B", 0.0), ("C", 0.0), ("D", 0.0), ("R", 0.106) }, new[] { "R" }),
            };

        /// <summary>R1 の値（sim_params.json）で、v4 の正解値と比べる。</summary>
        [TestCaseSource(nameof(ScenarioNames))]
        public void MatchesReference(string scenario)
        {
            Assert.That(Ref["_version"].String, Is.EqualTo("v4"), "reference_results.json は v4");
            using var progress = RunProgress.Begin("S1〜S6 の v4 との照合 " + scenario, Runs);
            var st = TestUtil.RunBatch(scenario, Runs);
            var r = Ref[scenario];
            var losses = r["loss_mean"].Object.Select(kv => (kv.Key, kv.Value.Number)).ToArray();
            var allSurrender = r["surrendered_count"].Object.Where(kv => (int)kv.Value.Number == (int)r.D("runs")).Select(kv => kv.Key).ToArray();
            Check($"{scenario}（v4、R1 の値）", st, r["rout_time_min"].D("median"), losses, allSurrender,
                $"  正解の壊走まで p05/p95 [分]: {r["rout_time_min"].D("p05"):F2} / {r["rout_time_min"].D("p95"):F2}");
        }

        /// <summary>R1 の規則を止めた値で、v3 の正解値と比べる。</summary>
        [TestCaseSource(nameof(ScenarioNames))]
        public void MatchesV3WithoutR1(string scenario)
        {
            using var progress = RunProgress.Begin("S1〜S6 の v3 との照合（R1 なし） " + scenario, Runs);
            var st = TestUtil.RunBatch(scenario, Runs, p: TestUtil.ParamsWithoutR1());
            var (median, losses, allSurrender) = V3[scenario];
            Check($"{scenario}（v3、R1 の規則なし）", st, median, losses, allSurrender, null);
        }

        static void Check(string title, TestUtil.BatchStats st, double refMedian, (string unit, double loss)[] refLosses, string[] allSurrender,
            string extra)
        {
            var sb = new StringBuilder();
            var failures = new StringBuilder();
            sb.AppendLine($"{title}（{Runs}回、シード1〜{Runs}）");
            sb.AppendLine($"  壊走まで p05/中央値/p95 [分]: 本実装 {st.P05:F2} / {st.Median:F2} / {st.P95:F2}  正解の中央値 {refMedian:F2}" +
                          $"  中央値の差 {Diff(st.Median, refMedian):+0.0%;-0.0%}");
            if (extra != null) sb.AppendLine(extra);
            if (Math.Abs(Diff(st.Median, refMedian)) > Tolerance)
                failures.AppendLine($"  中央値が ±15% を外れた: {st.Median:F2} vs {refMedian:F2}");

            foreach (var (unit, refLoss) in refLosses)
            {
                double mine = st.LossMean[unit];
                bool small = refLoss < SmallLoss;
                string judge = refLoss == 0 ? "（対象外）"
                    : $"差 {Diff(mine, refLoss):+0.0%;-0.0%}" + (small ? $"（{mine - refLoss:+0.000;-0.000}。0.05 未満は ±0.01 でも可）" : "");
                sb.AppendLine($"  損害率 {unit}: 本実装 {mine:F3}  正解 {refLoss:F3}  {judge}");
                bool within = Math.Abs(Diff(mine, refLoss)) <= Tolerance || (small && Math.Abs(mine - refLoss) <= SmallLossAbsTolerance + 1e-12);
                if (refLoss != 0 && !within) failures.AppendLine($"  {unit} の損害率が外れた: {mine:F3} vs {refLoss:F3}");
            }
            sb.AppendLine("  壊走した側: " + string.Join(", ", st.RoutedCount.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")) +
                          "  降伏: " + string.Join(", ", st.SurrenderedCount.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")) +
                          $"  打ち切り={st.TimedOut}");
            foreach (var unit in allSurrender)
            {
                int mine = st.SurrenderedCount.TryGetValue(unit, out var n) ? n : 0;
                if (mine != Runs) failures.AppendLine($"  {unit} の降伏が {Runs} 回中 {mine} 回（正解値では全回）");
            }
            TestContext.WriteLine(sb.ToString());

            Assert.That(failures.Length, Is.EqualTo(0), failures.ToString());
        }

        static double Diff(double mine, double reference) => mine / reference - 1.0;
    }
}
