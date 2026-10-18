using System;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// P2c.1.2：「気付いている敵」の判定を揃える（仕様「P2c.1.2：『気付いている敵』の判定を揃える（確定）」）。
    /// どの場面も、稜線（中心 y=1025m の行、高さ 30m）を挟んで、接触の距離（矩形の隙間 50m 以内）にいる見えない敵を、
    /// 「気付いている」として扱うことを確かめる。
    /// </summary>
    public class P2c12Tests
    {
        static TerrainMap RidgeMap()
        {
            const int w = 40, h = 40;
            var sb = new StringBuilder();
            sb.Append("{\"cell_m\":50,\"width\":40,\"height\":40,\"rows_order\":\"north_first\",\"terrain\":[");
            sb.Append(string.Join(",", Enumerable.Repeat("\"" + new string('.', w) + "\"", h)));
            sb.Append("],\"elevation\":[");
            for (int r = 0; r < h; r++)
            {
                int j = h - 1 - r;
                sb.Append(r > 0 ? "," : "").Append("[").Append(string.Join(",", Enumerable.Repeat(j == 20 ? "30" : "0", w))).Append("]");
            }
            sb.Append("]}");
            return TerrainMap.FromJson(JsonValue.Parse(sb.ToString()), TestUtil.Params.Terrain);
        }

        static Simulation Sim(SimParams p = null) => new Simulation(p ?? TestUtil.Params, 1, RidgeMap());

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg) =>
            sim.AddCompany(name, side, Training.Regular, Personality.Calm, new Vec2(x, y), facingDeg);

        static void Run(Simulation sim, double seconds, params Company[] keep)
        {
            int n = (int)Math.Round(seconds / sim.Params.TickSec);
            for (int i = 0; i < n; i++)
            {
                foreach (var c in keep) c.Morale = 100;
                sim.Step();
            }
        }

        /// <summary>稜線の南の A と北の E。隙間 40m（接触の距離）で、視線は通らない。E は兵を 0 にして撃てる目標にならないようにする。</summary>
        static (Simulation sim, Company a, Company e) ContactBehindRidge(double aFacingDeg, SimParams p = null)
        {
            var sim = Sim(p);
            var a = Add(sim, "A", Side.Blue, 1025, 975, aFacingDeg);
            var e = Add(sim, "E", Side.Red, 1025, 1065, -90);
            Assert.That(sim.InFireContact(a, e), Is.True);
            Assert.That(LineOfSight.Visible(sim.Map, sim.Params.Sight, a.Position, e.Position), Is.False);
            Assert.That(sim.Aware(a, e), Is.True, "気付いている");
            return (sim, a, e);
        }

        // ---------------------------------------------------------------- 1. P1d の向き直りの正面の弧

        /// <summary>東から脇を撃たれても、正面（北）の接触の距離に気付いている敵がいれば、正面は空いていないので向き直らない（冷静は視線が要る性格）。</summary>
        [Test]
        public void FacingReactionTreatsAContactEnemyInFrontAsOccupied()
        {
            var (sim, a, e) = ContactBehindRidge(90);
            e.Strength = 0;
            var s = Add(sim, "S", Side.Red, 1325, 975, 180);
            sim.Issue(Order.Hold(a.Id, 90 * Angles.DegToRad));
            Run(sim, 60, a, e, s);
            Assert.That(a.LastFlankHitTime, Is.GreaterThan(0), "脇から撃たれている");
            Assert.That(a.FacingOverride, Is.Null, "向き直らない");
            Assert.That(a.Facing * Angles.RadToDeg, Is.EqualTo(90).Within(1e-6));
        }

        // ---------------------------------------------------------------- 2. P2c.1 の自律の向き

        /// <summary>撃てる目標がない中隊（東向き）が、接触の距離に気付いている北の敵の方を向く。</summary>
        [Test]
        public void AutoFacingTurnsTowardAContactEnemyBehindTheRidge()
        {
            var (sim, a, e) = ContactBehindRidge(0);
            e.Strength = 0;
            Run(sim, 60, a, e);
            Assert.That(a.TargetId, Is.EqualTo(-1));
            Assert.That(a.AutoFacing.Value * Angles.RadToDeg, Is.EqualTo(90).Within(1e-6), "向く相手になる");
        }

        /// <summary>正面（北）の接触の距離に気付いている敵がいれば、東の 500m の見える敵の方へ向き直らない。</summary>
        [Test]
        public void AutoFacingKeepsAContactEnemyInTheFrontArc()
        {
            var (sim, a, e) = ContactBehindRidge(90);
            e.Strength = 0;
            Add(sim, "N", Side.Red, 1525, 975, 180);
            Run(sim, 60, a, e);
            Assert.That(a.AutoFacing, Is.Null);
            Assert.That(a.Facing * Angles.RadToDeg, Is.EqualTo(90).Within(1e-6));
        }

        // ---------------------------------------------------------------- 3. P2c の物陰の候補

        /// <summary>
        /// 物陰の候補の点から目標を「撃てる」か：東隣の点は稜線で目標が見えないが、接触の距離なので撃てるに数える。
        /// 南の 75m 下がった点は、見えず接触の距離でもないので撃てない。
        /// （接触の距離の点は、経路で敵を避けるマスに入るので、実際の物陰の候補からは先に外れる。報告の迷った点）
        /// </summary>
        [Test]
        public void CoverCandidateCountsAContactPointAsAbleToShoot()
        {
            var (sim, a, e) = ContactBehindRidge(90);
            var side = new Vec2(1075, 975);
            var back = new Vec2(1025, 900);
            Assert.That(LineOfSight.Visible(sim.Map, sim.Params.Sight, side, e.Position), Is.False, "東隣の点から目標は見えない");
            Assert.That(sim.CanShootFrom(a, side, e), Is.True, "接触の距離なので撃てる");
            Assert.That(LineOfSight.Visible(sim.Map, sim.Params.Sight, back, e.Position), Is.False);
            Assert.That(sim.CanShootFrom(a, back, e), Is.False, "見えず、接触の距離でもない");
        }

        // ---------------------------------------------------------------- 4. 露出の地図

        /// <summary>敵の矩形から 25m のマスは、稜線で敵から見えなくても射界（露出）に数える。75m のマスは数えない。</summary>
        [Test]
        public void ExposureCountsCellsInContactDistanceOfTheEnemyRect()
        {
            var sim = Sim();
            Add(sim, "A", Side.Blue, 1025, 525, 90);
            var e = Add(sim, "E", Side.Red, 1025, 1075, 0); // 東向き：矩形は x 1000〜1050、y 975〜1175
            sim.Step();
            var near = new Vec2(1075, 975);
            var far = new Vec2(1125, 975);
            Assert.That(LineOfSight.Visible(sim.Map, sim.Params.Sight, e.Position, near), Is.False, "敵から見えない");
            Assert.That(LineOfSight.Visible(sim.Map, sim.Params.Sight, e.Position, far), Is.False);
            var exp = sim.ExposureFor(Side.Blue);
            Assert.That(exp[sim.Map.IndexOf(near)], Is.True, "敵の矩形から 25m：射界");
            Assert.That(exp[sim.Map.IndexOf(far)], Is.False, "敵の矩形から 75m：射界でない");
        }

        // ---------------------------------------------------------------- 5. 退路の見積もり

        /// <summary>逃げる間、接触の距離の見えない敵を撃ってくる敵に数える（接触の距離を 0 にした値と比べる）。</summary>
        [Test]
        public void EscapeEstimateCountsAContactEnemyBehindTheRidge()
        {
            var (sim, a, _) = ContactBehindRidge(90);
            var noContact = TestUtil.ParamsWith(("\"sight_contact_gap_m\": 50", "\"sight_contact_gap_m\": 0"));
            var sim0 = Sim(noContact);
            var a0 = Add(sim0, "A", Side.Blue, 1025, 975, 90);
            Add(sim0, "E", Side.Red, 1025, 1065, -90);
            var est = sim.EstimateEscape(a);
            var est0 = sim0.EstimateEscape(a0);
            // 南（方位 12）へ逃げる：稜線の陰のままで敵は見えない。逃げ始めの接触の距離の間だけ撃たれる
            Assert.That(est0.Loss[12], Is.EqualTo(0), "接触を数えなければ、見えない敵からは撃たれない");
            Assert.That(est.Loss[12], Is.GreaterThan(0), "逃げ始めの接触の距離の間、撃ってくる敵に数える");
            for (int k = 0; k < est.Loss.Length; k++) Assert.That(est.Loss[k], Is.GreaterThanOrEqualTo(est0.Loss[k]), $"方位 {k}");
        }
    }
}
