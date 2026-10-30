using System;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P2c.1：撃てる目標がないときの向き（仕様「P2c.1：撃てる目標がないときの向きの直し（確定）」）。</summary>
    public class P2c1Tests
    {
        static Simulation Flat() =>
            new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -1500, -1500, 1500, 1500));

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg, int? strength = null) =>
            sim.AddCompany(name, side, Training.Regular, Personality.Calm, new Vec2(x, y), facingDeg, strength: strength);

        static void Run(Simulation sim, double seconds, params Company[] keep)
        {
            int n = (int)Math.Round(seconds / sim.Params.TickSec);
            for (int i = 0; i < n; i++)
            {
                foreach (var c in keep) c.Morale = 100;
                sim.Step();
            }
        }

        static double Deg(double rad) => rad * Angles.RadToDeg;

        /// <summary>
        /// 南から撃ってきた S が射程の外（南西 500m）へ下がった。北の 450m に撃っていない N がいて、そちらの方が近い。
        /// 直近 30秒に自分を撃った S の方を向く。
        /// </summary>
        [Test]
        public void RecentShooterIsPreferredOverACloserEnemyThatDidNotShoot()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var s = Add(sim, "S", Side.Red, 0, -350, 90, strength: 30);
            var n = Add(sim, "N", Side.Red, 0, 450, -90, strength: 30);
            sim.Issue(Order.Hold(a.Id, 0));
            Run(sim, 5, a, s, n);
            Assert.That(a.ShotBy.ContainsKey(s.Id), Is.True, "S に撃たれた");
            s.Position = new Vec2(-400, -300);
            Run(sim, 20, a, s, n);
            Assert.That(a.TargetId, Is.EqualTo(-1));
            Assert.That(Deg(a.AutoFacing.Value), Is.EqualTo(Deg(Math.Atan2(-300, -400))).Within(1e-6), "撃ってきた S の方");
        }

        /// <summary>
        /// 同じ場面で、今の正面（東）の 400m 以内に見える敵 F がいれば、撃ってきた S の方へ向き直らない。
        /// F は兵がいない（撃てる目標にならない）ので、撃てる目標がないまま正面に見える敵がいる場面になる。
        /// </summary>
        [Test]
        public void DoesNotTurnAwayWhenAVisibleEnemyIsAlreadyInTheFrontArc()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var s = Add(sim, "S", Side.Red, 0, -350, 90, strength: 30);
            var f = Add(sim, "F", Side.Red, 350, 0, 180, strength: 30);
            f.Strength = 0;
            sim.Issue(Order.Hold(a.Id, 0));
            Run(sim, 5, a, s, f);
            s.Position = new Vec2(-400, -300);
            Run(sim, 30, a, s, f);
            Assert.That(a.TargetId, Is.EqualTo(-1));
            Assert.That(a.AutoFacing, Is.Null, "正面の敵を捨てない");
            Assert.That(Deg(a.Facing), Is.EqualTo(0).Within(1e-6));
        }

        /// <summary>自分から見えない敵（森の向こう。味方からは見える）の方は向かない。</summary>
        [Test]
        public void DoesNotFaceAnEnemyOnlyFriendsCanSee()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 25, 25, 0);
            Add(sim, "B", Side.Blue, 325, 25, 0);
            var e = Add(sim, "E", Side.Red, 25, 525, -90);
            foreach (double y in new[] { 175.0, 225, 275 })
            {
                sim.Map.CellOf(new Vec2(25, y), out int i, out int j);
                sim.Map.SetOverlay(i, j, TerrainMap.Forest);
            }
            Assert.That(LineOfSight.Visible(sim.Map, sim.Params.Sight, a.Position, e.Position), Is.False, "A からは見えない");
            Assert.That(LineOfSight.Visible(sim.Map, sim.Params.Sight, new Vec2(325, 25), e.Position), Is.True, "B からは見える");
            Run(sim, 60);
            Assert.That(a.AutoFacing, Is.Null);
            Assert.That(Deg(a.Facing), Is.EqualTo(0).Within(1e-6));
        }

        /// <summary>撃てる目標がない間は 550m の敵の方を向き、撃てる目標ができたら命令の向き（東）に戻る。</summary>
        [Test]
        public void ReturnsToTheOrderFacingWhenATargetAppears()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var e = Add(sim, "E", Side.Red, 0, 550, -90, strength: 30);
            sim.Issue(Order.Hold(a.Id, 0));
            Run(sim, 60, a, e);
            Assert.That(Deg(a.Facing), Is.EqualTo(90).Within(1e-6), "550m の敵の方を向いた");
            e.Position = new Vec2(300, 250); // 射程に入る（約 391m）
            Run(sim, 2, a, e);
            Assert.That(a.TargetId, Is.EqualTo(e.Id));
            Assert.That(a.AutoFacing, Is.Null, "自律の向きを解く");
            // 接敵中は毎秒 movement.turn_deg_per_sec_engaged（R3。90° に約 131秒）で回る。回っている途中で自律行動の物陰へ寄る
            // 動き（向きを変えずに動く）が約1分挟まるので、長めに待つ
            Run(sim, 300, a, e);
            Assert.That(Deg(a.Facing), Is.EqualTo(0).Within(1e-6), "命令の向きに戻る");
        }
    }
}
