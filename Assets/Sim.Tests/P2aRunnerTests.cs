using System;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P2a：伝令（仕様「P2a：伝令（確定）」）。</summary>
    public class P2aRunnerTests
    {
        static readonly Vec2 Hq = new Vec2(0, 0);

        static Simulation Flat(IEventSink log = null, ulong seed = 1)
        {
            var sim = new Simulation(TestUtil.Params, seed, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -1500, -1500, 1500, 1500), log);
            sim.SetHeadquarters(Side.Blue, Hq);
            return sim;
        }

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg,
            Training training = Training.Regular) =>
            sim.AddCompany(name, side, training, Personality.Calm, new Vec2(x, y), facingDeg);

        /// <summary>会戦を始める（1刻み進める。開始前の命令は伝令を使わない）。</summary>
        static void Begin(Simulation sim) => sim.Step();

        static void RunUntil(Simulation sim, double t)
        {
            while (sim.Time < t - 1e-9) sim.Step();
        }

        // ---------------------------------------------------------------- 検算

        [Test]
        public void OrderTo600mArrivesIn230sAndStartsAfterTheTrainingDelay()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 600, 0, 0); // 通常：練度の遅れ 25秒
            Begin(sim);
            double t0 = sim.Time;
            Assert.That(sim.SendOrder(Order.Advance(a.Id, new Vec2(600, 300))), Is.EqualTo(SendResult.Sent));
            var r = sim.Runners.Single();

            RunUntil(sim, t0 + 229.5);
            Assert.That(r.Delivered, Is.False);
            sim.Step();
            Assert.That(sim.Time, Is.EqualTo(t0 + 230).Within(1e-9));
            Assert.That(r.Delivered, Is.True, "30＋600÷3＝230秒で届く");
            Assert.That(a.StartWaitOrder, Is.Not.Null);

            RunUntil(sim, t0 + 254.5);
            Assert.That(a.Position.Y, Is.EqualTo(0), "練度の遅れ（25秒）の間は動かない");
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            sim.Step();
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Advance), "届いてから 25秒で動き始める");
            Assert.That(a.Position.Y, Is.GreaterThan(0));
        }

        [Test]
        public void ReceiptReturnsAfterAbout430sAndFreesTheSlot()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 600, 0, 0);
            Begin(sim);
            double t0 = sim.Time;
            sim.SendOrder(Order.Hold(a.Id, Math.PI / 2));
            RunUntil(sim, t0 + 429.5);
            Assert.That(sim.RunnerSlotsUsed(Side.Blue), Is.EqualTo(1));
            sim.Step();
            Assert.That(sim.RunnerSlotsUsed(Side.Blue), Is.EqualTo(0), "往復 430秒で受領が戻り、判断枠が空く");
            Assert.That(sim.Runners.Single().State, Is.EqualTo(RunnerState.Done));
        }

        [Test]
        public void RunnerCrossingFireZoneFor100sSurvivesAbout74Percent()
        {
            // 本部から 300m の中隊へ。敵の射界が道のり全体を覆う（100秒）。台本の伝令（判断枠を数えない）を 2000人まとめて出す
            var sim = new Simulation(TestUtil.Params, 11, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -500, -500, 800, 800));
            sim.SetHeadquarters(Side.Blue, Hq);
            var a = Add(sim, "A", Side.Blue, 300, 0, 0);
            Add(sim, "E", Side.Red, 150, 250, 270);
            Begin(sim);
            Assert.That(sim.ExposureFor(Side.Blue)[sim.Map.IndexOf(new Vec2(150, 0))], Is.True, "道のりは射界");
            const int n = 2000;
            for (int k = 0; k < n; k++) sim.SendScriptOrder(Order.Hold(a.Id));
            RunUntil(sim, sim.Time + 131);
            int delivered = sim.Runners.Count(r => r.Delivered);
            double expected = Math.Pow(0.997, 100);
            Assert.That(delivered / (double)n, Is.EqualTo(expected).Within(0.03), $"届いた {delivered}/{n}（期待 {expected:F3}）");
        }

        // ---------------------------------------------------------------- 命令の受け渡し

        [Test]
        public void OldOrderContinuesUntilTheNewOneStarts()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 300, 0, 90);
            sim.SendOrder(Order.Advance(a.Id, new Vec2(300, 1200))); // 開始前：伝令なしで届く
            Begin(sim);
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Advance));
            sim.SendOrder(Order.Hold(a.Id));
            for (int k = 0; k < 200; k++)
            {
                sim.Step();
                if (a.StartWaitOrder == null && a.CurrentOrder.Type == OrderType.Hold) break;
                Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Advance), "届いて始まるまでは古い命令で動き続ける");
            }
            Assert.That(a.Position.Y, Is.GreaterThan(50), "その間も前進を続けていた");
        }

        [Test]
        public void LaterIssuedOrderWinsAndAStaleArrivalIsDiscarded()
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var a = Add(sim, "A", Side.Blue, 300, 0, 0);
            Begin(sim);
            sim.SendOrder(Order.Hold(a.Id, Math.PI / 2));      // 古い命令
            sim.SendOrder(Order.Hold(a.Id, -Math.PI / 2));     // 新しい命令
            var old = sim.Runners[0];
            // 古い命令の伝令を遅らせる（新しい伝令より後に着く）
            for (int k = 0; k < 100; k++)
            {
                sim.Step();
                if (old.State == RunnerState.Outbound) old.Position = Hq;
            }
            for (int k = 0; k < 1000 && !old.Delivered; k++) sim.Step();
            Assert.That(sim.Runners[1].Delivered, Is.True);
            Assert.That(old.Delivered, Is.True);
            RunUntil(sim, sim.Time + 60);
            Assert.That(a.CurrentOrder.FacingRad, Is.EqualTo(-Math.PI / 2), "後から出した命令が残る");
            StringAssert.Contains("\"stale\":true", log.ToString());
        }

        [Test]
        public void SlotsStopAtThree()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 300, 0, 0);
            Begin(sim);
            for (int k = 0; k < 3; k++) Assert.That(sim.SendOrder(Order.Hold(a.Id)), Is.EqualTo(SendResult.Sent));
            Assert.That(sim.SendOrder(Order.Hold(a.Id)), Is.EqualTo(SendResult.NoSlot));
            Assert.That(sim.RunnerSlotsUsed(Side.Blue), Is.EqualTo(3));
            Assert.That(sim.Runners.Count, Is.EqualTo(3), "出せなかった命令の伝令は出ない");
        }

        [Test]
        public void LostRunnerFreesTheSlotAtOnePointFiveTimesTheRoundTrip()
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var a = Add(sim, "A", Side.Blue, 600, 0, 0);
            Begin(sim);
            double t0 = sim.Time;
            sim.SendOrder(Order.Hold(a.Id));
            var r = sim.Runners.Single();
            RunUntil(sim, t0 + 100);
            r.State = RunnerState.Lost; // 倒れた（本部は知らない）
            double overdue = t0 + 1.5 * (30 + 2 * 600 / 3.0); // 645秒
            Assert.That(r.OverdueAt, Is.EqualTo(overdue).Within(1e-9));
            RunUntil(sim, overdue - 0.5);
            Assert.That(sim.RunnerSlotsUsed(Side.Blue), Is.EqualTo(1));
            Assert.That(sim.InFlightTo(a.Id, out _), Is.SameAs(r), "本部にとっては未着のまま");
            RunUntil(sim, overdue);
            Assert.That(sim.RunnerSlotsUsed(Side.Blue), Is.EqualTo(0), "受領報告なしで強制的に空く");
            StringAssert.Contains("\"type\":\"receipt_overdue\"", log.ToString());
            Assert.That(a.StartWaitOrder, Is.Null);
            Assert.That(r.Delivered, Is.False, "命令は届かない");
        }

        [Test]
        public void RunnerChasesAMovingCompany()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 600, 0, 90);
            sim.SendOrder(Order.Advance(a.Id, new Vec2(600, 1200))); // 開始前の命令で北へ動いている
            Begin(sim);
            double t0 = sim.Time;
            sim.SendOrder(Order.Hold(a.Id));
            var r = sim.Runners.Single();
            for (int k = 0; k < 2000 && !r.Delivered; k++) sim.Step();
            Assert.That(r.Delivered, Is.True, "追いついて渡す");
            Assert.That(sim.Time - t0, Is.GreaterThan(230), "動いている分だけ遅れる");
            Assert.That(r.Position.Y, Is.GreaterThan(100), "中隊の動いた先で渡した");
        }

        [Test]
        public void OrderIssuedWhilePausedHasTheSameDelay()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 600, 0, 0);
            RunUntil(sim, 100);
            // 一時停止中（刻みを進めない間）に出す
            sim.SendOrder(Order.Hold(a.Id));
            var r = sim.Runners.Single();
            Assert.That(r.SentTime, Is.EqualTo(100));
            RunUntil(sim, 329.5);
            Assert.That(r.Delivered, Is.False);
            RunUntil(sim, 330);
            Assert.That(r.Delivered, Is.True, "再開後に同じ 230秒で届く");
        }

        [Test]
        public void OrdersBeforeTheBattleStartsArriveImmediately()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 600, 0, 0);
            Assert.That(sim.BeforeStart, Is.True);
            Assert.That(sim.SendOrder(Order.Advance(a.Id, new Vec2(600, 300))), Is.EqualTo(SendResult.Immediate));
            Assert.That(sim.Runners.Count, Is.EqualTo(0));
            Assert.That(sim.RunnerSlotsUsed(Side.Blue), Is.EqualTo(0));
            sim.Step();
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Advance));
            Assert.That(sim.BeforeStart, Is.False);
            Assert.That(sim.SendOrder(Order.Hold(a.Id)), Is.EqualTo(SendResult.Sent), "時間が進み始めた後は伝令");
        }

        // ---------------------------------------------------------------- 予備隊の台本

        [Test]
        public void ReserveCounterattackArrivesByRunnerFromRedHeadquarters()
        {
            var log = new StringEventSink();
            var sc = MapScenario.LoadFile(TestUtil.PathOf("maps/scenario_a1.json"));
            var sim = sc.Build(TestUtil.Params, 1, log);
            var r3 = sim.Find("R3");
            var b1 = sim.Find("B1");
            b1.Position = new Vec2(1975, 1300); // 引き金の円（村の中心から 400m）の中
            sim.Step();
            var runner = sim.Runners.SingleOrDefault(r => r.CompanyId == r3.Id);
            Assert.That(runner, Is.Not.Null, "赤の本部が伝令を出した");
            Assert.That(runner.Side, Is.EqualTo(Side.Red));
            Assert.That(runner.CountsSlot, Is.False, "赤の判断枠は数えない");
            double d = Vec2.Distance(MapScenario.RedHeadquarters, r3.Position);
            double eta = runner.SentTime + 30 + d / 3.0;
            while (sim.Time < eta - 0.5 - 1e-9)
            {
                sim.Step();
                Assert.That(r3.CurrentOrder.Type, Is.Not.EqualTo(OrderType.Attack), "伝令が着くまで逆襲は始まらない");
            }
            for (int k = 0; k < 200 && r3.CurrentOrder.Type != OrderType.Attack; k++) sim.Step();
            Assert.That(runner.Delivered, Is.True);
            Assert.That(r3.CurrentOrder.Type, Is.EqualTo(OrderType.Attack));
            Assert.That(sim.Time, Is.EqualTo(eta + TestUtil.Params.Of(r3.Training).OrderStartSec).Within(1.0), "届いて練度の遅れの後に始まる");
        }
    }
}
