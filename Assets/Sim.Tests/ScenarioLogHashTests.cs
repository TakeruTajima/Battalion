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
            RunProgress.Tick();
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

        /// <summary>
        /// R2.1（proto-r2.1-fix）の通し実行の作戦のログ全体のハッシュ（40通り。中隊の伝令の行も含む）。E1 の規則を止めた値
        /// （受け身の赤、占領の乱れなし）のログは、これと一致するはず。
        /// </summary>
        static readonly string[] R21Hashes =
        {
            "stage1 総員突撃 seed1 3945 1b4e17c733d69eda9c8022f3666b4716aea97933d4fdd3290f4bb6351d7ca32c",
            "stage1 総員突撃 seed2 3956 cbcb1221a91af143f18292bfd47626aad49afca562e6a534925f2f0a6c74f200",
            "stage1 正面火力戦 seed1 3874 b9bbaf13b67f57b0ee59f49f8d5f43b6fa38be9e8c2ab875f705e546a9a61d29",
            "stage1 正面火力戦 seed2 3874 078900c428d48a2c691c1e00877dafa511f829070adadbd906cacfde05f1f5e7",
            "stage1 側面攻撃（1個中隊） seed1 3886 872957b2fce00ed92adbdaeb03e25275669265af5422157f3cb913d41241a16d",
            "stage1 側面攻撃（1個中隊） seed2 3887 9677e892c012fec266310fd5f4b45f144be1214d6a879b3e67056e1ce720cd19",
            "stage1 側面攻撃（2個中隊） seed1 3905 32b501b7352bd67afc6085729a30d4c84e63dabfb34294b65233857ccd5f0d84",
            "stage1 側面攻撃（2個中隊） seed2 3908 29019585f60e1b8a92aed53607a67ad22fade41e1c3cf6c3eb0894787c4b6ef5",
            "stage1 両翼包囲（鶴翼） seed1 3923 d1cdeffe1cf872f1d1af571cd107e391fce9358840916f23d13a3875109c877e",
            "stage1 両翼包囲（鶴翼） seed2 3932 6db92917e6170306cbbb2de6ed27852eb8089590326aa8a20b351c1e034da650",
            "stage1 拘束＋迂回（遠い拘束） seed1 3953 335f1e8f87b8b46e37e71400eed1a335231167c0f5f0c586c00d5b356dceacbf",
            "stage1 拘束＋迂回（遠い拘束） seed2 3947 cb6d2d7e2e2f03d5a4bd02c6539b5471f77ad964fd67f2fe305a1fb1dfe54d4c",
            "stage1 拘束＋迂回（近い拘束） seed1 3756 27dcbcc819578ff098026854a705f49098b121a5c45722baf528457dbc532427",
            "stage1 拘束＋迂回（近い拘束） seed2 3977 0adc743e2085045e0c14fa8d45c01e730d4501749a9b7cb60512c983daa70778",
            "stage1 拘束＋猪突の攻撃（確保） seed1 3977 aeec3d29e4efc6dc6d6720888570cb2fd8021cf5f5638137e30ed6c1aef70d59",
            "stage1 拘束＋猪突の攻撃（確保） seed2 3958 7bd802586658f85efd0e4f3a8cedde617fd0fa60b16a49a8b459522bfc90ba9a",
            "stage1 村への前進（全中隊） seed1 3579 8171758402a4a10fc2cd97cc369535c4407a625f756a62335c123b33a1f41402",
            "stage1 村への前進（全中隊） seed2 3562 e4c092467711007ab125ba9abaa58db213f72212588e7a88edf7fc4b0a6823b6",
            "stage1 拘束＋迂回（段階つき） seed1 3923 ff496b9152758fb3fb1080e755ddff6f65be354852d327431bfcca0918058072",
            "stage1 拘束＋迂回（段階つき） seed2 3928 5465e0955af364cf4eaca9f086f82b09c3fbcd9188b82320c9ddef2a5c75eb12",
            "stage2 総員突撃 seed1 3908 6e006f3e993c2b950cc00108126142121ddb07d09fdcfcc58763bffed56566e2",
            "stage2 総員突撃 seed2 3926 efc1fc30ca5120251a9b1ce18e87e1c535a4e4c8ffa28c8c5344bf9c6a4bb480",
            "stage2 正面火力戦 seed1 3917 0c81a29f08e422d2da5d6fa4e0925098430a523fd92a1baf6718ec6627faa14e",
            "stage2 正面火力戦 seed2 3923 7bedbdab77f8d249ff364a7c8e435d90d31793e863c62f81dc719d7c7e1b1db7",
            "stage2 側面攻撃（1個中隊） seed1 3899 ac56db7e72efddb384a61308e77b067ca09fdc50e8e3d3b99dcd8ab62386f698",
            "stage2 側面攻撃（1個中隊） seed2 3910 86636611cd08f0a0d948d9469fb0298317b14a5dc06ad380f00ada461237c4c6",
            "stage2 側面攻撃（2個中隊） seed1 3917 a18f3347ce860e28742d15291277c543b6a2042d2567f29af578c02b78d56285",
            "stage2 側面攻撃（2個中隊） seed2 3926 916dd2763e808301b614b74deb94840587e95429b65e1e00566c31be6cd33dbb",
            "stage2 両翼包囲（鶴翼） seed1 3540 88126b3a26c76e8ed718beeb54e4b41eb193b653ba8fc9c05b20fdec39d8113e",
            "stage2 両翼包囲（鶴翼） seed2 3915 3b6e8b0336fbc4ffc59363b4581b392867a3a3c64fe8e2ade62b89bbaa683a0c",
            "stage2 拘束＋迂回（遠い拘束） seed1 3996 7eb735ec9daa7a1c3f0280807d6e6decbfddcf8cdc2c5597604cb362b1d10a31",
            "stage2 拘束＋迂回（遠い拘束） seed2 3964 1629bff77b2dcc1c8c687f9ecb49084152c88b75dbcd82c9ab84871254569c15",
            "stage2 拘束＋迂回（近い拘束） seed1 4028 d20a5ae4d0fd5dfd46981a6dae0b6dd3e03190b9ef9985d1f8d15183997d5d77",
            "stage2 拘束＋迂回（近い拘束） seed2 3976 5d3e550c6c92c1c4ed4445d1e76fdf022efee7f4a0aa040e1038d55d7b106486",
            "stage2 拘束＋猪突の攻撃（確保） seed1 3996 d68efc30a70dd906d2f33c2138ad13e09767910cd55c3055ba1797048c8a6d8d",
            "stage2 拘束＋猪突の攻撃（確保） seed2 3970 82c339b7d1e15628b833b7127a873436abee575b1b4d6e67eebc3366f0a85b43",
            "stage2 村への前進（全中隊） seed1 3915 b85c2441014aa88f0596648ccf3184c459518568307eff6edaf5b76b95ccbdbb",
            "stage2 村への前進（全中隊） seed2 3916 b8af9b01690cb83a5a2a93fbd645e15e7e427a0f2b642eb328c380a33801a746",
            "stage2 拘束＋迂回（段階つき） seed1 3939 ac0c6f8abbaf3778f864e2d218b5b30a3b4d7fc825f500bd0d92ab998c744fea",
            "stage2 拘束＋迂回（段階つき） seed2 3962 78f792b445a8acc825793f9f85acdbce4035736cb281468e2ecf9defe5a305af",
        };

        /// <summary>
        /// E1・R3・R4・F1 の規則を止めた値（defense.enabled・occupation.enabled が false、旋回は前の版のまま、接近戦でも陣地の守りが効く、
        /// 隊形なし）のログ全体が、R2.1 と1文字も違わないこと（40通り）。
        /// </summary>
        [Test]
        public void PassiveRedLogMatchesR21()
        {
            var p = TestUtil.WithoutP2d(TestUtil.WithoutF1(TestUtil.WithoutR4(TestUtil.WithoutR3(TestUtil.ParamsWithoutE1()))));
            using var progress = RunProgress.Begin("受け身の赤のログの一致", R21Hashes.Length);
            var got = new System.Collections.Generic.List<string>();
            foreach (int stage in new[] { 1, 2 })
            {
                var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                foreach (var (name, plan) in ScenarioAReportTests.PlansWithPinAndFlank(stage, legacyEnvelopment: true))
                    foreach (ulong seed in new ulong[] { 1, 2 })
                    {
                        string text = RunAndLog(sc, plan, seed, out int n, p: p);
                        got.Add($"stage{stage} {name} seed{seed} {n} {Hash(text)}");
                    }
                int st = stage;
                foreach (ulong seed in new ulong[] { 1, 2 })
                {
                    string text = RunAndLog(sc, new ScenarioAReportTests.PlannedOrder[0], seed, out int n,
                        s => ScenarioAReportTests.StagedPinAndFlankStart(s, st), ScenarioAReportTests.StagedPinAndFlankTick, p);
                    got.Add($"stage{stage} {ScenarioAReportTests.StagedPlanName} seed{seed} {n} {Hash(text)}");
                }
            }
            for (int k = 0; k < got.Count; k++) TestContext.WriteLine((got[k] == R21Hashes[k] ? "一致 " : "違う ") + got[k]);
            Assert.That(got, Is.EqualTo(R21Hashes));
        }

        /// <summary>P2c.1.2 のハッシュの行（「stage1 作戦 seed1 」で始まる行）。</summary>
        public static string P2c12Hash(string prefix) => System.Array.Find(P2c12Hashes, h => h.StartsWith(prefix, StringComparison.Ordinal));

        /// <summary>
        /// 中隊の伝令の行を除いたログ（戦闘の行だけ）が、P2c.1.2 と1文字も違わないこと（36通り。数分かかる）。
        /// R1 の規則（開けた所を動く代償・制圧・動きながら撃つ）は止めて回す（R1 がほかを変えていないことの見張り）。
        /// </summary>
        [Test]
        public void CombatLogMatchesP2c12()
        {
            var p = TestUtil.ParamsWithoutR1();
            using var progress = RunProgress.Begin("戦闘のログの一致", new[] { 1, 2 }.Sum(st => ScenarioAReportTests.PlansWithPinAndFlank(st).Count + 1) * 2);
            var got = new System.Collections.Generic.List<string>();
            foreach (int stage in new[] { 1, 2 })
            {
                var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                foreach (var (name, plan) in ScenarioAReportTests.PlansWithPinAndFlank(stage, legacyEnvelopment: true))
                    foreach (ulong seed in new ulong[] { 1, 2 })
                    {
                        string text = StripCourierLines(RunAndLog(sc, plan, seed, out _, p: p), out int n);
                        got.Add($"stage{stage} {name} seed{seed} {n} {Hash(text)}");
                    }
                int st = stage;
                foreach (ulong seed in new ulong[] { 1, 2 })
                {
                    string text = StripCourierLines(RunAndLog(sc, new ScenarioAReportTests.PlannedOrder[0], seed, out _,
                        s => ScenarioAReportTests.StagedPinAndFlankStart(s, st), ScenarioAReportTests.StagedPinAndFlankTick, p), out int n);
                    got.Add($"stage{stage} {ScenarioAReportTests.StagedPlanName} seed{seed} {n} {Hash(text)}");
                }
            }
            // P2c.1.2 の後に足した作戦（V1 の「村への前進（全中隊）」など）は比べる値がないので、出すだけ
            var mismatched = new System.Collections.Generic.List<string>();
            foreach (var g in got)
            {
                string prefix = string.Join(" ", g.Split(' ').Take(3)) + " ";
                string expected = P2c12Hash(prefix);
                if (expected == null) TestContext.WriteLine("（新しい作戦） " + g);
                else
                {
                    TestContext.WriteLine((g == expected ? "一致 " : "違う ") + g);
                    if (g != expected) mismatched.Add(g);
                }
            }
            Assert.That(P2c12Hashes.All(h => got.Contains(h) || mismatched.Any(m => m.StartsWith(string.Join(" ", h.Split(' ').Take(3)) + " ", StringComparison.Ordinal))),
                Is.True, "P2c.1.2 の作戦が全て回っている");
            Assert.That(mismatched, Is.Empty);
        }

        [Test]
        public void PrintLogHashes()
        {
            using var progress = RunProgress.Begin("ログのハッシュ", new[] { 1, 2 }.Sum(st => ScenarioAReportTests.PlansWithPinAndFlank(st).Count + 1) * 2);
            var sb = new StringBuilder();
            foreach (int stage in new[] { 1, 2 })
            {
                var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                foreach (var (name, plan) in ScenarioAReportTests.PlansWithPinAndFlank(stage, legacyEnvelopment: true))
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
