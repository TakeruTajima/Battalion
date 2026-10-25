using System;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>R1：開けた所を動く代償と制圧（仕様「R1：開けた所を動く代償と制圧（確定）」の規則1〜3）。</summary>
    public class R1Tests
    {
        static Simulation Flat(SimParams p = null, IEventSink log = null)
        {
            p = p ?? TestUtil.Params;
            return new Simulation(p, 1, TerrainMap.FlatCovering(p.Terrain, -1500, -1500, 1500, 1500), log);
        }

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg,
            Personality pers = Personality.Calm, int? strength = null) =>
            sim.AddCompany(name, side, Training.Regular, pers, new Vec2(x, y), facingDeg, strength: strength);

        static void Give(Simulation sim, Order o) =>
            sim.IssueSheet(new OrderSheet(new[] { new Stage(o, StageTrigger.None, Discretion.Strict) }));

        /// <summary>士気を保って（動揺・壊走させずに）刻みを進める。</summary>
        static void Steps(Simulation sim, int n)
        {
            for (int i = 0; i < n; i++)
            {
                foreach (var c in sim.Companies) c.Morale = 100;
                sim.Step();
            }
        }

        static void Seconds(Simulation sim, double s) => Steps(sim, (int)Math.Round(s / sim.Params.TickSec));

        /// <summary>止まっている A を、正面の2個中隊（各 150人、300〜370m）が撃つ。火力の量は約 4.0（しきい値 3.5 以上）。</summary>
        static (Simulation sim, Company a, Company r1, Company r2) TwoOnOne(SimParams p = null, IEventSink log = null)
        {
            var sim = Flat(p, log);
            var a = Add(sim, "A", Side.Blue, 25, 25, 90);
            var r1 = Add(sim, "R1", Side.Red, 25, 350, -90);
            var r2 = Add(sim, "R2", Side.Red, 235, 330, -90);
            return (sim, a, r1, r2);
        }

        /// <summary>動いている M（東へ前進）を、正面 300m の R が撃つ。火力の量は約 2.0（動いている間のしきい値 1.5 以上）。</summary>
        static (Simulation sim, Company m, Company r) MoverUnderFire(Personality pers = Personality.Calm, SimParams p = null)
        {
            var sim = Flat(p);
            var m = Add(sim, "M", Side.Blue, 0, 0, 90, pers);
            var r = Add(sim, "R", Side.Red, 0, 300, -90);
            Give(sim, Order.Advance(m.Id, new Vec2(600, 0), 90 * Angles.DegToRad));
            return (sim, m, r);
        }

        /// <summary>動き始めるまで進める（練度の遅れ）。</summary>
        static void UntilMoving(Simulation sim, Company c)
        {
            for (int k = 0; k < 200 && !c.MovedThisTick; k++) Steps(sim, 1);
            Assert.That(c.MovedThisTick, Is.True, "動き始めた");
        }

        // ---------------------------------------------------------------- 火力の量と、制圧の始まり・終わり

        /// <summary>
        /// 火力の量は、守りを除いた損害の式の和（1個中隊 150人・400m 以内・正面で 0.0133×150 = 1.995）。止まっている A は
        /// しきい値 3.5 以上の状態が 10秒（20刻み）続いたら制圧下、半分（1.75）未満が release_sec 続いたら解ける。
        /// </summary>
        [Test]
        public void SuppressionStartsAfter10sAndEndsAfterReleaseSecBelowHalf()
        {
            var log = new StringEventSink();
            var (sim, a, r1, r2) = TwoOnOne(log: log);
            Steps(sim, 1);
            Assert.That(a.IncomingFirePerMin, Is.EqualTo(2 * 0.0133 * 150).Within(1e-9));
            Assert.That(sim.SuppressionThreshold(a), Is.EqualTo(3.5));
            Steps(sim, 18);
            Assert.That(a.Suppressed, Is.False, "9.5秒");
            Steps(sim, 1);
            Assert.That(a.Suppressed, Is.True, "10秒");
            StringAssert.Contains("\"type\":\"suppressed\",\"unit\":\"A\"", log.ToString());

            // 撃つ敵を遠ざける（火力の量 0）
            r1.Position = new Vec2(-1400, 1400);
            r2.Position = new Vec2(1400, 1400);
            int release = (int)Math.Round(sim.Params.Suppression.ReleaseSec / sim.Params.TickSec);
            Steps(sim, release - 1);
            Assert.That(a.IncomingFirePerMin, Is.EqualTo(0));
            Assert.That(a.Suppressed, Is.True, "release_sec の 1刻み前");
            Steps(sim, 1);
            Assert.That(a.Suppressed, Is.False, "release_sec で解ける");
            StringAssert.Contains("\"type\":\"suppression_end\",\"unit\":\"A\"", log.ToString());
        }

        /// <summary>しきい値の半分以上の火力が続く間は解けない（揺れ止め）。途中で途切れたら数え直す。</summary>
        [Test]
        public void SuppressionHoldsWhileFireStaysAboveHalf()
        {
            var (sim, a, r1, r2) = TwoOnOne();
            Seconds(sim, 10);
            Assert.That(a.Suppressed, Is.True);
            // 1個中隊だけ遠ざける：約 2.0（しきい値 3.5 の半分 1.75 以上）
            r2.Position = new Vec2(1400, 1400);
            Seconds(sim, 60);
            Assert.That(a.Suppressed, Is.True, "1.75 以上が続く間は解けない");
            // release_sec より 5秒短く火力を消して、戻す
            double rel = sim.Params.Suppression.ReleaseSec;
            r1.Position = new Vec2(-1400, 1400);
            Seconds(sim, rel - 5);
            r1.Position = new Vec2(25, 350);
            Seconds(sim, 10);
            r1.Position = new Vec2(-1400, 1400);
            Seconds(sim, rel - 5);
            Assert.That(a.Suppressed, Is.True, "途切れたら数え直す");
            Seconds(sim, 5);
            Assert.That(a.Suppressed, Is.False);
        }

        // ---------------------------------------------------------------- 伏せて止まる

        /// <summary>
        /// 動いている中隊は 1個中隊に撃たれれば伏せて止まる（段は捨てない）。撃つ敵の方を向く。解けたら、練度の遅れなしで
        /// 同じ段のまま動きを続ける。
        /// </summary>
        [Test]
        public void MovingCompanyLiesDownAndResumesTheSameStage()
        {
            var (sim, m, r) = MoverUnderFire();
            UntilMoving(sim, m);
            Seconds(sim, 11);
            Assert.That(m.Suppressed, Is.True);
            Assert.That(sim.PinnedDown(m), Is.True);
            var at = m.Position;
            Seconds(sim, 30);
            Assert.That(m.Position, Is.EqualTo(at), "伏せて止まっている");
            Assert.That(m.CurrentOrder.Type, Is.EqualTo(OrderType.Advance), "段は捨てない");
            Assert.That(m.Arrived, Is.False);
            Assert.That(Math.Abs(Angles.Wrap(m.Facing - Math.Atan2(r.Position.Y - m.Position.Y, r.Position.X - m.Position.X))), Is.LessThan(1e-6),
                "撃つ敵の方を向く");

            r.Position = new Vec2(-1400, 1400);
            Seconds(sim, sim.Params.Suppression.ReleaseSec - sim.Params.TickSec);
            Assert.That(m.Suppressed, Is.True);
            Assert.That(m.Position, Is.EqualTo(at));
            Steps(sim, 1);
            Assert.That(m.Suppressed, Is.False);
            Steps(sim, 1);
            Assert.That(m.MovedThisTick, Is.True, "解けた次の刻みから動く（練度の遅れなし）");
            Assert.That(m.CurrentOrder.Type, Is.EqualTo(OrderType.Advance));
        }

        /// <summary>規則1：開けた所を動いている間は被弾 ×moving_open_damage_mult、伏せている間は掛からない。</summary>
        [Test]
        public void MovingOpenMultIsNotAppliedWhileLyingDown()
        {
            var p = TestUtil.ParamsWithR1(3.0, 1.5, 3.5, 0.5);
            var (sim, m, _) = MoverUnderFire(p: p);
            UntilMoving(sim, m);
            Assert.That(sim.TerrainDamageMult(m), Is.EqualTo(3.0), "平地を動いている");
            Seconds(sim, 11);
            Assert.That(sim.PinnedDown(m), Is.True);
            Steps(sim, 1);
            Assert.That(m.MovedThisTick, Is.False);
            Assert.That(sim.TerrainDamageMult(m), Is.EqualTo(1.0), "伏せている間は掛からない");
        }

        /// <summary>撃つ力：制圧下は ×suppression.fire_mult（R1 の規則2）、動いた刻みは ×fire.moving_fire_mult（規則3）。</summary>
        [Test]
        public void SuppressedAndMovingShootersFireAtHalf()
        {
            var (sim, a, _, _) = TwoOnOne();
            Steps(sim, 1);
            // 兵力が減るので、1人あたりで比べる（損害の式は刻みの初めの兵力を使う）
            double before = sim.LastTickFire.Single(f => f.Shooter == a.Id).ExpectedPerMin / 150;
            Seconds(sim, 10);
            Assert.That(a.Suppressed, Is.True);
            int s0 = a.Strength;
            Steps(sim, 1);
            double after = sim.LastTickFire.Single(f => f.Shooter == a.Id).ExpectedPerMin / s0;
            Assert.That(after, Is.EqualTo(before * sim.Params.Suppression.FireMult).Within(1e-9), "制圧下 ×suppression.fire_mult");

            // 動きながら撃つ：撃たれない（射程の外から見えている敵を撃つ）向きで比べる
            var sim2 = Flat();
            var m = Add(sim2, "M", Side.Blue, 0, 0, 90);
            var t = Add(sim2, "T", Side.Red, 0, 300, -90, strength: 10);
            Steps(sim2, 1);
            double still = sim2.LastTickFire.Single(f => f.Shooter == m.Id).ExpectedPerMin;
            Give(sim2, Order.Advance(m.Id, new Vec2(600, 0), 90 * Angles.DegToRad));
            UntilMoving(sim2, m);
            double moving = sim2.LastTickFire.Single(f => f.Shooter == m.Id).ExpectedPerMin;
            Assert.That(moving, Is.EqualTo(still * sim2.Params.Fire.MovingFireMult).Within(1e-9), "動いた刻みは ×fire.moving_fire_mult");
        }

        // ---------------------------------------------------------------- 性格・接触の距離・自律行動・後退

        /// <summary>
        /// 動いている間のしきい値：猪突 ×1.5（2.25）、慎重 ×0.75（1.125）。止まっている間は性格によらない。
        /// 1個中隊に正面から撃たれる（約 2.0）と、慎重・冷静は伏せ、猪突は押し通す。
        /// </summary>
        [TestCase(Personality.Reckless, 2.25, false)]
        [TestCase(Personality.Cautious, 1.125, true)]
        [TestCase(Personality.Calm, 1.5, true)]
        public void PersonalityChangesTheMovingThreshold(Personality pers, double threshold, bool liesDown)
        {
            var (sim, m, _) = MoverUnderFire(pers);
            Assert.That(sim.SuppressionThreshold(m), Is.EqualTo(3.5), "止まっている間は性格によらない");
            UntilMoving(sim, m);
            Assert.That(sim.SuppressionThreshold(m), Is.EqualTo(threshold).Within(1e-12));
            Seconds(sim, 15);
            Assert.That(m.Suppressed, Is.EqualTo(liesDown));
            Assert.That(m.MovedThisTick, Is.EqualTo(!liesDown));
        }

        /// <summary>接触の距離（矩形の隙間 50m 以内）では、制圧下でも伏せて止まらない（制圧下の撃つ力の倍率は効く）。</summary>
        [Test]
        public void DoesNotLieDownInCloseCombat()
        {
            var sim = Flat();
            var m = Add(sim, "M", Side.Blue, 0, 0, 90);
            var r = Add(sim, "R", Side.Red, 0, 90, -90); // 矩形の隙間 40m
            Give(sim, Order.Advance(m.Id, new Vec2(600, 0), 90 * Angles.DegToRad));
            UntilMoving(sim, m);
            Seconds(sim, 11);
            Assert.That(sim.InCloseCombat(m, r), Is.True);
            Assert.That(m.Suppressed, Is.True);
            Assert.That(sim.PinnedDown(m), Is.False);
            Steps(sim, 1);
            Assert.That(m.MovedThisTick, Is.True, "動き続ける");
            Assert.That(sim.LastTickFire.Single(f => f.Shooter == m.Id).ExpectedPerMin,
                Is.EqualTo(0.0133 * m.Strength * 2.0 * sim.Params.Fire.MovingFireMult * sim.Params.Suppression.FireMult).Within(1e-9),
                "接近戦 ×2.0・動いた刻み・制圧下");
        }

        /// <summary>
        /// 制圧下は物陰へ寄らない（開けた所で撃たれ続けても動かない）。規則を止めると、同じ場面で 100m 以内の森へ寄る
        /// （場面が物陰へ寄る条件を満たしていることの確かめ）。
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void SuppressedCompanyDoesNotMoveToCover(bool r1Rules)
        {
            var (sim, a, r1, r2) = TwoOnOne(r1Rules ? null : TestUtil.ParamsWithoutR1());
            sim.Map.CellOf(new Vec2(75, 75), out int i, out int j);
            sim.Map.SetOverlay(i, j, TerrainMap.Forest);
            Give(sim, Order.Hold(a.Id));
            Seconds(sim, 200);
            if (r1Rules)
            {
                Assert.That(a.Suppressed, Is.True);
                Assert.That(a.Position, Is.EqualTo(new Vec2(25, 25)), "制圧下は物陰へ寄らない");
            }
            else
            {
                Assert.That(Vec2.Distance(a.Position, new Vec2(75, 75)), Is.LessThan(1), "規則を止めると森へ寄る");
            }
        }

        /// <summary>制圧下でも後退の命令だけは動ける（伏せたまま這って下がる）。</summary>
        [Test]
        public void RetreatOrderMovesEvenWhenSuppressed()
        {
            var (sim, a, _, _) = TwoOnOne();
            Seconds(sim, 10);
            Assert.That(a.Suppressed, Is.True);
            Give(sim, Order.Retreat(a.Id, new Vec2(25, -400)));
            UntilMoving(sim, a);
            Assert.That(a.Suppressed, Is.True);
            Assert.That(sim.PinnedDown(a), Is.False);
        }
    }
}
