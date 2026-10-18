using System;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P2b.2：壊走・動揺と命令書（仕様「P2b.2」）。</summary>
    public class P2b2Tests
    {
        static Simulation Flat(IEventSink log = null)
        {
            var sim = new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -1500, -1500, 1500, 1500), log);
            sim.SetHeadquarters(Side.Blue, new Vec2(0, 0));
            return sim;
        }

        static Company Add(Simulation sim, string name, double x, double y, double facingDeg) =>
            sim.AddCompany(name, Side.Blue, Training.Regular, Personality.Calm, new Vec2(x, y), facingDeg);

        static OrderSheet Sheet(params (Order order, StageTrigger trigger)[] stages) =>
            new OrderSheet(stages.Select(s => new Stage(s.order, s.trigger)));

        /// <summary>士気を決めて1刻み進める（状態遷移はその刻みに起きる）。</summary>
        static void StepWithMorale(Simulation sim, Company c, double morale)
        {
            c.Morale = morale;
            sim.Step();
        }

        // ---------------------------------------------------------------- 壊走中・再集結中の中隊への命令

        [Test]
        public void SheetSentToARallyingCompanyStartsAfterItReturnsToCommand()
        {
            var sim = Flat();
            var a = Add(sim, "A", 600, 0, 0);
            sim.Step();
            a.State = CompanyState.Rallying;
            Assert.That(sim.SendOrder(Order.Advance(a.Id, new Vec2(600, 300))), Is.EqualTo(SendResult.Sent), "再集結中でも送れる");
            var r = sim.Runners.Single();
            for (int k = 0; k < 2000 && !r.Delivered; k++) StepWithMorale(sim, a, 20); // 再集結を続けさせる
            Assert.That(r.Delivered, Is.True);
            Assert.That(a.HeldSheet, Is.Not.Null, "預かる");
            Assert.That(a.ActiveSheet, Is.Null);
            Assert.That(a.State, Is.EqualTo(CompanyState.Rallying));
            for (int k = 0; k < 20; k++) StepWithMorale(sim, a, 20);
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold), "預かっている間は動かない");

            StepWithMorale(sim, a, 60); // 指揮下に戻る（正常）
            double back = sim.Time;
            Assert.That(a.UnderCommand, Is.True);
            Assert.That(a.HeldSheet, Is.Null);
            Assert.That(a.StartWaitOrder, Is.Not.Null, "練度の遅れの後に第1段");
            for (int k = 0; k < 200 && a.CurrentOrder.Type != OrderType.Advance; k++) StepWithMorale(sim, a, 60);
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Advance));
            Assert.That(sim.Time - back, Is.EqualTo(25).Within(0.6), "通常の練度の遅れ 25秒");
        }

        // ---------------------------------------------------------------- 壊走したときの命令書

        [Test]
        public void RoutAbortsTheSheetAndRallyLeavesTheCompanyAwaitingOrders()
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var a = Add(sim, "A", 600, 0, 0);
            sim.SendSheet(Sheet((Order.Advance(a.Id, new Vec2(600, 600)), StageTrigger.None),
                (Order.Advance(a.Id, new Vec2(900, 600)), StageTrigger.Complete)));
            sim.Step();
            Assert.That(a.ActiveSheet, Is.Not.Null);
            StepWithMorale(sim, a, 1); // 壊走
            Assert.That(a.State, Is.EqualTo(CompanyState.Routed));
            Assert.That(a.ActiveSheet, Is.Null, "命令書は残りの段ごと中断");
            StringAssert.Contains("\"type\":\"sheet_aborted\",\"unit\":\"A\"", log.ToString());

            a.State = CompanyState.Rallying;
            StepWithMorale(sim, a, 45); // 再集結を終えて指揮下へ（動揺）
            Assert.That(a.UnderCommand, Is.True);
            Assert.That(a.ActiveSheet, Is.Null);
            Assert.That(sim.AwaitingOrders(a), Is.True, "預かった命令書がなければ命令待ち");
            for (int k = 0; k < 400; k++) StepWithMorale(sim, a, 60);
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold), "古い計画を自分で再開しない");
        }

        // ---------------------------------------------------------------- 動揺で止まったときの命令書

        [Test]
        public void ShakenHaltedAdvanceResumesAfterRecoveryAndThenCompletes()
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var a = Add(sim, "A", 25, 25, 90);
            var goal = new Vec2(25, 425);
            sim.SendSheet(Sheet((Order.Advance(a.Id, goal), StageTrigger.None),
                (Order.Hold(a.Id, 0), StageTrigger.Complete)));
            for (int k = 0; k < 60; k++) StepWithMorale(sim, a, 100);
            StepWithMorale(sim, a, 45); // 動揺
            Assert.That(a.State, Is.EqualTo(CompanyState.Shaken));
            Assert.That(a.ShakenHalt, Is.True, "動揺で停止中");
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            Assert.That(a.StageIndex, Is.EqualTo(0), "今の段は捨てない");
            double y = a.Position.Y;
            for (int k = 0; k < 400; k++) StepWithMorale(sim, a, 45);
            Assert.That(a.Position.Y, Is.EqualTo(y), "停止中はその場");
            Assert.That(a.StageIndex, Is.EqualTo(0), "停止中に「完了したら」は満たされない");

            StepWithMorale(sim, a, 60); // 正常に戻る
            double back = sim.Time;
            Assert.That(a.ShakenHalt, Is.False);
            for (int k = 0; k < 200 && a.CurrentOrder.Type != OrderType.Advance; k++) StepWithMorale(sim, a, 60);
            Assert.That(sim.Time - back, Is.EqualTo(25).Within(0.6), "練度の遅れの後に再開");
            Assert.That(a.CurrentOrder.Destination.Value, Is.EqualTo(goal), "同じ目的地");
            for (int k = 0; k < 2000 && a.StageIndex == 0; k++) StepWithMorale(sim, a, 60);
            Assert.That(a.StageIndex, Is.EqualTo(1), "着けば「完了したら」が満たされる");
            Assert.That(Vec2.Distance(a.Position, goal), Is.LessThan(1e-6));
            StringAssert.Contains("\"type\":\"halt_resume\"", log.ToString());
        }

        [Test]
        public void ShakenHaltCanStillAdvanceOnTimeOrFlare()
        {
            var sim = Flat();
            var a = Add(sim, "A", 25, 25, 90);
            var b = Add(sim, "B", 425, 25, 90);
            sim.SendSheet(Sheet((Order.Advance(a.Id, new Vec2(25, 625)), StageTrigger.None),
                (Order.Hold(a.Id, 0), StageTrigger.OnFlare(FlareColor.Green))));
            sim.SendSheet(Sheet((Order.Advance(b.Id, new Vec2(425, 625)), StageTrigger.None),
                (Order.Advance(b.Id, new Vec2(825, 625)), StageTrigger.At(120))));
            for (int k = 0; k < 40; k++) { a.Morale = 100; b.Morale = 100; sim.Step(); }
            a.Morale = 45;
            b.Morale = 45;
            sim.Step();
            Assert.That(a.ShakenHalt && b.ShakenHalt, Is.True);

            sim.FireFlare(Side.Blue, FlareColor.Green);
            for (int k = 0; k < 400; k++) { a.Morale = 45; b.Morale = 45; sim.Step(); }
            Assert.That(a.StageIndex, Is.EqualTo(1), "信号弾で次の段へ");
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            Assert.That(a.ShakenHalt, Is.False, "次の段（保持）は拒まれない");
            Assert.That(b.StageIndex, Is.EqualTo(1), "時刻で次の段へ");
            Assert.That(b.ShakenHalt, Is.True, "次の段の前進は動揺の間は拒まれ、動揺で停止中");
            Assert.That(b.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));

            for (int k = 0; k < 200 && b.CurrentOrder.Type != OrderType.Advance; k++) { b.Morale = 60; sim.Step(); }
            Assert.That(b.CurrentOrder.Type, Is.EqualTo(OrderType.Advance), "正常に戻ったら第2段を始める");
            Assert.That(b.CurrentOrder.Destination.Value.X, Is.EqualTo(825));
        }
    }
}
