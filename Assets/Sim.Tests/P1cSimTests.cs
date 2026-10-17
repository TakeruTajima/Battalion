using System;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P1c で Sim に足したもの：目的地のある陣地構築、表示用の射撃の記録。</summary>
    public class P1cSimTests
    {
        static Simulation Flat() =>
            new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -2000, -2000, 2000, 2000));

        static void Run(Simulation sim, double seconds)
        {
            int n = (int)Math.Round(seconds / sim.Params.TickSec);
            for (int i = 0; i < n; i++) sim.Step();
        }

        [Test]
        public void EntrenchWithDestinationMovesThenTurnsThenDigs()
        {
            var sim = Flat();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            sim.AddCompany("Far", Side.Red, Training.Regular, Personality.Calm, new Vec2(1900, 1900), 180);
            // 東へ 120m（100秒）、着いたら北（90°）へ向き直る（15秒）
            sim.Issue(Order.Entrench(a.Id, new Vec2(120, 0), 90 * Angles.DegToRad));
            Run(sim, 99);
            Assert.That(a.Arrived, Is.False);
            Assert.That(a.EntrenchProgress, Is.EqualTo(0), "着くまでは進捗が増えない");
            Run(sim, 2);
            Assert.That(a.Arrived, Is.True);
            Assert.That(a.Position.X, Is.EqualTo(120).Within(1e-9));
            Assert.That(a.EntrenchProgress, Is.EqualTo(0), "向き直っている間も増えない");
            Run(sim, 15);
            Assert.That(a.Facing, Is.EqualTo(Math.PI / 2).Within(1e-9));
            Run(sim, 10);
            // 100秒で着き、その刻みから旋回（90°÷毎秒6°＝15秒）を始めて 114.5秒で向き終える。今は 126秒なので 11.5秒掘った
            Assert.That(a.EntrenchProgress, Is.EqualTo(11.5).Within(1e-9), "向き終えたら掘り始める");
            Run(sim, 600);
            sim.Map.CellOf(a.Position, out int i, out int j);
            Assert.That(sim.Map.TerrainAt(i, j), Is.EqualTo(TerrainMap.Trench), "着いた地点に塹壕ができる");
        }

        [Test]
        public void EntrenchWithoutDestinationDigsInPlaceAsBefore()
        {
            var sim = Flat();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(25, 25), 0);
            sim.AddCompany("Far", Side.Red, Training.Regular, Personality.Calm, new Vec2(1900, 1900), 180);
            sim.Issue(Order.Entrench(a.Id));
            Assert.That(Order.Entrench(a.Id).Moves, Is.False);
            Run(sim, 60);
            Assert.That(a.EntrenchProgress, Is.EqualTo(60).Within(1e-9));
            Assert.That(a.Position.X, Is.EqualTo(25));
        }

        [Test]
        public void ShakenDuringTheMoveHoldsAndDoesNotDig()
        {
            var sim = Flat();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            sim.AddCompany("Far", Side.Red, Training.Regular, Personality.Calm, new Vec2(1900, 1900), 180);
            sim.Issue(Order.Entrench(a.Id, new Vec2(500, 0)));
            Run(sim, 10);
            a.Morale = 45; // 移動中に動揺する
            a.LastTargetedTime = sim.Time;
            Run(sim, 5);
            Assert.That(a.State, Is.EqualTo(CompanyState.Shaken));
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            Run(sim, 60);
            Assert.That(a.EntrenchProgress, Is.EqualTo(0));
        }

        [Test]
        public void ShakenRefusesEntrenchWithDestinationButMayDigInPlace()
        {
            var sim = Flat();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0, morale: 45);
            sim.AddCompany("Far", Side.Red, Training.Regular, Personality.Calm, new Vec2(1900, 1900), 180);
            sim.Step();
            Assert.That(a.State, Is.EqualTo(CompanyState.Shaken));
            sim.Issue(Order.Entrench(a.Id, new Vec2(300, 0)));
            sim.Step();
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            sim.Issue(Order.Entrench(a.Id));
            sim.Step();
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Entrench));
        }

        [Test]
        public void FireRecordsDescribeEachShotOfTheLastTick()
        {
            var sim = Scenarios.S3_2v1_front_flank().Build(TestUtil.Params, 1);
            int total = 0;
            for (int k = 0; k < 600; k++)
            {
                var r = sim.Find("R");
                int before = r.Strength;
                sim.Step();
                var shots = sim.LastTickFire;
                Assert.That(shots.Count, Is.EqualTo(3), "A・B・R がそれぞれ1回撃つ");
                var onR = shots.Where(s => s.Target == r.Id).ToList();
                Assert.That(onR.Count, Is.EqualTo(2));
                Assert.That(onR.Single(s => s.Shooter == sim.Find("A").Id).Aspect, Is.EqualTo(Aspect.Front));
                Assert.That(onR.Single(s => s.Shooter == sim.Find("B").Id).Aspect, Is.EqualTo(Aspect.Flank));
                Assert.That(before - r.Strength, Is.EqualTo(onR.Sum(s => s.Casualties)), "記録の損害の合計が実際の兵力の減りと一致");
                total += onR.Sum(s => s.Casualties);
                if (r.State != CompanyState.Normal && r.State != CompanyState.Shaken) break;
            }
            Assert.That(total, Is.GreaterThan(0));
            // 正面の損害の期待値：k×兵力（150 からの減りを含む）×距離倍率 1.0。毎分 2名前後
            var front = sim.LastTickFire.FirstOrDefault(s => s.Aspect == Aspect.Front);
            Assert.That(front.ExpectedPerMin, Is.GreaterThan(0.5).And.LessThan(3.0));
        }
    }
}
