using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// R1：強い陣地の検証の場面 S7〜S9（仕様「R1：開けた所を動く代償と制圧（確定）」の検証の場面）。正解値を固定せず、
    /// 期待（史実の目安）を満たすかどうかで見る。各 20 シード。
    /// </summary>
    public class R1SceneTests
    {
        public const int Seeds = 20;

        /// <summary>1回の場面の結果。</summary>
        public sealed class SceneResult
        {
            /// <summary>守備（R）が崩れた（指揮下でなくなった）か。</summary>
            public bool DefenderBroken;
            /// <summary>攻める側（青）の損害率（全体）と、1個中隊以上が壊走（降伏を含む）したか。</summary>
            public double AttackerLoss;
            public bool AttackerRouted;
            /// <summary>青の中隊が一度でも伏せて止まったか（S9 の横切る中隊）。</summary>
            public bool PinnedDown;
            public double EndMin;
        }

        /// <summary>場面の打ち切り（分）：S7・S8 は 45分（途中報告2の後に 30分から延ばした）、S9 は 20分。</summary>
        public static double CapMin(Scenario sc) => sc.Name.StartsWith("S9", StringComparison.Ordinal) ? 20 : 45;

        /// <summary>
        /// 場面を1回回す。S7・S8 は守備が崩れるか、攻める側が全て指揮下でなくなるまで、S9 は横切る中隊が着くか指揮下で
        /// なくなるまで（どちらも打ち切りまで）。
        /// </summary>
        public static SceneResult Run(Scenario sc, SimParams p, ulong seed)
        {
            var sim = sc.Build(p, seed);
            var blue = sim.Companies.Where(c => c.Side == Side.Blue).ToList();
            var red = sim.Find("R");
            bool crossing = sc.Name.StartsWith("S9", StringComparison.Ordinal);
            var r = new SceneResult();
            int next = 0;
            double capSec = CapMin(sc) * 60;
            while (sim.Time < capSec)
            {
                sc.IssueDue(sim, ref next);
                sim.Step();
                foreach (var c in blue)
                {
                    if (c.State == CompanyState.Routed || c.State == CompanyState.Surrendered) r.AttackerRouted = true;
                    if (sim.PinnedDown(c)) r.PinnedDown = true;
                }
                if (!red.UnderCommand) { r.DefenderBroken = true; break; }
                if (blue.All(c => !c.UnderCommand)) break;
                if (crossing && next >= sc.Orders.Count && !sim.MovingPosture(blue[0]) && blue[0].Arrived) break;
            }
            r.AttackerLoss = blue.Sum(c => c.InitialStrength - c.Strength) / (double)blue.Sum(c => c.InitialStrength);
            r.EndMin = sim.Time / 60;
            return r;
        }

        /// <summary>場面ごとの集計（20シード）。</summary>
        public sealed class SceneSummary
        {
            public double BrokenRate, LossMean, RoutedRate, PinnedRate;
            /// <summary>30分以内に守備が崩れた割合（S7 の期待に使う）。</summary>
            public double Broken30Rate;
            /// <summary>守備が崩れた回の、崩れた時刻の中央値（分。崩れた回がなければ null）。</summary>
            public double? BrokenMedianMin;
        }

        public static SceneSummary Summarize(Scenario sc, SimParams p, int seeds = Seeds)
        {
            var rs = Enumerable.Range(1, seeds).Select(s => Run(sc, p, (ulong)s)).ToList();
            var broken = rs.Where(x => x.DefenderBroken).Select(x => x.EndMin).ToList();
            return new SceneSummary
            {
                BrokenMedianMin = broken.Count == 0 ? (double?)null : TestUtil.Median(broken),
                BrokenRate = rs.Average(x => x.DefenderBroken ? 1.0 : 0.0),
                Broken30Rate = rs.Average(x => x.DefenderBroken && x.EndMin <= 30 + 1e-9 ? 1.0 : 0.0),
                LossMean = rs.Average(x => x.AttackerLoss),
                RoutedRate = rs.Average(x => x.AttackerRouted ? 1.0 : 0.0),
                PinnedRate = rs.Average(x => x.PinnedDown ? 1.0 : 0.0),
            };
        }

        /// <summary>
        /// 期待（R1 の途中報告2の後に直したもの）：S7 は代償が大きい（攻めの損害 20% 以上か、30分以内に守備が崩れない割合
        /// 0.5 以上）、S8 は S7 より明らかに良い（45分以内に崩れる割合 0.6 以上、かつ攻めの損害が S7 の 2/3 以下）、S9 は
        /// 横切る代償が大きい。満たせない期待の一覧を返す。
        /// </summary>
        public static List<string> Unmet(SceneSummary s7, SceneSummary s8, SceneSummary s9)
        {
            var u = new List<string>();
            if (s7.LossMean < 0.20 - 1e-9 && 1 - s7.Broken30Rate < 0.5 - 1e-9) u.Add("S7 攻めの損害 ≥ 20% か30分以内に崩れない ≥ 0.5");
            if (s8.BrokenRate < 0.6 - 1e-9) u.Add("S8 45分以内に崩れる ≥ 0.6");
            if (s8.LossMean > s7.LossMean * 2 / 3 + 1e-9) u.Add("S8 攻めの損害 ≤ S7 の 2/3");
            if (s9.LossMean < 0.20 - 1e-9 && s9.PinnedRate < 0.5 - 1e-9) u.Add("S9 損害 ≥ 20% か伏せる ≥ 0.5");
            return u;
        }

        /// <summary>
        /// 値の掃き出し（R1 のタスク4。範囲は途中報告2の後に決めたもの）：開けた所を動く被弾倍率 3.0 で、動いている間と
        /// 止まっている間のしきい値・動きながら撃つ倍率・制圧が解けるまでの秒数・制圧下の撃つ力の 48組で S7〜S9 を回し、
        /// 期待を満たすかを表にする。値を決めるための道具なので、ふだんは回さない。
        /// </summary>
        [Test, Explicit]
        public void Sweep()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| 動くしきい値 | 止まるしきい値 | 動きながら撃つ | 解けるまで | 制圧下の撃つ力 | S7 30分以内に崩れる | S7 崩れる（45分） | S7 崩れた時刻 | S7 損害 | S8 崩れる（45分） | S8 崩れた時刻 | S8 損害 | S9 損害 | S9 伏せる | 満たせない期待 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            const double mo = 3.0;
            using var progress = RunProgress.Begin("R1 の掃き出し", 48 * 3 * Seeds);
            foreach (double tm in new[] { 1.0, 1.5 })
            foreach (double ts in new[] { 2.5, 3.5 })
            foreach (double mf in new[] { 0.3, 0.5 })
            foreach (double rel in new[] { 20.0, 60.0, 120.0 })
            foreach (double sfm in new[] { 0.25, 0.5 })
            {
                var p = TestUtil.ParamsWithR1(mo, tm, ts, mf, rel, sfm);
                var s7 = Summarize(Scenarios.S7_assault_front(), p);
                var s8 = Summarize(Scenarios.S8_suppress_then_assault(), p);
                var s9 = Summarize(Scenarios.S9_cross_field_of_fire(), p);
                for (int k = 0; k < 3 * Seeds; k++) RunProgress.Tick();
                var unmet = Unmet(s7, s8, s9);
                sb.AppendLine($"| {tm:0.0} | {ts:0.0} | {mf:0.0} | {rel:0}秒 | {sfm:0.00} | {s7.Broken30Rate:0.00} | {s7.BrokenRate:0.00} | {Min(s7.BrokenMedianMin)} | {s7.LossMean:0.000} | " +
                              $"{s8.BrokenRate:0.00} | {Min(s8.BrokenMedianMin)} | {s8.LossMean:0.000} | {s9.LossMean:0.000} | {s9.PinnedRate:0.00} | " +
                              $"{(unmet.Count == 0 ? "なし" : string.Join("・", unmet))} |");
            }
            TestContext.WriteLine(sb.ToString());
        }

        static string Min(double? m) => m.HasValue ? $"{m.Value:0.0}分" : "—";

        /// <summary>
        /// 決めた値（sim_params.json）で、S7〜S9 の期待を全て満たす（各 20シード）。R2.1（伏せた味方の超越）の後に掃き出しを
        /// 回し直して、fire.moving_fire_mult を 0.5 から 0.3 に替えた（R2.1 の報告）。
        /// </summary>
        [Test]
        public void ChosenValuesMeetAllTheExpectations()
        {
            Assert.That(UnmetWithChosenValues(), Is.Empty);
        }

        static List<string> UnmetWithChosenValues()
        {
            var p = TestUtil.Params;
            var s7 = Summarize(Scenarios.S7_assault_front(), p);
            var s8 = Summarize(Scenarios.S8_suppress_then_assault(), p);
            var s9 = Summarize(Scenarios.S9_cross_field_of_fire(), p);
            TestContext.WriteLine($"S7 30分以内に崩れる {s7.Broken30Rate:0.00}・45分 {s7.BrokenRate:0.00}・損害 {s7.LossMean:0.000}／" +
                                  $"S8 崩れる {s8.BrokenRate:0.00}・損害 {s8.LossMean:0.000}／S9 損害 {s9.LossMean:0.000}・伏せる {s9.PinnedRate:0.00}");
            return Unmet(s7, s8, s9);
        }
    }
}
