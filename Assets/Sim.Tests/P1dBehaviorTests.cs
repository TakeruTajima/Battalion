using System;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P1d：行動の規則（仕様「P1d：行動の規則（確定）」の1〜7）。</summary>
    public class P1dBehaviorTests
    {
        static Simulation Flat() =>
            new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -3000, -3000, 3000, 3000));

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg,
            Personality pers = Personality.Calm, double? morale = null) =>
            sim.AddCompany(name, side, Training.Regular, pers, new Vec2(x, y), facingDeg, morale: morale);

        static void Run(Simulation sim, double seconds)
        {
            int n = (int)Math.Round(seconds / sim.Params.TickSec);
            for (int i = 0; i < n; i++) sim.Step();
        }

        static double Deg(double rad) => rad * Angles.RadToDeg;

        /// <summary>撃たれて士気が動かないように（射程外の回復も止める）。</summary>
        static void Freeze(params Company[] cs)
        {
            foreach (var c in cs) c.Morale = 100;
        }

        // ---------------------------------------------------------------- 1. 攻撃の側と方角の固定

        [TestCase(false, -30)]
        [TestCase(true, -90)]
        public void FlankRangeIsReconsideredWhenTargetTurnsMapBearingKeeps(bool byBearing, double expectedDeg)
        {
            var sim = Flat();
            // 目標は西向き。攻撃側は真南にいる：側面の範囲に入っているので、今の方角（南）から攻める（P1e の4）
            var a = Add(sim, "A", Side.Blue, 1500, -1500, 90);
            var e = Add(sim, "E", Side.Red, 1500, 0, 180);
            sim.Issue(byBearing ? Order.AttackFrom(a.Id, e.Id, -Math.PI / 2) : Order.Attack(a.Id, e.Id, AttackAspect.Flank));
            sim.Step();
            Assert.That(Deg(a.AttackBearing), Is.EqualTo(-90).Within(1e-6));
            // 目標が北東寄り（80°）を向く：南は背面になる。範囲の指定なら、側面の端から 10° 内側のうち近い方（−30°）へ選び直す
            e.Facing = 80 * Angles.DegToRad;
            Run(sim, 31);
            Assert.That(Deg(Angles.Wrap(a.AttackBearing)), Is.EqualTo(expectedDeg).Within(1e-6));
            Assert.That(Vec2.Distance(a.MoveGoal, sim.JumpOff(e, a.AttackBearing)), Is.LessThan(1e-6));
        }

        [Test]
        public void AfterArrivalTheBearingIsFixedUntilTheTargetMoves()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var e = Add(sim, "E", Side.Red, 600, 0, 180); // 西向き：正面の発起点は (350, 0)
            sim.Issue(Order.Attack(a.Id, e.Id, AttackAspect.Front));
            for (int k = 0; k < 2400 && !a.AttackFixed; k++)
            {
                Freeze(a, e);
                sim.Step();
            }
            Assert.That(a.AttackFixed, Is.True);
            Assert.That(Math.Abs(Deg(a.AttackFixedBearing)), Is.EqualTo(180).Within(1e-6), "目標から見て西");
            var at = a.Position;

            // 目標が北を向いても回り込み直さない
            e.Facing = Math.PI / 2;
            for (int k = 0; k < 200; k++) { Freeze(a, e); sim.Step(); }
            Assert.That(Vec2.Distance(a.Position, at), Is.LessThan(1e-6));

            // 目標が 50m 以上動いたら、同じ方角の 250m の点へ動き直す
            e.Position = new Vec2(600, 120);
            for (int k = 0; k < 600; k++) { Freeze(a, e); sim.Step(); }
            Assert.That(Vec2.Distance(a.Position, new Vec2(350, 120)), Is.LessThan(1e-6));
        }

        // ---------------------------------------------------------------- 2. 敵の中隊とは重ならない

        [Test]
        public void MovingUnitStopsTenMetresShortOfEnemyRectangle()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var e = Add(sim, "E", Side.Red, 300, 0, 180);
            sim.Issue(Order.Advance(a.Id, new Vec2(300, 0))); // 目的地は敵の中心
            double minGap = double.MaxValue;
            for (int k = 0; k < 3000 && (k == 0 || !a.Arrived); k++)
            {
                Freeze(a, e);
                e.LastFlankHitTime = double.NegativeInfinity; // 敵が向き直って矩形が回らないように
                sim.Step();
                minGap = Math.Min(minGap, UnitRect.Gap(sim.RectOf(a), sim.RectOf(e)));
            }
            Assert.That(minGap, Is.GreaterThanOrEqualTo(10 - 1e-6));
            Assert.That(UnitRect.Gap(sim.RectOf(a), sim.RectOf(e)), Is.EqualTo(10).Within(0.05), "手前で止まる");
            Assert.That(a.Arrived, Is.True, "進めなくなったら着いたことにする");
        }

        [Test]
        public void PathGoesAroundEnemyRectangle()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var e = Add(sim, "E", Side.Red, 400, 0, 180);
            sim.Issue(Order.Advance(a.Id, new Vec2(800, 0)));
            double minGap = double.MaxValue;
            for (int k = 0; k < 3000 && !(a.Arrived && k > 5); k++)
            {
                Freeze(a, e);
                sim.Step();
                minGap = Math.Min(minGap, UnitRect.Gap(sim.RectOf(a), sim.RectOf(e)));
            }
            Assert.That(Vec2.Distance(a.Position, new Vec2(800, 0)), Is.LessThan(1e-6), "敵を回り込んで目的地に着く");
            Assert.That(minGap, Is.GreaterThanOrEqualTo(10 - 1e-6));
        }

        [Test]
        public void RoutedUnitCannotPassThroughEnemy()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0, morale: 10);
            var e = Add(sim, "E", Side.Red, 150, 0, 180);
            sim.Step(); // 壊走
            Assert.That(a.State, Is.EqualTo(CompanyState.Routed));
            a.FleeDirection = 0; // 敵の方へ逃げさせる
            a.FleeDirIndex = 0;
            a.NextEscapeCheckTime = double.PositiveInfinity;
            for (int k = 0; k < 200; k++) sim.Step();
            Assert.That(UnitRect.Gap(sim.RectOf(a), sim.RectOf(e)), Is.GreaterThanOrEqualTo(10 - 1e-6));
        }

        [Test]
        public void EscapeTowardEnemyRectangleIsBlockedButMovingAwayIsNot()
        {
            var sim = Flat();
            // 接近戦（隙間 0）でも、離れる方位はふさがれない
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            Add(sim, "E", Side.Red, 50, 0, 180);
            var est = sim.EstimateEscape(a);
            Assert.That(est.Loss[0], Is.EqualTo(1.0), "東（敵の方）はふさがる");
            Assert.That(est.Loss[8], Is.LessThan(1.0), "西（離れる方）は逃げられる");
        }

        // ---------------------------------------------------------------- 3. 拘束

        [Test]
        public void PinClosesTo125mAlongTheCurrentBearing()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, -400, 0, 0);
            var e = Add(sim, "E", Side.Red, 0, 0, 180); // こちらを向いている：隙間は 125−25−25＝75m
            sim.Issue(Order.Pin(a.Id, e.Id, PinRange.Near));
            for (int k = 0; k < 1200; k++) { Freeze(a, e); sim.Step(); }
            Assert.That(Vec2.Distance(a.Position, e.Position), Is.EqualTo(125).Within(1e-6));
            Assert.That(a.Position.Y, Is.EqualTo(0).Within(1e-6), "回り込まずに、今いる方角から詰める");
            Assert.That(sim.AspectOf(a, e.Position), Is.EqualTo(Aspect.Front));
            Assert.That(a.TargetId, Is.EqualTo(e.Id));
        }

        [Test]
        public void PinDoesNotCloseWhenAlreadyNearOrShaken()
        {
            var sim = Flat();
            var near = Add(sim, "N", Side.Blue, -100, 0, 0);
            var shaken = Add(sim, "S", Side.Blue, 0, -400, 90, morale: 45);
            var e = Add(sim, "E", Side.Red, 0, 0, 180);
            sim.Step();
            Assert.That(shaken.State, Is.EqualTo(CompanyState.Shaken));
            sim.Issue(Order.Pin(near.Id, e.Id, PinRange.Near));
            sim.Issue(Order.Pin(shaken.Id, e.Id, PinRange.Near));
            for (int k = 0; k < 120; k++) { near.Morale = 100; shaken.Morale = 45; e.Morale = 100; sim.Step(); }
            Assert.That(near.Position.X, Is.EqualTo(-100), "125m より近ければ下がらない");
            Assert.That(shaken.Position.Y, Is.EqualTo(-400), "動揺中は詰めない");
            Assert.That(shaken.CurrentOrder.Type, Is.EqualTo(OrderType.Pin));
            Assert.That(shaken.Facing, Is.EqualTo(Math.PI / 2).Within(1e-9), "目標を向いて撃つ");
        }

        // ---------------------------------------------------------------- 4. 追撃の許可

        /// <summary>A は E の正面 250m に着いた状態で攻撃している。E を壊走させて、A の判断を見る。</summary>
        static (Simulation sim, Company a, Company e) BrokenTarget(Personality pers, AttackOnBreak onBreak, bool enemyNear = false)
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0, pers);
            var e = Add(sim, "E", Side.Red, 250, 0, 180);
            if (enemyNear) Add(sim, "N", Side.Red, -50, 350, 270);
            sim.Issue(Order.Attack(a.Id, e.Id, AttackAspect.Front, onBreak: onBreak));
            for (int k = 0; k < 4; k++) { Freeze(a, e); sim.Step(); }
            Assert.That(a.AttackFixed, Is.True);
            e.Morale = 5; // 次の刻みで壊走
            sim.Step();
            Assert.That(e.State, Is.EqualTo(CompanyState.Routed));
            sim.Step();
            return (sim, a, e);
        }

        [TestCase(Personality.Cautious, AttackOnBreak.Pursue, false)]
        [TestCase(Personality.Calm, AttackOnBreak.Pursue, true)]
        [TestCase(Personality.Agile, AttackOnBreak.Pursue, true)]
        [TestCase(Personality.Reckless, AttackOnBreak.Pursue, true)]
        [TestCase(Personality.Reckless, AttackOnBreak.Secure, false)]
        public void PursuitDependsOnPermissionAndPersonality(Personality pers, AttackOnBreak onBreak, bool pursues)
        {
            var (_, a, _) = BrokenTarget(pers, onBreak);
            Assert.That(a.Pursuing, Is.EqualTo(pursues));
            if (!pursues) Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold), "確保：その場で保持");
            if (pers == Personality.Reckless && pursues) Assert.That(a.PursuitLimitM, Is.EqualTo(900).Within(1e-9));
        }

        [Test]
        public void CalmDoesNotPursueWithEnemyWithin400m()
        {
            var (_, a, _) = BrokenTarget(Personality.Calm, AttackOnBreak.Pursue, enemyNear: true);
            Assert.That(a.Pursuing, Is.False);
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            // 確保の向き：400m 以内の最も近い指揮下の敵（北の N）の方
            Assert.That(a.CurrentOrder.FacingRad.Value, Is.EqualTo(Math.Atan2(350, -50)).Within(1e-9));
        }

        [Test]
        public void SecureFacesTheWayTheTargetFledWhenNoEnemyIsNear()
        {
            var (_, a, e) = BrokenTarget(Personality.Cautious, AttackOnBreak.Pursue);
            Assert.That(a.CurrentOrder.FacingRad.Value, Is.EqualTo(e.FleeDirection.Value).Within(1e-9));
        }

        [Test]
        public void PursuerKeepsShootingAndStopsAtTheLimit()
        {
            var (sim, a, e) = BrokenTarget(Personality.Agile, AttackOnBreak.Pursue);
            var start = a.Position;
            bool fired = false;
            for (int k = 0; k < 6000 && a.Pursuing; k++)
            {
                a.Morale = 100;
                sim.Step();
                if (sim.LastTickFire.Any(f => f.Shooter == a.Id && f.Target == e.Id)) fired = true;
            }
            Assert.That(fired, Is.True, "追いながら撃つ");
            Assert.That(a.Pursuing, Is.False);
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold), "限界か、目標の再集結・離脱で確保に移る");
            double moved = Vec2.Distance(start, a.Position);
            Assert.That(moved, Is.LessThanOrEqualTo(600 + 1.0));
        }

        [Test]
        public void CalmStopsPursuingWhenAnEnemyAppearsWithin400m()
        {
            var (sim, a, _) = BrokenTarget(Personality.Calm, AttackOnBreak.Pursue);
            Assert.That(a.Pursuing, Is.True);
            Run(sim, 20);
            Add(sim, "N2", Side.Red, a.Position.X, a.Position.Y + 300, 270);
            sim.Step();
            Assert.That(a.Pursuing, Is.False);
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
        }

        // ---------------------------------------------------------------- 5. 向き直り

        static (Simulation sim, Company a) FlankedHolder(Personality pers, bool frontEnemy, double frontEnemyX = 300, Training training = Training.Regular)
        {
            var sim = Flat();
            var a = sim.AddCompany("A", Side.Blue, training, pers, new Vec2(0, 0), 0);
            Add(sim, "S", Side.Red, 0, 300, 270); // 北から撃つ：A（東向き）から見て側面
            if (frontEnemy) Add(sim, "F", Side.Red, frontEnemyX, 0, 180);
            return (sim, a);
        }

        [Test]
        public void HoldingCalmTurnsToTheFlankShooterAfterTheTrainingDelay()
        {
            var (sim, a) = FlankedHolder(Personality.Calm, false);
            for (int k = 0; k < 48; k++) { a.Morale = 100; sim.Step(); } // 24秒
            Assert.That(a.FacingOverride.HasValue, Is.False, "通常は 25秒待つ");
            for (int k = 0; k < 4; k++) { a.Morale = 100; sim.Step(); }
            Assert.That(a.FacingOverride.HasValue, Is.True);
            Assert.That(a.FacingOverride.Value, Is.EqualTo(Math.PI / 2).Within(1e-9));
            for (int k = 0; k < 40; k++) { a.Morale = 100; sim.Step(); } // 90°÷毎秒6°＝15秒
            Assert.That(a.Facing, Is.EqualTo(Math.PI / 2).Within(1e-9));
        }

        [Test]
        public void VeteranTurnsSooner()
        {
            var (sim, a) = FlankedHolder(Personality.Calm, false, training: Training.Veteran);
            for (int k = 0; k < 34; k++) { a.Morale = 100; sim.Step(); } // 17秒
            Assert.That(a.FacingOverride.HasValue, Is.True, "古参は 15秒");
        }

        [TestCase(Personality.Calm, 300, false)]     // 正面の 400m 以内に見える敵 → 向き直らない
        [TestCase(Personality.Cautious, 300, false)]
        [TestCase(Personality.Agile, 300, true)]     // 機敏は 150m 以内だけを見る
        [TestCase(Personality.Agile, 120, false)]
        [TestCase(Personality.Reckless, 0, false)]   // 猪突は向き直らない（正面は空）
        public void TurnConditionsByPersonality(Personality pers, double frontX, bool turns)
        {
            var (sim, a) = FlankedHolder(pers, frontX > 0, frontX);
            for (int k = 0; k < 120; k++) { a.Morale = 100; sim.Step(); }
            Assert.That(a.FacingOverride.HasValue, Is.EqualTo(turns));
        }

        [Test]
        public void PinOrderDoesNotTurn()
        {
            var (sim, a) = FlankedHolder(Personality.Calm, false);
            var far = Add(sim, "T", Side.Red, 1000, 1000, 180);
            sim.Issue(Order.Pin(a.Id, far.Id));
            for (int k = 0; k < 120; k++) { a.Morale = 100; sim.Step(); }
            Assert.That(a.FacingOverride.HasValue, Is.False);
        }

        [Test]
        public void WaitIsCancelledAfterTenQuietSeconds()
        {
            var (sim, a) = FlankedHolder(Personality.Calm, false);
            var s = sim.Find("S");
            for (int k = 0; k < 10; k++) { a.Morale = 100; sim.Step(); }
            Assert.That(a.TurnWaitStart.HasValue, Is.True);
            s.Position = new Vec2(0, 2000); // 撃ってこなくなる
            for (int k = 0; k < 22; k++) { a.Morale = 100; sim.Step(); } // 11秒
            Assert.That(a.TurnWaitStart.HasValue, Is.False);
            for (int k = 0; k < 60; k++) { a.Morale = 100; sim.Step(); }
            Assert.That(a.FacingOverride.HasValue, Is.False);
        }

        // ---------------------------------------------------------------- 6. 壊走中の味方のすり抜け

        [Test]
        public void RoutedPassesThroughFriendsWhoGetDisarrayedOnce()
        {
            var sim = Flat();
            var r = Add(sim, "R", Side.Blue, 0, 0, 0, morale: 10);
            var f = Add(sim, "F", Side.Blue, -150, 0, 0);
            var e = Add(sim, "E", Side.Red, 350, 0, 180);
            sim.Step(); // R が壊走
            Assert.That(r.State, Is.EqualTo(CompanyState.Routed));
            r.FleeDirection = Math.PI; // 西（F の方）へ
            r.FleeDirIndex = 8;
            r.NextEscapeCheckTime = double.PositiveInfinity;
            double before = f.Morale;
            bool slowed = false;
            for (int k = 0; k < 600; k++)
            {
                var p = r.Position;
                sim.Step();
                if (r.State != CompanyState.Routed) break;
                double step = Vec2.Distance(p, r.Position);
                if (step > 0 && Math.Abs(step - 1.8 * 0.5 * 0.5) < 1e-9) slowed = true;
            }
            Assert.That(slowed, Is.True, "重なっている間は ×0.5");
            Assert.That(f.Ledger.Disarray, Is.EqualTo(-5).Within(1e-9), "一度だけ −5");
            Assert.That(f.PassedBy.Contains(r.Id), Is.True);
            Assert.That(r.Position.X, Is.LessThan(-200), "すり抜けた（F の矩形の西の端を越えた）");
        }

        [Test]
        public void DisarrayHalvesFire()
        {
            var sim = Flat();
            var f = Add(sim, "F", Side.Blue, 0, 0, 0);
            Add(sim, "E", Side.Red, 300, 0, 180);
            sim.Step();
            double normal = sim.LastTickFire.Single(x => x.Shooter == f.Id).ExpectedPerMin;
            f.DisarrayUntil = sim.Time + 30;
            sim.Step();
            double disarrayed = sim.LastTickFire.Single(x => x.Shooter == f.Id).ExpectedPerMin;
            Assert.That(disarrayed / normal, Is.EqualTo(0.5).Within(0.02));
        }

        // ---------------------------------------------------------------- 7. 逃げる方位の揺れ止め

        [Test]
        public void FleeDirectionChangesOnlyWhenClearlyBetter()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0, morale: 10);
            var e = Add(sim, "E", Side.Red, 550, 0, 180); // 600m 以内（止まらない）だが、どの方位も損害 0
            sim.Step();
            Assert.That(a.State, Is.EqualTo(CompanyState.Routed));
            // 今の方位を北にしておく。同点の規則（敵の平均位置から遠ざかる）なら西を選ぶが、損害の差が 0 なので北のまま
            a.FleeDirIndex = 4;
            a.FleeDirection = Math.PI / 2;
            a.NextEscapeCheckTime = double.NegativeInfinity;
            sim.Step();
            Assert.That(a.FleeDirIndex, Is.EqualTo(4));
            // 北に撃つ敵を置くと、北の損害が 0.05 以上大きくなるので替える
            Add(sim, "N", Side.Red, 0, 380, 270);
            a.NextEscapeCheckTime = double.NegativeInfinity;
            sim.Step();
            Assert.That(a.FleeDirIndex, Is.Not.EqualTo(4));
        }
    }
}
