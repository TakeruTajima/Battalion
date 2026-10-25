using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    // R1：撃たれながら動く場面で、制圧（伏せて止まる）と関係のない動きを確かめるので、R1 の規則を止めたパラメータで回す。
    /// <summary>P1e：行動と命令の改善（仕様「P1e：行動と命令の改善（確定）」の1〜5）。</summary>
    public class P1eOrderTests
    {
        static Simulation Flat(IEventSink log = null) =>
            new Simulation(TestUtil.ParamsNoR1, 1, TerrainMap.FlatCovering(TestUtil.ParamsNoR1.Terrain, -3000, -3000, 3000, 3000), log);

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg,
            Personality pers = Personality.Calm, double? morale = null) =>
            sim.AddCompany(name, side, Training.Regular, pers, new Vec2(x, y), facingDeg, morale: morale);

        static double Deg(double rad) => rad * Angles.RadToDeg;

        /// <summary>撃たれて士気が動かないように。</summary>
        static void Step(Simulation sim)
        {
            foreach (var c in sim.Companies) c.Morale = 100;
            sim.Step();
        }

        static void Run(Simulation sim, double seconds)
        {
            int n = (int)Math.Round(seconds / sim.Params.TickSec);
            for (int i = 0; i < n; i++) Step(sim);
        }

        // ---------------------------------------------------------------- 1. 隊列の超越

        /// <summary>止まっている味方の横隊：x＝0 に、中心どうし 250m おきに6個（真ん中の2個は y＝±125）。回り込むと遠い。</summary>
        static (Company f1, Company f2) Line(Simulation sim)
        {
            Company f1 = null, f2 = null;
            foreach (double y in new[] { 625.0, 375, 125, -125, -375, -625 })
            {
                var f = Add(sim, "F" + y, Side.Blue, 0, y, 180);
                if (y == 125) f1 = f;
                if (y == -125) f2 = f;
            }
            return (f1, f2);
        }

        [Test]
        public void ReserveGoesThroughA250mGapBetweenStationaryFriendsToCounterattack()
        {
            var sim = Flat();
            var (f1, f2) = Line(sim);
            var m = Add(sim, "M", Side.Blue, 300, 0, 180);
            var e = Add(sim, "E", Side.Red, -700, 0, 0); // 味方の向こう（西）の敵
            sim.Issue(Order.Attack(m.Id, e.Id));
            for (int k = 0; k < 4000 && !m.AttackFixed; k++) Step(sim);
            Assert.That(m.AttackFixed, Is.True, "味方の間を抜けて発起点に着く");
            Assert.That(Vec2.Distance(m.Position, sim.JumpOff(e, 0)), Is.LessThan(1e-6), "今いる方角（東）の 250m");
            Assert.That(f1.Position.Y, Is.EqualTo(125));
            Assert.That(f2.Position.Y, Is.EqualTo(-125));
        }

        [Test]
        public void NoPushAndHalfSpeedOnlyWhileRectanglesOverlap()
        {
            var sim = Flat();
            var (f1, f2) = Line(sim);
            var m = Add(sim, "M", Side.Blue, 300, 0, 180);
            Add(sim, "Far", Side.Red, 2900, 2900, 180);
            sim.Issue(Order.Advance(m.Id, new Vec2(-400, 0)));
            bool sawPassing = false, sawOverlap = false;
            for (int k = 0; k < 4000 && (k == 0 || !m.Arrived); k++)
            {
                var before = m.Position;
                Step(sim);
                double moved = Vec2.Distance(before, m.Position);
                if (m.Passing.Count > 0) sawPassing = true;
                if (m.PassOverlap)
                {
                    sawOverlap = true;
                    Assert.That(moved, Is.LessThanOrEqualTo(0.6 * 0.5 + 1e-9), "重なっている間は ×0.5（押されて余分に動くこともない）");
                }
                else if (m.MoveStyle == MoveStyle.Forward)
                {
                    Assert.That(moved, Is.LessThanOrEqualTo(0.6 + 1e-9), "押し離しで余分に動かない");
                }
            }
            Assert.That(m.Arrived, Is.True);
            Assert.That(sawPassing, Is.True);
            Assert.That(sawOverlap, Is.True, "味方の矩形と重なって抜けた");
            Assert.That(f1.Position.Y, Is.EqualTo(125), "止まっている味方は押されない");
            Assert.That(f2.Position.Y, Is.EqualTo(-125));
        }

        [Test]
        public void PathAvoidsStationaryFriendZoneWhenTheDetourIsCheap()
        {
            // 味方が1個だけなら、その圏（×3）を避けて回る方が安い
            var sim = Flat();
            var f = Add(sim, "F", Side.Blue, 0, 0, 180);
            var m = Add(sim, "M", Side.Blue, 400, 0, 180);
            Add(sim, "Far", Side.Red, 2900, 2900, 180);
            sim.Issue(Order.Advance(m.Id, new Vec2(-400, 0)));
            double minDist = double.MaxValue;
            for (int k = 0; k < 4000 && (k == 0 || !m.Arrived); k++)
            {
                Step(sim);
                minDist = Math.Min(minDist, Vec2.Distance(m.Position, f.Position));
            }
            Assert.That(m.Arrived, Is.True);
            Assert.That(minDist, Is.GreaterThan(150), "味方の真ん中は抜けない");
        }

        // ---------------------------------------------------------------- 2. 動くときの向き

        [Test]
        public void InContactSidewaysMoveKeepsFacingAtHalfSpeedThenTurnsOnArrival()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 25, 25, 0);
            Add(sim, "E", Side.Red, 275, 25, 180); // 250m 東：接敵中（まわりのマスはどれも露出しているので、経路はまっすぐ南）
            sim.Issue(Order.Advance(a.Id, new Vec2(25, -125), -90 * Angles.DegToRad)); // 南（向きから 90°）。着いても敵から 400m 以内
            Step(sim);
            Assert.That(a.InContact, Is.True);
            Assert.That(a.MoveStyle, Is.EqualTo(MoveStyle.Sideways));
            Run(sim, 20);
            Assert.That(a.Facing, Is.EqualTo(0).Within(1e-12), "敵に横腹を向けない");
            Assert.That(a.Position.Y, Is.EqualTo(25 - 0.6 * 0.5 * 41).Within(1e-9), "横歩きは ×0.5");
            for (int k = 0; k < 2000 && !a.Arrived; k++) Step(sim);
            Assert.That(a.Arrived, Is.True);
            Assert.That(a.Facing, Is.EqualTo(0).Within(1e-12), "着くまで向きを変えない");
            Run(sim, 20);
            Assert.That(a.Facing, Is.EqualTo(-Math.PI / 2).Within(1e-9), "着いてから指定の向きへ");
        }

        [Test]
        public void InContactForwardKeepsNormalSpeed()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 25, 25, 0);
            Add(sim, "E", Side.Red, 375, 75, 180);
            sim.Issue(Order.Advance(a.Id, new Vec2(125, 25)));
            Step(sim);
            Assert.That(a.InContact, Is.True);
            Assert.That(a.MoveStyle, Is.EqualTo(MoveStyle.Forward));
            Assert.That(a.Position.X, Is.EqualTo(25.6).Within(1e-9));
        }

        [Test]
        public void OutOfContactDoesNotStopAtCorners()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 25, 25, 0);
            Add(sim, "Far", Side.Red, 2900, 2900, 180);
            sim.Issue(Order.Advance(a.Id, new Vec2(625, 325))); // 斜めと横の区間がある経路
            int corners = 0;
            double prevHeading = double.NaN;
            for (int k = 0; k < 4000 && (k == 0 || !a.Arrived); k++)
            {
                var before = a.Position;
                Step(sim);
                if (a.Arrived) break;
                var d = a.Position - before;
                Assert.That(d.Length, Is.GreaterThan(1e-9), "曲がり角でも止まらない");
                double h = Math.Atan2(d.Y, d.X);
                if (!double.IsNaN(prevHeading) && Math.Abs(Angles.Wrap(h - prevHeading)) > 0.1) corners++;
                prevHeading = h;
            }
            Assert.That(a.Arrived, Is.True);
            Assert.That(corners, Is.GreaterThan(0), "曲がり角のある経路だった");
        }

        [TestCase(1825 - 250, false)] // 頂上の塹壕から目の前の斜面は死角（MapBehaviourTests と同じ）：250m でも接敵ではない
        [TestCase(1825 - 375, true)]  // 西の平地からは見える
        public void EnemyBehindTheRidgeIsNotContact(double x, bool contact)
        {
            var s = MapScenario.LoadFile(TestUtil.PathOf("maps/scenario_a1.json"));
            var sim = new Simulation(TestUtil.ParamsNoR1, 1, TerrainMap.FromJson(s.Source, TestUtil.ParamsNoR1.Terrain));
            Add(sim, "R1", Side.Red, 1825, 1000, 180);
            var b = Add(sim, "B", Side.Blue, x, 1000, 0);
            sim.Issue(Order.Advance(b.Id, new Vec2(x, 700))); // 接敵の判定は動いている中隊だけ
            sim.Step();
            Assert.That(b.InContact, Is.EqualTo(contact));
        }

        // ---------------------------------------------------------------- 3. 拘束の距離

        [Test]
        public void FarPinStopsAt350mWithoutPinningEffect()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, -800, 0, 0);
            var e = Add(sim, "E", Side.Red, 0, 0, 180);
            sim.Issue(Order.Pin(a.Id, e.Id)); // 初期は遠い
            for (int k = 0; k < 2400; k++) Step(sim);
            Assert.That(Vec2.Distance(a.Position, e.Position), Is.EqualTo(350).Within(1e-6));
            Assert.That(a.Pinned || e.Pinned, Is.False, "350m は足止めの距離（150m）の外");
            Assert.That(a.TargetId, Is.EqualTo(e.Id), "撃ち合いの射程（400m）の内側で、目標を撃つ");
        }

        [Test]
        public void NearPinStopsAt125m()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, -800, 0, 0);
            var e = Add(sim, "E", Side.Red, 0, 0, 180);
            sim.Issue(Order.Pin(a.Id, e.Id, PinRange.Near));
            for (int k = 0; k < 2400; k++) Step(sim);
            Assert.That(Vec2.Distance(a.Position, e.Position), Is.EqualTo(125).Within(1e-6));
            Assert.That(e.Pinned, Is.True);
        }

        // ---------------------------------------------------------------- 4. 攻撃の方角

        [Test]
        public void FlankRangeFromSsoutWestAttacksFromTheCurrentBearing()
        {
            // 西を向いた敵に、南南西（地図の 247.5°。敵の向きから 67.5°：すでに側面の範囲）から側面を指定する
            var sim = Flat();
            double bearing = 247.5 * Angles.DegToRad;
            var e = Add(sim, "E", Side.Red, 0, 0, 180);
            var a = Add(sim, "A", Side.Blue, Math.Cos(bearing) * 1000, Math.Sin(bearing) * 1000, 67.5);
            sim.Issue(Order.Attack(a.Id, e.Id, AttackAspect.Flank));
            Step(sim);
            Assert.That(Angles.Wrap(a.AttackBearing - bearing), Is.EqualTo(0).Within(1e-9), "真南へ回り込まない");
            Assert.That(a.AttackChosenAspect, Is.EqualTo(AttackAspect.Flank));
            Assert.That(Vec2.Distance(a.MoveGoal, sim.JumpOff(e, bearing)), Is.LessThan(1e-6));
        }

        [Test]
        public void UnspecifiedFromBehindIsRear()
        {
            var sim = Flat();
            var e = Add(sim, "E", Side.Red, 0, 0, 180);
            var a = Add(sim, "A", Side.Blue, 900, 100, 180); // 敵の後ろ（東）
            sim.Issue(Order.Attack(a.Id, e.Id));
            Step(sim);
            Assert.That(a.AttackChosenAspect, Is.EqualTo(AttackAspect.Rear), "正面へ回り込まない");
            Assert.That(Angles.Wrap(a.AttackBearing - Math.Atan2(100, 900)), Is.EqualTo(0).Within(1e-9));
        }

        [TestCase(AttackAspect.Flank, 110)]
        [TestCase(AttackAspect.Rear, 50)]
        [TestCase(AttackAspect.Front, 170)]
        public void RangeOutsideCurrentBearingUsesTheNearestEdgeTenDegreesInside(AttackAspect range, double expectedDeg)
        {
            // 敵は西向き。攻撃側は地図の 170°（敵の向きから −10°：正面）にいる
            var sim = Flat();
            double b = 170 * Angles.DegToRad;
            var e = Add(sim, "E", Side.Red, 0, 0, 180);
            var a = Add(sim, "A", Side.Blue, Math.Cos(b) * 1000, Math.Sin(b) * 1000, -10);
            sim.Issue(Order.Attack(a.Id, e.Id, range));
            Step(sim);
            Assert.That(Deg(Angles.Wrap(a.AttackBearing)), Is.EqualTo(expectedDeg).Within(1e-6));
            Assert.That(a.AttackChosenAspect, Is.EqualTo(range));
        }

        [Test]
        public void MapBearingIsKeptThroughReplans()
        {
            var sim = Flat();
            var e = Add(sim, "E", Side.Red, 0, 0, 180);
            var a = Add(sim, "A", Side.Blue, -1000, 0, 0);
            sim.Issue(Order.AttackFrom(a.Id, e.Id, Math.PI / 2)); // 北から（敵の右の側面）
            Step(sim);
            Assert.That(a.AttackChosenAspect, Is.EqualTo(AttackAspect.Flank));
            e.Facing = 0; // 敵が東を向いても
            Run(sim, 65);
            Assert.That(Deg(a.AttackBearing), Is.EqualTo(90).Within(1e-9), "ドラッグの方角を守る");
            Assert.That(Vec2.Distance(a.MoveGoal, new Vec2(0, 250)), Is.LessThan(1e-6));
        }

        [Test]
        public void AgileTakesABetterAspectOnlyWhenCheapEnough()
        {
            // 機敏が真正面にいる。近ければ側面の端（±70°）だけが 1.5 倍以内、遠ければ背面も 1.5 倍以内になる（最も良い向きを選ぶ）
            foreach (var (x, expected) in new[] { (-700.0, AttackAspect.Flank), (-2500.0, AttackAspect.Rear) })
            {
                var sim = Flat();
                var e = Add(sim, "E", Side.Red, 0, 0, 180);
                var agile = Add(sim, "A", Side.Blue, x, 10, 0, Personality.Agile);
                sim.Issue(Order.Attack(agile.Id, e.Id));
                Step(sim);
                Assert.That(agile.AttackChosenAspect, Is.EqualTo(expected), $"x={x}");
            }

            // 機敏が側面にいれば、より悪い正面には替えない
            var sim2 = Flat();
            var e2 = Add(sim2, "E", Side.Red, 0, 0, 180);
            var side = Add(sim2, "A", Side.Blue, 0, -1500, 90, Personality.Agile);
            sim2.Issue(Order.Attack(side.Id, e2.Id));
            Step(sim2);
            Assert.That(side.AttackChosenAspect, Is.Not.EqualTo(AttackAspect.Front));
        }

        // ---------------------------------------------------------------- P1e.1 止められた中隊の動き直し

        [Test]
        public void PathKeepsHalfDiagonalPlusGapFromEnemyRectangle()
        {
            // 経路で敵を避ける範囲は、自分の矩形の半対角（約 103m）＋10m（P1e.1）。P1d の半幅＋10m（110m）なら通れた、
            // 敵の矩形から 108m の列（y＝25）を通らず、横歩きのまま角に引っかからずに着く
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 25, 25, 90);   // 北向きのまま東へ動く：横歩き
            var e = Add(sim, "E", Side.Red, 325, 158, 270); // 南向き：矩形の南の辺は y＝133
            var goal = new Vec2(625, 25);
            sim.Issue(Order.Advance(a.Id, goal));
            double clear = Math.Sqrt(200 * 200 + 50 * 50) / 2 + 10;
            double minDist = double.MaxValue;
            bool sideways = false, blocked = false;
            for (int k = 0; k < 4000 && (k == 0 || !a.Arrived); k++)
            {
                Step(sim);
                if (a.MoveStyle == MoveStyle.Sideways) sideways = true;
                if (a.BlockedByEnemy) blocked = true;
                minDist = Math.Min(minDist, sim.RectOf(e).DistanceTo(a.Position));
            }
            Assert.That(sideways, Is.True);
            Assert.That(blocked, Is.False, "角に引っかからない");
            Assert.That(a.Arrived, Is.True);
            Assert.That(Vec2.Distance(a.Position, goal), Is.LessThan(1e-6));
            Assert.That(minDist, Is.GreaterThanOrEqualTo(clear - 1e-6));
        }

        [Test]
        public void AttackBlockedShortOfTheJumpOffDoesNotFixTheBearing()
        {
            // 発起点の手前で別の敵に止められた攻撃は、方角を固定せず、道が空けば発起点まで進む
            var sim = Flat();
            var e = Add(sim, "E", Side.Red, 0, 0, 180);
            var x = Add(sim, "X", Side.Red, -360, 0, 0);  // 発起点（-250, 0）の手前をふさぐ敵
            var a = Add(sim, "A", Side.Blue, -440, 0, 0); // X のすぐ後ろ（隙間 30m）
            sim.Issue(Order.Attack(a.Id, e.Id, AttackAspect.Front));
            Run(sim, 40);
            Assert.That(a.AttackFixed, Is.False, "止められただけでは着いたことにしない");
            x.State = CompanyState.Surrendered;
            for (int k = 0; k < 4000 && !a.AttackFixed; k++) Step(sim);
            Assert.That(a.AttackFixed, Is.True);
            Assert.That(Vec2.Distance(a.Position, sim.JumpOff(e, Math.PI)), Is.LessThan(1e-6));
        }

        // ---------------------------------------------------------------- P1e.2 阻まれた中隊の向き直り

        [Test]
        public void UnitBlockedByEnemyTurnsWhenShotFromTheFlank()
        {
            // 北を向いたまま東へ横歩きする中隊が、東の敵の矩形の手前で阻まれる。その敵から側面を撃たれ、正面（北）は空いているので、
            // 練度の遅れ（通常 25秒）の後にその敵の方へ向き直る
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 90);
            var e = Add(sim, "E", Side.Red, 300, 0, 180);
            sim.Issue(Order.Advance(a.Id, new Vec2(300, 0)));
            for (int k = 0; k < 2000 && (k == 0 || !a.BlockedByEnemy); k++) Step(sim);
            Assert.That(a.BlockedByEnemy, Is.True);
            Assert.That(a.Arrived, Is.False);
            Assert.That(Deg(a.Facing), Is.EqualTo(90).Within(1e-9), "横歩きで向きは変えていない");
            for (int k = 0; k < 120 && !a.FacingOverride.HasValue; k++) Step(sim);
            Assert.That(a.FacingOverride.HasValue, Is.True, "阻まれて止まっていても向き直る");
            var toE = e.Position - a.Position;
            Assert.That(Angles.Wrap(a.FacingOverride.Value - Math.Atan2(toE.Y, toE.X)), Is.EqualTo(0).Within(1e-9), "撃ってきた敵の方へ");
            Run(sim, 35); // 最大 180° を毎秒 6° で 30秒
            Assert.That(Deg(a.Facing), Is.EqualTo(Deg(a.FacingOverride.Value)).Within(1e-9), "向き直り終えた");
            Assert.That(sim.AspectOf(a, e.Position), Is.EqualTo(Aspect.Front));
        }

        [Test]
        public void BlockedPinOrderDoesNotTurn()
        {
            // 拘束の命令中は、阻まれていても向き直らない（P1d と同じ）
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 90);
            var e = Add(sim, "E", Side.Red, 300, 0, 180);
            sim.Issue(Order.Pin(a.Id, e.Id, PinRange.Near));
            Run(sim, 300);
            Assert.That(a.FacingOverride.HasValue, Is.False);
        }

        // ---------------------------------------------------------------- 5. 下見

        [Test]
        public void OrderIssuedWhilePausedHasAPreviewPathImmediately()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var e = Add(sim, "E", Side.Red, 1500, 0, 180);
            Step(sim);
            var o = Order.Attack(a.Id, e.Id, AttackAspect.Flank);
            sim.Issue(o); // 刻みを進めない（一時停止中）
            Assert.That(sim.PendingOrderOf(a.Id), Is.SameAs(o));
            var pv = sim.Preview(o);
            Assert.That(pv.Stays, Is.False);
            Assert.That(pv.Path.Count, Is.GreaterThan(1));
            Assert.That(Vec2.Distance(pv.Path[0], a.Position), Is.LessThan(1e-9));
            Assert.That(pv.Aspect, Is.EqualTo(AttackAspect.Flank));
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold), "下見では中隊の状態を変えない");
            Assert.That(a.Path, Is.Null);

            // 次の刻みで受け付けると、下見と同じ目的地に向かう
            Step(sim);
            Assert.That(sim.PendingOrderOf(a.Id), Is.Null);
            Assert.That(Vec2.Distance(a.MoveGoal, pv.Goal), Is.LessThan(1e-9));
        }

        [Test]
        public void PreviewsDoNotChangeTheOutcome()
        {
            string Play(bool preview)
            {
                var log = new StringEventSink();
                var sim = Flat(log);
                var a = Add(sim, "A", Side.Blue, -900, 0, 0);
                var b = Add(sim, "B", Side.Blue, -900, 400, 0, Personality.Agile);
                var e = Add(sim, "E", Side.Red, 0, 0, 180);
                var f = Add(sim, "F", Side.Red, 0, 450, 180);
                for (int k = 0; k < 1200; k++)
                {
                    if (preview)
                    {
                        sim.Preview(Order.Attack(a.Id, e.Id, AttackAspect.Rear));
                        sim.Preview(Order.AttackFrom(b.Id, f.Id, 1.0));
                        sim.Preview(Order.Pin(b.Id, e.Id, PinRange.Near));
                        sim.Preview(Order.Advance(a.Id, new Vec2(-300, -300)));
                    }
                    if (k == 2)
                    {
                        sim.Issue(Order.Pin(a.Id, e.Id));
                        sim.Issue(Order.Attack(b.Id, f.Id, AttackAspect.Flank));
                    }
                    sim.Step();
                }
                return log.ToString();
            }
            Assert.That(Play(true), Is.EqualTo(Play(false)));
        }
    }
}
