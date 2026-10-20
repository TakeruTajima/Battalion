using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// 接近戦の検算：50m で対峙した同数の正面衝突。合否は問わず、結果を出力するだけ（値の決定は報告を見てから）。
    /// 比較のため、同じ条件の 300m（S1）も並べて出す。
    /// </summary>
    [Category("Slow")]
    public class CloseCombatReportTests
    {
        [Test]
        public void Report()
        {
            using var progress = RunProgress.Begin("接近戦の表", 2 * 300);
            var cc = TestUtil.RunBatch("CC_1v1_front_50m");
            var s1 = TestUtil.RunBatch("S1_1v1_front");
            foreach (var st in new[] { cc, s1 })
            {
                TestContext.WriteLine(
                    $"{st.Scenario}（{st.Runs}回）: 壊走まで p05 {st.P05:F2} / 中央値 {st.Median:F2} / p95 {st.P95:F2} 分, " +
                    "平均損害率 " + string.Join(", ", st.LossMean.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value:F3}")) +
                    ", 壊走 " + string.Join(", ", st.RoutedCount.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")) +
                    $", 打ち切り {st.TimedOut}");
            }
            Assert.Pass();
        }
    }
}
