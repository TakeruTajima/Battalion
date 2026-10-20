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
    [Category("Slow")]
    public class ScenarioLogHashTests
    {
        public static string RunAndHash(MapScenario sc, ScenarioAReportTests.PlannedOrder[] plan, ulong seed, out int lines,
            Action<Simulation> start = null, Action<Simulation> tick = null) =>
            Hash(RunAndLog(sc, plan, seed, out lines, start, tick));

        public static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>中隊の伝令（P2c.2）の行を除いたログ（戦闘の行だけ）と、その行数。</summary>
        public static string StripCourierLines(string log, out int lines)
        {
            var sb = new StringBuilder();
            lines = 0;
            foreach (var l in log.Split('\n'))
            {
                if (l.Length == 0 || l.Contains("\"type\":\"courier_")) continue;
                sb.Append(l).Append('\n');
                lines++;
            }
            return sb.ToString();
        }

        /// <summary>作戦を1回回したログ全体（<paramref name="p"/> が null なら sim_params.json）。</summary>
        public static string RunAndLog(MapScenario sc, ScenarioAReportTests.PlannedOrder[] plan, ulong seed, out int lines,
            Action<Simulation> start = null, Action<Simulation> tick = null, SimParams p = null)
        {
            var log = new StringEventSink();
            var sim = sc.Build(p ?? TestUtil.Params, seed, log);
            start?.Invoke(sim);
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
                tick?.Invoke(sim);
                sim.Step();
                if (!sim.Companies.Any(c => c.Side == Side.Red && c.OnBoard) || !sim.Companies.Any(c => c.Side == Side.Blue && c.OnBoard)) break;
            }
            lines = log.Count;
            return log.ToString();
        }

        /// <summary>
        /// P2c.1.2（proto-p2c.1.2-fix）の通し実行のログのハッシュ（36通り）。P2c.2 の中隊の伝令は情報だけなので、
        /// 中隊の伝令の行を除いたログはこれと一致するはず。
        /// </summary>
        static readonly string[] P2c12Hashes =
        {
            "stage1 総員突撃 seed1 3053 39a65881c5376d9b1416a8c218ca82efddfd0c21fa9454a419a21d0427a7a864",
            "stage1 総員突撃 seed2 3060 f365f438f6583db87703895bff1f310e98d4fecbbfb7b3ec0555659c963c9b9c",
            "stage1 正面火力戦 seed1 3805 3041c8ffd5772a6f6e058278cacac26ed97d3e668da6c28b65e3c5540b4ce967",
            "stage1 正面火力戦 seed2 3805 38f9b870fd25fd4aba3a14ce135790b9bc304874d01d5ad4404ed1b2d43ca34e",
            "stage1 側面攻撃（1個中隊） seed1 3824 20fd34e7d6c5db3f3e40b2a6aab41bb310b7519961b3d9deaa6aa3c46ab8b876",
            "stage1 側面攻撃（1個中隊） seed2 3822 322576ffdd8ae27ad1e123365f87d38792d262b26858935563f5d37bc125c25d",
            "stage1 側面攻撃（2個中隊） seed1 3833 7032ca5662129489ff1619d28c8c6ad10e6f877af8dfb56e5087ad1779ea84b2",
            "stage1 側面攻撃（2個中隊） seed2 3831 6df1cdf0af904e2602894cf855f323570005df1660158ba4ea20683a0c7207aa",
            "stage1 両翼包囲（鶴翼） seed1 3862 0d900fc659f31d67c6c499d75b0cfc83d3a6c0162e58f79a9009c0d454c5b38a",
            "stage1 両翼包囲（鶴翼） seed2 3862 32e917c782704acd74b4904bfaf390e46b12b3e70ad8e99a939a29792ca108e4",
            "stage1 拘束＋迂回（遠い拘束） seed1 3900 b9b7717c47c4f7622b783b7d460b1de4af217615763d148d1fb246ca6e419bc3",
            "stage1 拘束＋迂回（遠い拘束） seed2 3909 c4c09f5d572941e8e6ed6de811473e2524346d19b910c4a9ef6927289d04657d",
            "stage1 拘束＋迂回（近い拘束） seed1 3908 682dc59e335a67d65332e2241e21f601137586be8364a2872f8c26d500c7b056",
            "stage1 拘束＋迂回（近い拘束） seed2 3922 c078b781fe716a60e6b052859b876dd391b6639e229bad8f9e9036a49e598bf6",
            "stage1 拘束＋猪突の攻撃（確保） seed1 3896 95c14620c214e56c4627f37a9bcb3b7c507d4bc8f90bec71abb9f287cff37416",
            "stage1 拘束＋猪突の攻撃（確保） seed2 3906 5f99204fea4b26d171e647c7df8b5a205195b8bf5aeee8418bf515bc92c1a50b",
            "stage1 拘束＋迂回（段階つき） seed1 3871 7749bd887ea8e85d00e0533d5ee173ee0857ac98a66f0b6fa4204ddd478a03ec",
            "stage1 拘束＋迂回（段階つき） seed2 3863 6dbd6a658dcbb165a76adeed2d96074e788a4a20620002f5e1279a3ec92a578a",
            "stage2 総員突撃 seed1 3848 5449bddfd7a6bce29fceff0edf3bee3a91789af4a32891c5b212ca3d9f895afc",
            "stage2 総員突撃 seed2 3861 fa96b12c1c4c690755e2aab6da0bc41ca87f0cf96a69e31889543b8ac30a2a3d",
            "stage2 正面火力戦 seed1 3835 5476a12d87fcafd2e00bdeb4c98735d71becc5dd11f519e29c5f73311098fe0a",
            "stage2 正面火力戦 seed2 3835 f85992359f9fb885fd80329d2986ddfa3a178ebe780329d76a1975c18a7f9f1f",
            "stage2 側面攻撃（1個中隊） seed1 3819 ac3307411f45ddb4e1e4e6620c0a0adca2f1979e561c11453d25d682cb143fce",
            "stage2 側面攻撃（1個中隊） seed2 3819 2b477ceb6367500c2ae3b889dfbe258785a0d2efe66526c2613a7ae53e767bb3",
            "stage2 側面攻撃（2個中隊） seed1 3833 432f96f029f9a7c05a5a38bb860020fa7d2a3ab3e9e5c35d781234e88472c580",
            "stage2 側面攻撃（2個中隊） seed2 3835 4a6c20a7bc8e506d591c08533b6481eceb7c16552fd671820c4963c7cba74fcc",
            "stage2 両翼包囲（鶴翼） seed1 3847 1fa5c663278c94007942c5677ee1460776a6cc55cdc2eeb471234d4f394779a5",
            "stage2 両翼包囲（鶴翼） seed2 3849 ed8850de30609ea1caff726f01953d2669a0c8ac56bb7f0e43085c87827457fe",
            "stage2 拘束＋迂回（遠い拘束） seed1 3533 7cad25ff87fca98c07a746bb797615cba275649161190bd992db9e4388b1ce68",
            "stage2 拘束＋迂回（遠い拘束） seed2 3549 053d20a90d59576b3d89f88d8037112a69594a89123b610e7f236cf7a8b15b2f",
            "stage2 拘束＋迂回（近い拘束） seed1 3927 a8aed0eadcddee9430b5fbfbc7b86283f42ee0dcf64daaa3f243a626dac126fa",
            "stage2 拘束＋迂回（近い拘束） seed2 3908 512cfe7677e0b1e89c386f56528f8be1c62ba8b5b099506297c1d36923c03c86",
            "stage2 拘束＋猪突の攻撃（確保） seed1 3515 d07189ac678df0db80a1c4fe91f620260e99d0b34f590db5959017ac9cd11b90",
            "stage2 拘束＋猪突の攻撃（確保） seed2 3878 383bbec6cc607dc012aaa4554ae3c18c5197ccc7725768edf0855b4f4efc6244",
            "stage2 拘束＋迂回（段階つき） seed1 3857 a2f5dcc2691cf0c04dfe67a37d2812b86206c7c794c4295533c1aa92b6c60190",
            "stage2 拘束＋迂回（段階つき） seed2 3859 97606be7c95b09657b14256f081653047cb7dc69012fa952b75c22221e4510c6",
        };

        /// <summary>P2c.1.2 のハッシュの行（「stage1 作戦 seed1 」で始まる行）。</summary>
        public static string P2c12Hash(string prefix) => System.Array.Find(P2c12Hashes, h => h.StartsWith(prefix, StringComparison.Ordinal));

        /// <summary>中隊の伝令の行を除いたログ（戦闘の行だけ）が、P2c.1.2 と1文字も違わないこと（36通り。数分かかる）。</summary>
        [Test]
        public void CombatLogMatchesP2c12()
        {
            var got = new System.Collections.Generic.List<string>();
            foreach (int stage in new[] { 1, 2 })
            {
                var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                foreach (var (name, plan) in ScenarioAReportTests.PlansWithPinAndFlank(stage))
                    foreach (ulong seed in new ulong[] { 1, 2 })
                    {
                        string text = StripCourierLines(RunAndLog(sc, plan, seed, out _), out int n);
                        got.Add($"stage{stage} {name} seed{seed} {n} {Hash(text)}");
                    }
                int st = stage;
                foreach (ulong seed in new ulong[] { 1, 2 })
                {
                    string text = StripCourierLines(RunAndLog(sc, new ScenarioAReportTests.PlannedOrder[0], seed, out _,
                        s => ScenarioAReportTests.StagedPinAndFlankStart(s, st), ScenarioAReportTests.StagedPinAndFlankTick), out int n);
                    got.Add($"stage{stage} {ScenarioAReportTests.StagedPlanName} seed{seed} {n} {Hash(text)}");
                }
            }
            for (int k = 0; k < got.Count; k++) TestContext.WriteLine((got[k] == P2c12Hashes[k] ? "一致 " : "違う ") + got[k]);
            Assert.That(got, Is.EqualTo(P2c12Hashes));
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
                int st = stage;
                foreach (ulong seed in new ulong[] { 1, 2 })
                {
                    string h = RunAndHash(sc, new ScenarioAReportTests.PlannedOrder[0], seed, out int n,
                        s => ScenarioAReportTests.StagedPinAndFlankStart(s, st), ScenarioAReportTests.StagedPinAndFlankTick);
                    sb.AppendLine($"HASH stage{stage} {ScenarioAReportTests.StagedPlanName} seed{seed} {n} {h}");
                }
            }
            TestContext.WriteLine(sb.ToString());
            Assert.Pass();
        }
    }
}
