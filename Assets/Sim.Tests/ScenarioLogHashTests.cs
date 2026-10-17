using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// 通し実行の作戦（両ステージ、7作戦、シード1・2の28通り）のログ全体の SHA256 を出す。
    /// 表示だけの段（P1f など）で、会戦の結果が変わっていないことを前の段のハッシュと比べて確かめるのに使う。合否は付けない。
    /// </summary>
    public class ScenarioLogHashTests
    {
        public static string RunAndHash(MapScenario sc, ScenarioAReportTests.PlannedOrder[] plan, ulong seed, out int lines)
        {
            var log = new StringEventSink();
            var sim = sc.Build(TestUtil.Params, seed, log);
            var timed = plan.Where(o => o.WhenBroken == null).OrderBy(o => o.AtSec).ToList();
            var conditional = plan.Where(o => o.WhenBroken != null).ToList();
            int next = 0;
            double endSec = sc.TimeLimitMin * 60;
            while (sim.Time < endSec)
            {
                while (next < timed.Count && timed[next].AtSec <= sim.Time && ScenarioAReportTests.Issue(sim, timed[next])) next++;
                for (int k = conditional.Count - 1; k >= 0; k--)
                {
                    var o = conditional[k];
                    if (o.AtSec > sim.Time || sim.Find(o.WhenBroken).UnderCommand) continue;
                    if (ScenarioAReportTests.Issue(sim, o)) conditional.RemoveAt(k);
                }
                sim.Step();
                if (!sim.Companies.Any(c => c.Side == Side.Red && c.OnBoard) || !sim.Companies.Any(c => c.Side == Side.Blue && c.OnBoard)) break;
            }
            string text = log.ToString();
            lines = log.Count;
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        [Test]
        public void PrintLogHashes()
        {
            var sb = new StringBuilder();
            foreach (int stage in new[] { 1, 2 })
            {
                var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                foreach (var (name, plan) in ScenarioAReportTests.PlansWithPinAndFlank(stage))
                    foreach (ulong seed in new ulong[] { 1, 2 })
                    {
                        string h = RunAndHash(sc, plan, seed, out int n);
                        sb.AppendLine($"HASH stage{stage} {name} seed{seed} {n} {h}");
                    }
            }
            TestContext.WriteLine(sb.ToString());
            Assert.Pass();
        }
    }
}
