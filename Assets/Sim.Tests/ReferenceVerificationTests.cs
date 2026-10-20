using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// 検算テスト：S1〜S6 をシード 1〜300 で回し、壊走までの時間の中央値と各中隊の平均損害率が
    /// reference_results.json の ±15% 以内に入ることを確かめる（損害率が 0 の中隊は対象外）。
    /// 正解値で全回降伏している中隊（S6 の R）は、本実装でも全回降伏することを確かめる。
    /// </summary>
    [Category("Slow")]
    public class ReferenceVerificationTests
    {
        const double Tolerance = 0.15;
        const int Runs = 300;

        static JsonValue _ref;
        static JsonValue Ref => _ref ?? (_ref = JsonValue.Parse(File.ReadAllText(TestUtil.PathOf(TestUtil.ReferenceResultsRelPath))));

        static readonly string[] ScenarioNames = Scenarios.Reference;

        [TestCaseSource(nameof(ScenarioNames))]
        public void MatchesReference(string scenario)
        {
            var st = TestUtil.RunBatch(scenario, Runs);
            var r = Ref[scenario];
            double refMedian = r["rout_time_min"].D("median");

            var sb = new StringBuilder();
            var failures = new StringBuilder();
            sb.AppendLine($"{scenario}（{Runs}回、シード1〜{Runs}）");
            sb.AppendLine($"  壊走まで p05/中央値/p95 [分]: 本実装 {st.P05:F2} / {st.Median:F2} / {st.P95:F2}" +
                          $"  正解 {r["rout_time_min"].D("p05"):F2} / {refMedian:F2} / {r["rout_time_min"].D("p95"):F2}" +
                          $"  中央値の差 {Diff(st.Median, refMedian):+0.0%;-0.0%}");
            if (Math.Abs(Diff(st.Median, refMedian)) > Tolerance)
                failures.AppendLine($"  中央値が ±15% を外れた: {st.Median:F2} vs {refMedian:F2}");

            foreach (var kv in r["loss_mean"].Object)
            {
                double refLoss = kv.Value.Number;
                double mine = st.LossMean[kv.Key];
                string judge = refLoss == 0 ? "（対象外）" : $"差 {Diff(mine, refLoss):+0.0%;-0.0%}";
                sb.AppendLine($"  損害率 {kv.Key}: 本実装 {mine:F3}  正解 {refLoss:F3}  {judge}");
                if (refLoss != 0 && Math.Abs(Diff(mine, refLoss)) > Tolerance)
                    failures.AppendLine($"  {kv.Key} の損害率が ±15% を外れた: {mine:F3} vs {refLoss:F3}");
            }
            sb.AppendLine("  壊走した側: " + string.Join(", ", st.RoutedCount.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")) +
                          "  降伏: " + string.Join(", ", st.SurrenderedCount.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")) +
                          $"  打ち切り={st.TimedOut}");
            foreach (var kv in r["surrendered_count"].Object)
            {
                int mine = st.SurrenderedCount.TryGetValue(kv.Key, out var n) ? n : 0;
                if ((int)kv.Value.Number == r.D("runs") && mine != Runs)
                    failures.AppendLine($"  {kv.Key} の降伏が {Runs} 回中 {mine} 回（正解値では全回）");
            }
            TestContext.WriteLine(sb.ToString());

            Assert.That(failures.Length, Is.EqualTo(0), failures.ToString());
        }

        static double Diff(double mine, double reference) => mine / reference - 1.0;
    }
}
