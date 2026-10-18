using System;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P2b.1：プレイの声を受けた直し（仕様「P2b.1」）。</summary>
    public class P2b1Tests
    {
        static Simulation Flat()
        {
            var sim = new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -1500, -1500, 1500, 1500));
            sim.SetHeadquarters(Side.Blue, new Vec2(0, 0));
            return sim;
        }

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg) =>
            sim.AddCompany(name, side, Training.Regular, Personality.Calm, new Vec2(x, y), facingDeg);

        // ---------------------------------------------------------------- 段の自動追加と空の段

        [Test]
        public void WritingTheLastStageAddsAnEmptyStageBelowUpToThree()
        {
            var d = new OrderSheetDraft(0);
            d.Write(Order.Advance(0, new Vec2(100, 0)), 3);
            Assert.That(d.Count, Is.EqualTo(2), "下に空の段が1つ自動で足される");
            Assert.That(d.Stages[1].Order, Is.Null);
            Assert.That(d.Stages[1].Trigger.Kind, Is.EqualTo(TriggerKind.Complete), "前進の後の初めの条件は「完了したら」");
            Assert.That(d.Selected, Is.EqualTo(1), "足した段が書き込む段になる");
            d.Write(Order.Hold(0), 3);
            Assert.That(d.Count, Is.EqualTo(3));
            Assert.That(d.Stages[2].Trigger.Kind, Is.EqualTo(TriggerKind.None), "保持の後は未設定");
            d.Write(Order.Hold(0, 0), 3);
            Assert.That(d.Count, Is.EqualTo(3), "3段まで");
            d.Selected = 0;
            d.Write(Order.Advance(0, new Vec2(50, 0)), 3);
            Assert.That(d.Count, Is.EqualTo(3), "途中の段を書き直しても足さない");
        }

        [Test]
        public void EmptyStagesAreDroppedAndCompactedOnDispatch()
        {
            var d = new OrderSheetDraft(0);
            d.Write(Order.Advance(0, new Vec2(100, 0)), 3);      // 第1段、空の第2段が足される
            d.Add(Order.Advance(0, new Vec2(200, 0)), 3);         // 第3段
            d.SetTrigger(2, StageTrigger.At(600));
            Assert.That(d.Count, Is.EqualTo(3));
            Assert.That(d.Stages[1].Order, Is.Null, "間に空の段");
            Assert.That(d.IsValid(3), Is.True, "空の段は条件が未設定でも発令を止めない");
            var sheet = d.ToSheet(3);
            Assert.That(sheet.Stages.Count, Is.EqualTo(2), "空の段を落として詰める");
            Assert.That(sheet.Stages[1].Order.Destination.Value.X, Is.EqualTo(200));
            Assert.That(sheet.Stages[1].Trigger.Kind, Is.EqualTo(TriggerKind.Time));

            // 第1段が空なら、詰めて第1段になった段の条件は外れる
            var e = new OrderSheetDraft(0);
            e.Add(null, 3);
            e.Add(Order.Advance(0, new Vec2(100, 0)), 3);
            e.SetTrigger(1, StageTrigger.OnFlare(FlareColor.Green));
            var s2 = e.ToSheet(3);
            Assert.That(s2.Stages.Count, Is.EqualTo(1));
            Assert.That(s2.Stages[0].Trigger.Kind, Is.EqualTo(TriggerKind.None));

            // 命令の入った段が1つもなければ発令できない
            var f = new OrderSheetDraft(0);
            f.Add(null, 3);
            Assert.That(f.IsValid(3), Is.False);
        }

        [Test]
        public void StageWithAnOrderButNoTriggerStillCannotBeDispatched()
        {
            var d = new OrderSheetDraft(0);
            d.Write(Order.Hold(0), 3);                         // 保持の後の空の段は条件が未設定
            d.Write(Order.Advance(0, new Vec2(100, 0)), 3);    // そこに命令を書く
            Assert.That(d.Stages[1].Trigger.Kind, Is.EqualTo(TriggerKind.None));
            Assert.That(d.Problems(3)[1], Is.Not.Null, "命令の入った段に条件がない");
            Assert.That(d.IsValid(3), Is.False);
            d.SetTrigger(1, StageTrigger.Complete);
            Assert.That(d.Problems(3)[1], Is.Not.Null, "保持の後に「完了したら」");
        }

        // ---------------------------------------------------------------- 命令待ち

        [Test]
        public void AwaitingOrdersBeforeStartAfterTheLastAdvanceButNotForHoldOrPin()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 25, 25, 0);
            var h = Add(sim, "H", Side.Blue, 25, 425, 0);
            var p = Add(sim, "P", Side.Blue, 25, -425, 0);
            var e = Add(sim, "E", Side.Red, 1200, -425, 180);
            Assert.That(sim.Companies.Where(c => c.Side == Side.Blue).All(c => sim.AwaitingOrders(c)), Is.True, "会戦開始前は全て命令待ち");

            sim.SendOrder(Order.Advance(a.Id, new Vec2(225, 25)));
            sim.SendOrder(Order.Hold(h.Id, 0));
            sim.SendOrder(Order.Pin(p.Id, e.Id));
            Assert.That(sim.AwaitingOrders(a), Is.False, "命令書を受けた");
            for (int k = 0; k < 2000 && (k == 0 || !a.Arrived); k++) sim.Step();
            Assert.That(a.Arrived, Is.True);
            Assert.That(sim.AwaitingOrders(a), Is.True, "前進の最後の段に着くと命令待ち");
            Assert.That(sim.AwaitingOrders(h), Is.False, "保持は終わらない");
            Assert.That(sim.AwaitingOrders(p), Is.False, "拘束は終わらない");

            sim.SendOrder(Order.Advance(a.Id, new Vec2(225, 225)));
            Assert.That(sim.AwaitingOrders(a), Is.True, "伝令が着くまでは命令待ちのまま");
            for (int k = 0; k < 2000 && sim.AwaitingOrders(a); k++) sim.Step();
            Assert.That(a.CurrentOrder.Destination.Value.Y, Is.EqualTo(225), "新しい命令書が始まると解ける");
            Assert.That(sim.AwaitingOrders(a), Is.False);
        }

        // ---------------------------------------------------------------- 使った色の信号弾

        [Test]
        public void SheetConditionedOnAUsedFlareCannotBeDispatched()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 600, 0, 0);
            sim.Step();
            sim.FireFlare(Side.Blue, FlareColor.Green);
            var sheet = new OrderSheet(new[]
            {
                new Stage(Order.Hold(a.Id), StageTrigger.None),
                new Stage(Order.Advance(a.Id, new Vec2(600, 300)), StageTrigger.OnFlare(FlareColor.Green)),
            });
            Assert.That(OrderSheet.StageProblems(sheet.Stages, 3, sim.FlareUsedBy(Side.Blue))[1], Is.Not.Null);
            Assert.That(sim.SendSheet(sheet), Is.EqualTo(SendResult.Invalid), "使用済みの色の条件は発令できない");

            // 準備中の命令書に残っていれば、その色のままの差し替えはできない
            var ok = new OrderSheet(new[]
            {
                new Stage(Order.Hold(a.Id), StageTrigger.None),
                new Stage(Order.Advance(a.Id, new Vec2(600, 300)), StageTrigger.OnFlare(FlareColor.White)),
            });
            Assert.That(sim.SendSheet(ok), Is.EqualTo(SendResult.Sent));
            sim.FireFlare(Side.Blue, FlareColor.White);
            var r = sim.Runners.Single();
            Assert.That(sim.ReviseSheet(r.Id, ok), Is.False, "使用済みの色のままでは差し替えられない");
            Assert.That(sim.ReviseSheet(r.Id, OrderSheet.Single(Order.Hold(a.Id, 0))), Is.True, "直せば差し替えられる");

            var d = OrderSheetDraft.CopyOf(sheet);
            Assert.That(d.Problems(3, sim.FlareUsedBy(Side.Blue))[1], Is.Not.Null, "起案中の段にも印");
        }
    }
}
