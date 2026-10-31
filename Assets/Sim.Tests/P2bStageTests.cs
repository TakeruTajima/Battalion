using System;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P2b：段階つき命令と信号弾（仕様「P2b：段階つき命令と信号弾（確定）」）。</summary>
    public class P2bStageTests
    {
        static readonly Vec2 Hq = new Vec2(0, 0);

        static Simulation Flat(IEventSink log = null, SimParams p = null)
        {
            p = p ?? TestUtil.Params;
            var sim = new Simulation(p, 1, TerrainMap.FlatCovering(p.Terrain, -1500, -1500, 1500, 1500), log);
            sim.SetHeadquarters(Side.Blue, Hq);
            return sim;
        }

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg) =>
            sim.AddCompany(name, side, Training.Regular, Personality.Calm, new Vec2(x, y), facingDeg);

        static OrderSheet Sheet(params (Order order, StageTrigger trigger)[] stages) =>
            new OrderSheet(stages.Select(s => new Stage(s.order, s.trigger)));

        static void RunUntil(Simulation sim, double t)
        {
            while (sim.Time < t - 1e-9) sim.Step();
        }

        // ---------------------------------------------------------------- 段の進行

        [Test]
        public void StagesAdvanceOnCompleteThenTimeWithTrainingDelay()
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var a = Add(sim, "A", Side.Blue, 25, 25, 0); // 通常：練度の遅れ 25秒
            var sheet = Sheet(
                (Order.Advance(a.Id, new Vec2(225, 25)), StageTrigger.None),
                (Order.Advance(a.Id, new Vec2(225, 225)), StageTrigger.Complete),
                (Order.Hold(a.Id, Math.PI), StageTrigger.At(600)));
            Assert.That(sim.SendSheet(sheet), Is.EqualTo(SendResult.Immediate), "会戦開始前は伝令なし");
            for (int k = 0; k < 2000 && a.StageIndex == 0; k++) sim.Step();
            double advancedAt = sim.Time;
            Assert.That(a.StageIndex, Is.EqualTo(1));
            Assert.That(Vec2.Distance(a.Position, new Vec2(225, 25)), Is.LessThan(1e-6), "第1段の目的地に着いて完了");
            RunUntil(sim, advancedAt + 24.5);
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Advance));
            Assert.That(a.CurrentOrder.Destination.Value.Y, Is.EqualTo(25), "練度の遅れの間は前の段のまま");
            sim.Step();
            Assert.That(a.CurrentOrder.Destination.Value.Y, Is.EqualTo(225), "25秒後に第2段を始める");

            RunUntil(sim, 600);
            Assert.That(a.StageIndex, Is.EqualTo(2), "時刻 600 で第3段へ");
            RunUntil(sim, 624.5);
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Advance));
            sim.Step();
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold), "時刻＋練度の遅れで第3段");
            StringAssert.Contains("\"reason\":\"complete\"", log.ToString());
            StringAssert.Contains("\"reason\":\"time\"", log.ToString());
        }

        [Test]
        public void FlareAdvancesAllCompaniesImmediatelyAndOnlyOncePerColor()
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var a = Add(sim, "A", Side.Blue, 25, 25, 0);
            var b = Add(sim, "B", Side.Blue, 25, 425, 0);
            foreach (var c in new[] { a, b })
                sim.SendSheet(Sheet((Order.Hold(c.Id), StageTrigger.None),
                    (Order.Advance(c.Id, new Vec2(225, c.Position.Y)), StageTrigger.OnFlare(FlareColor.Green))));
            RunUntil(sim, 100);
            Assert.That(a.StageIndex, Is.EqualTo(0));
            Assert.That(sim.FireFlare(Side.Blue, FlareColor.Green), Is.True);
            Assert.That(sim.FireFlare(Side.Blue, FlareColor.Green), Is.False, "各色1回だけ");
            Assert.That(sim.RunnerSlotsUsed(Side.Blue), Is.EqualTo(0), "判断枠を使わない");
            Assert.That(sim.Runners.Count, Is.EqualTo(0));
            sim.Step();
            Assert.That(a.StageIndex, Is.EqualTo(1), "次の刻みで全中隊に届く");
            Assert.That(b.StageIndex, Is.EqualTo(1));
            RunUntil(sim, 125.5); // 信号弾の次の刻み（100.5秒）＋練度の遅れ 25秒
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Advance), "練度の遅れの後に動く");
            StringAssert.Contains("\"reason\":\"flare_green\"", log.ToString());
            Assert.That(sim.FireFlare(Side.Blue, FlareColor.White), Is.True, "ほかの色は使える");
        }

        [Test]
        public void FlareFiredBeforeTheStageStartedStillCounts()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 600, 0, 0);
            sim.Step();
            sim.SendSheet(Sheet((Order.Hold(a.Id), StageTrigger.None), (Order.Hold(a.Id, Math.PI), StageTrigger.OnFlare(FlareColor.Red))));
            // 命令書を出した後、届く前に上がる（P2b.1 から、上がった後の色を条件にした命令書は発令できない）
            for (int k = 0; k < 20; k++) sim.Step();
            sim.FireFlare(Side.Blue, FlareColor.Red);
            for (int k = 0; k < 3000 && a.StageIndex == 0; k++) sim.Step();
            Assert.That(a.StageIndex, Is.EqualTo(1), "上がったことが残り、第1段が始まった時点で満たす");
        }

        // ---------------------------------------------------------------- 検査

        [TestCase(OrderType.Hold)]
        [TestCase(OrderType.Pin)]
        public void CompleteCannotFollowHoldOrPin(OrderType first)
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 25, 25, 0);
            var e = Add(sim, "E", Side.Red, 800, 25, 180);
            var o1 = first == OrderType.Hold ? Order.Hold(a.Id) : Order.Pin(a.Id, e.Id);
            var sheet = Sheet((o1, StageTrigger.None), (Order.Advance(a.Id, new Vec2(225, 25)), StageTrigger.Complete));
            Assert.That(OrderSheet.StageProblems(sheet.Stages, 3)[1], Is.Not.Null);
            Assert.That(sim.SendSheet(sheet), Is.EqualTo(SendResult.Invalid));
        }

        [Test]
        public void SwappingMakesTheNewFirstStageLoseItsTriggerAndTheOldFirstNeedOne()
        {
            var d = new OrderSheetDraft(0);
            d.Add(Order.Advance(0, new Vec2(100, 0)), 3);
            d.Add(Order.Advance(0, new Vec2(200, 0)), 3);
            d.SetTrigger(1, StageTrigger.OnFlare(FlareColor.Green));
            Assert.That(d.IsValid(3), Is.True);
            d.MoveUp(1);
            Assert.That(d.Stages[0].Trigger.Kind, Is.EqualTo(TriggerKind.None), "第1段になった段の条件は外れる");
            Assert.That(d.Stages[0].Order.Destination.Value.X, Is.EqualTo(200));
            Assert.That(d.Stages[1].Trigger.Kind, Is.EqualTo(TriggerKind.None));
            Assert.That(d.Problems(3)[1], Is.Not.Null, "条件のない第2段");
            Assert.That(d.IsValid(3), Is.False, "発令できない");
            d.SetTrigger(1, StageTrigger.At(300));
            Assert.That(d.IsValid(3), Is.True);
            Assert.That(d.Add(Order.Hold(0), 3), Is.True);
            Assert.That(d.Add(Order.Hold(0), 3), Is.False, "3段まで");
        }

        // ---------------------------------------------------------------- 置き換え・直し・取り消し

        [Test]
        public void NewSheetReplacesTheRemainingStagesOfTheOldOne()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 600, 0, 0);
            sim.SendSheet(Sheet((Order.Hold(a.Id), StageTrigger.None),
                (Order.Advance(a.Id, new Vec2(600, 300)), StageTrigger.OnFlare(FlareColor.White)),
                (Order.Hold(a.Id, 0), StageTrigger.Complete)));
            sim.Step();
            Assert.That(sim.SendOrder(Order.Hold(a.Id, Math.PI / 2)), Is.EqualTo(SendResult.Sent));
            for (int k = 0; k < 2000 && (a.ActiveSheet.Stages.Count != 1 || a.StartWaitOrder != null); k++) sim.Step();
            Assert.That(a.ActiveSheet.Stages.Count, Is.EqualTo(1), "古い命令書は段ごと置き換わる");
            sim.FireFlare(Side.Blue, FlareColor.White);
            for (int k = 0; k < 200; k++) sim.Step();
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold), "古い第2段は残っていない");
            Assert.That(a.CurrentOrder.FacingRad, Is.EqualTo(Math.PI / 2));
        }

        [Test]
        public void CancellingWhilePreparingFreesTheSlotAndNoRunnerLeaves()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 600, 0, 0);
            sim.Step();
            sim.SendOrder(Order.Hold(a.Id, Math.PI));
            var r = sim.Runners.Single();
            RunUntil(sim, sim.Time + 10);
            Assert.That(sim.CancelRunner(r.Id), Is.True);
            Assert.That(sim.RunnerSlotsUsed(Side.Blue), Is.EqualTo(0), "判断枠が空く");
            RunUntil(sim, sim.Time + 400);
            Assert.That(r.State, Is.EqualTo(RunnerState.Cancelled));
            Assert.That(r.Position, Is.EqualTo(Hq), "伝令は出ない");
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            Assert.That(a.CurrentOrder.FacingRad, Is.Null, "命令は届かない");
        }

        [Test]
        public void RevisingWhilePreparingDeliversTheNewSheetWithoutRestartingPrep()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 600, 0, 0);
            sim.Step();
            double t0 = sim.Time;
            sim.SendOrder(Order.Hold(a.Id, Math.PI));
            var r = sim.Runners.Single();
            RunUntil(sim, t0 + 20);
            Assert.That(sim.ReviseSheet(r.Id, OrderSheet.Single(Order.Hold(a.Id, Math.PI / 2))), Is.True);
            RunUntil(sim, t0 + 30.5);
            Assert.That(r.State, Is.EqualTo(RunnerState.Outbound), "準備の残り時間は数え直さない（発令から 30秒で出る）");
            Assert.That(sim.ReviseSheet(r.Id, OrderSheet.Single(Order.Hold(a.Id, 0))), Is.False, "出発後は直せない");
            Assert.That(sim.CancelRunner(r.Id), Is.False, "出発後は取り消せない");
            for (int k = 0; k < 2000 && (a.StartWaitOrder != null || !r.Delivered); k++) sim.Step();
            RunUntil(sim, sim.Time + 1);
            Assert.That(a.CurrentOrder.FacingRad, Is.EqualTo(Math.PI / 2), "直した内容が届く");
        }

        [Test]
        public void MultiStageSheetBeforeTheBattleArrivesWithoutARunner()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 600, 0, 0);
            var sheet = Sheet((Order.Advance(a.Id, new Vec2(600, 200)), StageTrigger.None),
                (Order.Hold(a.Id, 0), StageTrigger.Complete));
            Assert.That(sim.SendSheet(sheet), Is.EqualTo(SendResult.Immediate));
            Assert.That(sim.Runners.Count, Is.EqualTo(0));
            sim.Step();
            Assert.That(a.ActiveSheet, Is.SameAs(sheet));
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Advance), "最初の刻みから第1段");
        }

        // ---------------------------------------------------------------- 伝令の経路

        /// <summary>伝令が届くまでに、射界のマスにいた刻みの数。</summary>
        static int ExposedTicksUntilDelivered(Simulation sim, Runner r)
        {
            int n = 0;
            for (int k = 0; k < 4000 && !r.Delivered; k++)
            {
                sim.Step();
                if (r.State == RunnerState.Outbound && sim.Map.Contains(r.Position) && sim.ExposureFor(Side.Blue)[sim.Map.IndexOf(r.Position)]) n++;
            }
            return n;
        }

        [Test]
        public void RunnerDetoursAroundTheFireZone()
        {
            // 本部 (0,0) から 1000m 東の中隊へ。まっすぐの道の真ん中を、北の敵の射界（400m）が覆う
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 1000, 0, 0);
            Add(sim, "E", Side.Red, 500, 300, 270);
            sim.Step();
            var straight = Enumerable.Range(1, 99).Select(k => new Vec2(10 * k, 0))
                .Count(p => sim.ExposureFor(Side.Blue)[sim.Map.IndexOf(p)]) * 10 / 3.0 / sim.Params.TickSec;
            sim.SendOrder(Order.Hold(a.Id));
            int exposed = ExposedTicksUntilDelivered(sim, sim.Runners.Single());
            Assert.That(sim.Runners.Single().Delivered, Is.True);
            Assert.That(exposed, Is.LessThan(straight), $"射界にいた刻み {exposed}（まっすぐなら {straight:0}）");
        }

        [Test]
        public void RunnerCrossesTheRiverOnlyAtTheBridgeOrFord()
        {
            var sc = MapScenario.LoadFile(TestUtil.PathOf("maps/scenario_a1.json"));
            var sim = sc.Build(TestUtil.Params, 1);
            var b3 = sim.Find("B3");
            b3.Position = new Vec2(625, 125); // 川の南
            sim.Step();
            sim.SendOrder(Order.Hold(b3.Id));
            var r = sim.Runners.Single();
            bool crossedWater = false;
            for (int k = 0; k < 6000 && !r.Delivered; k++)
            {
                sim.Step();
                if (sim.Map.Contains(r.Position) && sim.Map.TerrainAt(r.Position) == TerrainMap.River) crossedWater = true;
            }
            Assert.That(r.Delivered, Is.True);
            Assert.That(crossedWater, Is.False, "川のマスには入らない（橋か浅瀬を渡る）");
        }

        [Test]
        public void RunnerChasesAMovingCompanyAndReplans()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 600, 0, 90);
            sim.SendOrder(Order.Advance(a.Id, new Vec2(600, 1200))); // 開始前の命令で北へ動いている
            sim.Step();
            sim.SendOrder(Order.Hold(a.Id));
            var r = sim.Runners.Single();
            int replans = 0;
            double last = double.NaN;
            for (int k = 0; k < 4000 && !r.Delivered; k++)
            {
                sim.Step();
                if (r.State == RunnerState.Outbound && r.NextReplan != last)
                {
                    replans++;
                    last = r.NextReplan;
                }
            }
            Assert.That(r.Delivered, Is.True, "追いついて渡す");
            Assert.That(replans, Is.GreaterThan(5), "10秒ごとに経路を引き直した");
            Assert.That(r.Position.Y, Is.GreaterThan(100), "中隊の動いた先で渡した");
        }

        // ---------------------------------------------------------------- 到着予定

        [Test]
        public void EstimatedArrivalIsWithinTenPercentOnFlatGround()
        {
            // 到着の見込みは隊形（F1 の縮合・展開・縦隊の速さ）を見ない。見込みの式そのものを、隊形なしで確かめる
            var sim = Flat(p: TestUtil.WithoutF1(TestUtil.ParamsWith()));
            var a = Add(sim, "A", Side.Blue, 25, 25, 0);
            var sheet = Sheet((Order.Advance(a.Id, new Vec2(625, 425)), StageTrigger.None),
                (Order.Advance(a.Id, new Vec2(1025, 425)), StageTrigger.Complete));
            var pv = sim.PreviewSheet(sheet.Stages, sim.Time);
            Assert.That(pv.Count, Is.EqualTo(2));
            Assert.That(pv.All(p => p.Kind == EtaKind.Absolute), Is.True);
            sim.SendSheet(sheet);
            double first = double.NaN;
            for (int k = 0; k < 4000; k++)
            {
                sim.Step();
                if (double.IsNaN(first) && a.StageIndex == 1) first = sim.Time;
                if (a.StageIndex == 1 && a.StageStarted && a.Arrived) break;
            }
            Assert.That(first, Is.EqualTo(pv[0].Arrive).Within(pv[0].Arrive * 0.1), "第1段の到着予定");
            Assert.That(sim.Time, Is.EqualTo(pv[1].Arrive).Within(pv[1].Arrive * 0.1), "第2段の到着予定（練度の遅れを含む）");
        }
    }
}
