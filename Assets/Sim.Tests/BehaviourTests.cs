using System;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>状態遷移・壊走・再集結・離脱・移動の振る舞い。</summary>
    public class BehaviourTests
    {
        static Simulation Empty(IEventSink log = null) =>
            new Simulation(TestUtil.Params, 1, new MapBounds(-1500, -1000, 1500, 1000), log);

        /// <summary>直前に狙われた扱いにして、射程外での回復（+1/分）がしきい値の確認に混ざらないようにする。</summary>
        static void SuppressRecovery(Simulation sim)
        {
            foreach (var c in sim.Companies) c.LastTargetedTime = sim.Time;
        }

        [Test]
        public void ShakenAndRoutThresholds()
        {
            var sim = Empty();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0, morale: 50);
            sim.AddCompany("Far", Side.Red, Training.Regular, Personality.Calm, new Vec2(1000, 0), 180);
            SuppressRecovery(sim);
            sim.Step();
            Assert.That(a.State, Is.EqualTo(CompanyState.Shaken)); // 50 以下で動揺
            a.Morale = 25;
            sim.Step();
            Assert.That(a.State, Is.EqualTo(CompanyState.Routed)); // 閾値以下で壊走
            Assert.That(sim.FirstRoutTime, Is.EqualTo(1.0));
        }

        [Test]
        public void EncircledRoutSurrenders()
        {
            // S4（正面＋背面）は R が包囲された状態で壊走するので、降伏になる
            var r = BattleRunner.RunToFirstRout(Scenarios.S4_2v1_front_rear(), TestUtil.Params, 1);
            var sim = Scenarios.S4_2v1_front_rear().Build(TestUtil.Params, 1);
            while (!sim.FirstRoutTime.HasValue) sim.Step();
            Assert.That(sim.Find("R").State, Is.EqualTo(CompanyState.Surrendered));
            Assert.That(r.RoutedUnits, Is.EqualTo(new[] { "R" }));
        }

        [Test]
        public void RoutedUnitFleesThenRalliesAndReturns()
        {
            var log = new StringEventSink();
            var sim = Scenarios.S1_1v1_front().Build(TestUtil.Params, 1, log);
            while (!sim.FirstRoutTime.HasValue) sim.Step();
            var routed = sim.Companies.Single(c => c.State == CompanyState.Routed);
            var start = routed.Position;
            for (int i = 0; i < 120 * 60 && routed.State != CompanyState.Normal && routed.State != CompanyState.Shaken; i++)
                sim.Step();
            Assert.That(Vec2.Distance(start, routed.Position), Is.GreaterThan(100), "逃走していない");
            Assert.That(routed.State, Is.EqualTo(CompanyState.Shaken), "士気40で動揺として指揮下に戻る");
            Assert.That(routed.Morale, Is.EqualTo(40).Within(0.02));
            string text = log.ToString();
            StringAssert.Contains("\"type\":\"rally\",\"unit\":\"" + routed.Name + "\",\"phase\":\"start\"", text);
            StringAssert.Contains("\"type\":\"rally\",\"unit\":\"" + routed.Name + "\",\"phase\":\"complete\"", text);
        }

        [Test]
        public void RoutCascadesToNeighbours()
        {
            var sim = Empty();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0, morale: 20);
            var b = sim.AddCompany("B", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 250), 0, morale: 34);
            var c = sim.AddCompany("C", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 500), 0, morale: 60);
            var far = sim.AddCompany("Far", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, -400), 0, morale: 60);
            sim.AddCompany("E", Side.Red, Training.Regular, Personality.Calm, new Vec2(1400, 0), 180);
            SuppressRecovery(sim);
            sim.Step();
            Assert.That(a.State, Is.EqualTo(CompanyState.Routed));
            Assert.That(b.State, Is.EqualTo(CompanyState.Routed), "A の壊走で −10 を受けて連鎖する");
            Assert.That(c.Morale, Is.EqualTo(50).Within(1e-9), "B の壊走で一度だけ −10");
            Assert.That(c.State, Is.EqualTo(CompanyState.Shaken));
            Assert.That(far.Morale, Is.EqualTo(60).Within(1e-9), "300m より遠い味方は影響を受けない");
            Assert.That(c.Ledger.NeighborRout, Is.EqualTo(-10).Within(1e-9));
        }

        [Test]
        public void RoutedUnitLeavesBattleAtMapEdge()
        {
            var log = new StringEventSink();
            var sim = new Simulation(TestUtil.Params, 1, new MapBounds(-100, -1000, 1000, 1000), log);
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0, morale: 10);
            sim.AddCompany("E", Side.Red, Training.Regular, Personality.Calm, new Vec2(300, 0), 180);
            for (int i = 0; i < 400; i++) sim.Step();
            Assert.That(a.State, Is.EqualTo(CompanyState.LeftBattle));
            StringAssert.Contains("\"type\":\"left_battle\"", log.ToString());
        }

        [Test]
        public void AdvanceMovesAndTurns()
        {
            var sim = Empty();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 90);
            sim.Issue(Order.Advance(a.Id, new Vec2(120, 0), 90 * Angles.DegToRad));
            // 90° の旋回に 15 秒（30刻み）。旋回中は動かず、向き終えた刻みから進む
            for (int i = 0; i < 29; i++) sim.Step();
            Assert.That(a.Facing, Is.EqualTo(3 * Angles.DegToRad).Within(1e-9));
            Assert.That(a.Position.X, Is.EqualTo(0).Within(1e-9));
            sim.Step();
            Assert.That(a.Facing, Is.EqualTo(0).Within(1e-9));
            Assert.That(a.Position.X, Is.EqualTo(0.6).Within(1e-9));
            // 120m を秒速 1.2m で 100 秒
            for (int i = 0; i < 200; i++) sim.Step();
            Assert.That(a.Position.X, Is.EqualTo(120).Within(1e-9));
            Assert.That(a.Arrived, Is.True);
            // 到着後に指定の向きへ旋回
            for (int i = 0; i < 30; i++) sim.Step();
            Assert.That(a.Facing, Is.EqualTo(90 * Angles.DegToRad).Within(1e-9));
        }

        [Test]
        public void ShakenRefusesAdvance()
        {
            var sim = Empty();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0, morale: 45);
            sim.AddCompany("Far", Side.Red, Training.Regular, Personality.Calm, new Vec2(1400, 0), 180);
            sim.Step();
            Assert.That(a.State, Is.EqualTo(CompanyState.Shaken));
            sim.Issue(Order.Advance(a.Id, new Vec2(500, 0)));
            for (int i = 0; i < 20; i++) sim.Step();
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            Assert.That(a.Position.X, Is.EqualTo(0).Within(1e-9));
        }

        [Test]
        public void PinnedRetreatIsDisengagement()
        {
            var sim = Empty();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            sim.AddCompany("E", Side.Red, Training.Regular, Personality.Calm, new Vec2(120, 0), 180);
            sim.Step();
            Assert.That(a.Pinned, Is.True, "150m 以内から撃たれて拘束");
            double before = a.Morale;
            sim.Issue(Order.Retreat(a.Id, new Vec2(-500, 0)));
            sim.Step();
            Assert.That(a.Disengaging, Is.True);
            Assert.That(a.Ledger.Disengage, Is.EqualTo(-10).Within(1e-9));
            Assert.That(a.Morale, Is.LessThanOrEqualTo(before - 10));
        }

        [Test]
        public void PinnedMovesAtHalfSpeed()
        {
            var sim = Empty();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            sim.AddCompany("E", Side.Red, Training.Regular, Personality.Calm, new Vec2(140, 0), 180);
            sim.Step();
            Assert.That(a.Pinned, Is.True);
            sim.Issue(Order.Advance(a.Id, new Vec2(0, 0) + new Vec2(100, 0)));
            sim.Step();
            Assert.That(a.Position.X, Is.EqualTo(1.2 * 0.5 * 0.5).Within(1e-9));
        }

        [Test]
        public void RecoveryOnlyAfterQuietMinuteAndCappedAt70()
        {
            var sim = Empty();
            var a = sim.AddCompany("A", Side.Blue, Training.Veteran, Personality.Calm, new Vec2(0, 0), 0, morale: 69.99);
            sim.AddCompany("Far", Side.Red, Training.Regular, Personality.Calm, new Vec2(1400, 0), 180);
            for (int i = 0; i < 10; i++) sim.Step();
            Assert.That(a.Morale, Is.EqualTo(70).Within(1e-9));
            a.Morale = 60;
            a.LastTargetedTime = sim.Time; // 今狙われた扱い
            for (int i = 0; i < 119; i++) sim.Step();
            Assert.That(a.Morale, Is.EqualTo(60).Within(1e-9), "60秒経つまでは回復しない");
            sim.Step();
            sim.Step();
            Assert.That(a.Morale, Is.GreaterThan(60));
        }

        [Test]
        public void SnapshotEveryTenSecondsHasRequiredFields()
        {
            var log = new StringEventSink();
            var sim = Scenarios.S3_2v1_front_flank().Build(TestUtil.Params, 3, log);
            for (int i = 0; i < 40; i++) sim.Step();
            var snaps = log.ToString().Split('\n').Where(l => l.Contains("\"type\":\"unit_snapshot\"")).ToArray();
            Assert.That(snaps.Length, Is.EqualTo(2 * 3)); // t=10, 20 の各3中隊
            foreach (var key in new[] { "\"t\":", "\"unit\":", "\"strength\":", "\"morale\":", "\"x\":", "\"y\":", "\"facing_deg\":",
                         "\"state\":", "\"target\":", "\"morale_causes\":{\"front\":" })
                StringAssert.Contains(key, snaps[0]);
            StringAssert.Contains("\"type\":\"first_flank_hit\",\"unit\":\"R\",\"by\":\"B\"", log.ToString());
            StringAssert.Contains("\"type\":\"encircled_start\",\"unit\":\"R\"", log.ToString());
        }
    }
}
