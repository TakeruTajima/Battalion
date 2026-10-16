using System;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P1a の命令の挙動、陣地構築、重なりの禁止、予備隊の台本、地形と視線の効果。</summary>
    public class MapBehaviourTests
    {
        static Simulation Flat(double minX = -2000, double minY = -2000, double maxX = 2000, double maxY = 2000) =>
            new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(minX, minY, maxX, maxY));

        static Simulation OnStage(string stage)
        {
            var s = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_{stage}.json"));
            return new Simulation(TestUtil.Params, 1, TerrainMap.FromJson(s.Source));
        }

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg,
            Personality pers = Personality.Calm, double? morale = null) =>
            sim.AddCompany(name, side, Training.Regular, pers, new Vec2(x, y), facingDeg, morale: morale);

        static void Run(Simulation sim, double seconds)
        {
            int n = (int)Math.Round(seconds / sim.Params.TickSec);
            for (int i = 0; i < n; i++) sim.Step();
        }

        // ---------------------------------------------------------------- 後退の向き

        [Test]
        public void RetreatToRearArcBacksAwayWithoutTurning()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            Add(sim, "Far", Side.Red, 1900, 1900, 180);
            sim.Issue(Order.Retreat(a.Id, new Vec2(-200, -80))); // 向き（東）から約158°：背後の弧
            sim.Step();
            Assert.That(a.Backward, Is.True);
            Assert.That(a.Facing, Is.EqualTo(0).Within(1e-12));
            Assert.That(a.Position.X, Is.LessThan(0));
            Run(sim, 600);
            Assert.That(a.Arrived, Is.True);
            Assert.That(a.Facing, Is.EqualTo(0).Within(1e-12), "後ろ向きのまま下がり、向きは変わらない");
        }

        [Test]
        public void RetreatOutsideRearArcTurnsAndMovesLikeAdvance()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            Add(sim, "Far", Side.Red, 1900, 1900, 180);
            sim.Issue(Order.Retreat(a.Id, new Vec2(0, -300))); // 向きから 90°：側面。前進と同じ扱い
            sim.Step();
            Assert.That(a.Backward, Is.False);
            Assert.That(a.Position.Y, Is.EqualTo(0), "旋回し終えるまでは動かない");
            Run(sim, 15);
            Assert.That(a.Facing, Is.EqualTo(-Math.PI / 2).Within(1e-9));
            Run(sim, 10);
            Assert.That(a.Position.Y, Is.LessThan(-10), "速度は ×0.6 にならない（1.2m/s）");
            Assert.That(a.Position.Y, Is.EqualTo(-1.2 * 10).Within(1.2));
        }

        // ---------------------------------------------------------------- 陣地構築

        [Test]
        public void EntrenchTakesTenMinutesAndDigsFiveCellsAcrossTheFront()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 25, 25, 0); // 東向き：左右は南北
            Add(sim, "Far", Side.Red, 1900, 1900, 180);
            sim.Map.CellOf(a.Position, out int ci, out int cj);
            sim.Issue(Order.Entrench(a.Id));
            Run(sim, 300);
            Assert.That(a.EntrenchProgress, Is.EqualTo(300).Within(1e-9));
            Assert.That(sim.TerrainDamageMult(a), Is.EqualTo(1.0 + (0.3 - 1.0) * 0.5).Within(1e-12), "途中は進捗に比例して 0.3 へ");
            Assert.That(sim.Map.TerrainAt(ci, cj), Is.EqualTo(TerrainMap.Plain));
            Run(sim, 300);
            for (int k = -2; k <= 2; k++)
                Assert.That(sim.Map.TerrainAt(ci, cj + k), Is.EqualTo(TerrainMap.Trench), $"中心から {k} マス");
            Assert.That(sim.Map.TerrainAt(ci + 1, cj), Is.EqualTo(TerrainMap.Plain));
            Assert.That(sim.Map.TerrainAt(ci, cj + 3), Is.EqualTo(TerrainMap.Plain));
            Assert.That(sim.TerrainDamageMult(a), Is.EqualTo(0.3).Within(1e-12));
            Assert.That(a.Fortified, Is.True, "構築した塹壕の中は籠城");
        }

        [Test]
        public void MovingLosesEntrenchProgressAndTrenchCoverOnlyWhenStill()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 25, 25, 0);
            Add(sim, "Far", Side.Red, 1900, 1900, 180);
            sim.Issue(Order.Entrench(a.Id));
            Run(sim, 120);
            Assert.That(a.EntrenchProgress, Is.GreaterThan(0));
            sim.Issue(Order.Advance(a.Id, new Vec2(225, 25)));
            sim.Step();
            sim.Step();
            Assert.That(a.MovedThisTick, Is.True);
            Assert.That(a.EntrenchProgress, Is.EqualTo(0), "動くと進捗は失われる");

            // 塹壕のマスでも、動いている刻みは 0.3 にならない
            sim.Map.CellOf(a.Position, out int i, out int j);
            sim.Map.SetOverlay(i, j, TerrainMap.Trench);
            Assert.That(sim.TerrainDamageMult(a), Is.EqualTo(1.0));
            a.MovedThisTick = false;
            Assert.That(sim.TerrainDamageMult(a), Is.EqualTo(0.3));
        }

        [Test]
        public void EntrenchOnlyConvertsPlainAndCraterCells()
        {
            var sim = OnStage("a1");
            // 村の西端のマス (38, 19) の中心。北向きなら左右は東西：(36..40, 19)。36 は基本の塹壕、37 は平地、38〜40 は村
            var a = Add(sim, "A", Side.Blue, 38 * 50 + 25, 19 * 50 + 25, 90);
            var cells = sim.EntrenchCells(a).ToArray();
            Assert.That(cells, Is.EquivalentTo(new[] { (36, 19), (37, 19), (38, 19), (39, 19), (40, 19) }));
            sim.Map.SetOverlay(37, 19, TerrainMap.Crater);
            sim.Issue(Order.Entrench(a.Id));
            Run(sim, 600);
            Assert.That(sim.Map.TerrainAt(37, 19), Is.EqualTo(TerrainMap.Trench), "砲撃痕は塹壕になる");
            Assert.That(sim.Map.TerrainAt(38, 19), Is.EqualTo(TerrainMap.Village), "村は変えない");
            Assert.That(sim.Map.TerrainAt(40, 19), Is.EqualTo(TerrainMap.Village));
        }

        // ---------------------------------------------------------------- 重なりの禁止

        [Test]
        public void DestinationTooCloseToFriendIsShifted()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var b = Add(sim, "B", Side.Blue, 500, 0, 0);
            Add(sim, "Far", Side.Red, 1900, 1900, 180);
            sim.Issue(Order.Advance(a.Id, new Vec2(450, 0)));
            sim.Step();
            Assert.That(Vec2.Distance(a.MoveGoal, b.Position), Is.GreaterThanOrEqualTo(200 - 1e-9));
            Assert.That(Vec2.Distance(a.MoveGoal, new Vec2(450, 0)), Is.LessThanOrEqualTo(200), "元の目的地のなるべく近く");
        }

        [Test]
        public void MoverIsPushedButStationaryFriendIsNot()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var b = Add(sim, "B", Side.Blue, 300, 0, 0);
            Add(sim, "Far", Side.Red, 1900, 1900, 180);
            // 目的地は B から 200m 以上離れているが、経路が B の脇 150m を通る
            sim.Issue(Order.Advance(a.Id, new Vec2(600, 150)));
            double minDist = double.MaxValue;
            for (int i = 0; i < 1200; i++)
            {
                sim.Step();
                minDist = Math.Min(minDist, Vec2.Distance(a.Position, b.Position));
            }
            Assert.That(b.Position.X, Is.EqualTo(300), "止まっている B は押されない");
            Assert.That(b.Position.Y, Is.EqualTo(0));
            Assert.That(minDist, Is.GreaterThanOrEqualTo(200 - 1e-6));
        }

        [Test]
        public void PathGoesAroundGapBetweenStationaryFriends()
        {
            // 止まっている2個中隊の間（250m）は通れない（押されないので挟まって動けなくなる）。経路はその外を回る
            var sim = Flat();
            var f1 = Add(sim, "F1", Side.Blue, 0, 125, 180);
            var f2 = Add(sim, "F2", Side.Blue, 0, -125, 180);
            var m = Add(sim, "M", Side.Blue, 300, 0, 180);
            Add(sim, "Far", Side.Red, 1900, 1900, 180);
            sim.Issue(Order.Advance(m.Id, new Vec2(-400, 0)));
            double minDist = double.MaxValue;
            for (int i = 0; i < 2400 && (i == 0 || !m.Arrived); i++)
            {
                sim.Step();
                minDist = Math.Min(minDist, Math.Min(Vec2.Distance(m.Position, f1.Position), Vec2.Distance(m.Position, f2.Position)));
            }
            Assert.That(m.Arrived, Is.True, "挟まらずに目的地に着く");
            Assert.That(Vec2.Distance(m.Position, new Vec2(-400, 0)), Is.LessThan(1e-6));
            Assert.That(minDist, Is.GreaterThanOrEqualTo(200 - 1e-6));
            Assert.That(f1.Position.Y, Is.EqualTo(125));
            Assert.That(f2.Position.Y, Is.EqualTo(-125));
        }

        [Test]
        public void TwoMoversArePushedHalfEach()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, -300, 0, 0);
            var b = Add(sim, "B", Side.Blue, 300, 0, 180);
            Add(sim, "Far", Side.Red, 1900, 1900, 180);
            // 互いの目的地は 200m 以上離れているが、すれ違う
            sim.Issue(Order.Advance(a.Id, new Vec2(400, 0)));
            sim.Issue(Order.Advance(b.Id, new Vec2(-400, 0)));
            double minDist = double.MaxValue;
            for (int i = 0; i < 400; i++)
            {
                sim.Step();
                minDist = Math.Min(minDist, Vec2.Distance(a.Position, b.Position));
            }
            Assert.That(minDist, Is.GreaterThanOrEqualTo(200 - 1e-6));
        }

        // ---------------------------------------------------------------- 攻撃

        [Test]
        public void JumpOffPointsAre250mFromTheEnemyByItsFacing()
        {
            var sim = Flat();
            var e = Add(sim, "E", Side.Red, 1000, 0, 180); // 西向き
            Assert.That(Dist(Simulation.JumpOff(e, AttackAspect.Front, 0), new Vec2(750, 0)), Is.LessThan(1e-9));
            Assert.That(Dist(Simulation.JumpOff(e, AttackAspect.Rear, 0), new Vec2(1250, 0)), Is.LessThan(1e-9));
            Assert.That(Dist(Simulation.JumpOff(e, AttackAspect.Flank, +1), new Vec2(1000, -250)), Is.LessThan(1e-9), "西向きの左は南");
            Assert.That(Dist(Simulation.JumpOff(e, AttackAspect.Flank, -1), new Vec2(1000, 250)), Is.LessThan(1e-9));
        }

        static double Dist(Vec2 a, Vec2 b) => Vec2.Distance(a, b);

        [TestCase(Personality.Calm, AttackAspect.Front)]
        [TestCase(Personality.Agile, AttackAspect.Flank)]
        public void UnspecifiedAttackIsFrontalExceptAgileTakesCheapFlank(Personality pers, AttackAspect expected)
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0, pers);
            var e = Add(sim, "E", Side.Red, 1000, 0, 180);
            sim.Issue(Order.Attack(a.Id, e.Id));
            sim.Step();
            Assert.That(a.AttackChosenAspect, Is.EqualTo(expected));
        }

        [Test]
        public void AttackReachesJumpOffAndFacesTheEnemy()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var e = Add(sim, "E", Side.Red, 1000, 0, 90); // 北向き：正面の発起点は (1000, 250)
            sim.Issue(Order.Attack(a.Id, e.Id, AttackAspect.Front));
            Run(sim, 1500);
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Attack));
            Assert.That(a.Arrived, Is.True);
            Assert.That(Vec2.Distance(a.Position, new Vec2(1000, 250)), Is.LessThan(1e-6));
            Assert.That(sim.AspectOf(a, e.Position), Is.EqualTo(Aspect.Front), "着いたら敵に正面を向ける");
            Assert.That(a.Facing, Is.EqualTo(-Math.PI / 2).Within(1e-9));
        }

        [Test]
        public void ShakenRefusesAttack()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0, morale: 45);
            var e = Add(sim, "E", Side.Red, 1500, 0, 180);
            sim.Step();
            Assert.That(a.State, Is.EqualTo(CompanyState.Shaken));
            sim.Issue(Order.Attack(a.Id, e.Id));
            Run(sim, 10);
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            Assert.That(a.Position.X, Is.EqualTo(0));
        }

        [Test]
        public void PinFacesTheTargetAndFiresAtIt()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            Add(sim, "Near", Side.Red, 250, 0, 180);  // 正面の近い敵
            var t = Add(sim, "T", Side.Red, 0, 300, 180); // 北の敵（拘束の目標）
            sim.Issue(Order.Pin(a.Id, t.Id));
            Run(sim, 20);
            Assert.That(a.Facing, Is.EqualTo(Math.PI / 2).Within(1e-9), "目標に正面を向ける");
            Assert.That(a.TargetId, Is.EqualTo(t.Id), "近い敵がいても拘束の目標を撃つ");
            Assert.That(a.Position.X, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- 視線と地形

        [Test]
        public void CannotTargetEnemyBehindTheRidge()
        {
            var sim = OnStage("a1");
            var r1 = Add(sim, "R1", Side.Red, 1825, 1000, 180);
            var b = Add(sim, "B", Side.Blue, 1825 - 250, 1000, 0); // L8：頂上の塹壕から目の前の斜面は死角
            Assert.That(Vec2.Distance(r1.Position, b.Position), Is.LessThanOrEqualTo(400));
            Assert.That(sim.SelectTarget(r1), Is.Null);
            Assert.That(sim.SelectTarget(b), Is.Null);
            b.Position = new Vec2(1825 - 375, 1000); // L2：西の平地からは見える
            Assert.That(sim.SelectTarget(r1), Is.SameAs(b));
        }

        [Test]
        public void TerrainSetsDamageMoveAndFortified()
        {
            var sim = OnStage("a1");
            var v = Add(sim, "V", Side.Red, 2025, 1000, 180); // 村
            var f = Add(sim, "F", Side.Blue, 1000, 1650, 0);  // 北の森
            var t = Add(sim, "T", Side.Red, 1825, 750, 180);  // 稜線の塹壕（基本の地形、y 700〜1300）
            sim.Step();
            Assert.That(sim.TerrainDamageMult(v), Is.EqualTo(0.5));
            Assert.That(sim.TerrainDamageMult(f), Is.EqualTo(0.6));
            Assert.That(sim.TerrainDamageMult(t), Is.EqualTo(0.3));
            Assert.That(v.Fortified, Is.True);
            Assert.That(t.Fortified, Is.True);
            Assert.That(f.Fortified, Is.False);

            // 森の中は秒速 0.6m
            sim.Issue(Order.Advance(f.Id, new Vec2(1300, 1650)));
            Run(sim, 60);
            Assert.That(f.Position.X, Is.EqualTo(1000 + 0.6 * 60).Within(1e-6));
        }

        // ---------------------------------------------------------------- 退路の見積もり（地図）

        [Test]
        public void EscapeTowardRiverIsBlocked()
        {
            var sim = OnStage("a1");
            var a = Add(sim, "A", Side.Blue, 1500, 400, 0); // 川（y 250〜350）のすぐ北。浅瀬（x 1000〜1050）と橋（x 2000〜2050）から離れた所
            Add(sim, "E", Side.Red, 1800, 400, 180);
            var est = sim.EstimateEscape(a);
            Assert.That(est.Loss[12], Is.EqualTo(1.0), "真南は川に突き当たる");
            Assert.That(est.Loss[4], Is.LessThan(0.2), "北へは逃げられる");
        }

        [Test]
        public void EscapeOffTheMapStopsCountingAtTheEdge()
        {
            var sim = new Simulation(TestUtil.Params, 1, TerrainMap.Flat(0, 0, 60, 40));
            var a = Add(sim, "A", Side.Blue, 100, 1000, 0);
            Add(sim, "E", Side.Red, 200, 1000, 180);
            var est = sim.EstimateEscape(a);
            // 西へ 1.8m×55歩で x=1。56歩目で地図の外。150m 以内（27歩まで）は ×1.3
            double rate = 0.0133 / 60 * 150;
            double expected = (27 * 1.3 + 28 * 1.0) * rate * 2 / 150;
            Assert.That(est.Loss[8], Is.EqualTo(expected).Within(1e-12));
        }

        [Test]
        public void EscapeCountsOnlyEnemiesThatCanSeeThePoint()
        {
            // 頂上の塹壕（R1）の目の前の斜面は死角。同じ配置の平地より見積もり損害が小さい
            var hill = OnStage("a1");
            var a = Add(hill, "A", Side.Blue, 1825 - 250, 1000, 0);
            Add(hill, "R1", Side.Red, 1825, 1000, 180);
            var flat = new Simulation(TestUtil.Params, 1, TerrainMap.Flat(0, 0, 60, 40));
            var b = Add(flat, "A", Side.Blue, 1825 - 250, 1000, 0);
            Add(flat, "R1", Side.Red, 1825, 1000, 180);
            double onHill = hill.EstimateEscape(a).Loss[4], onFlat = flat.EstimateEscape(b).Loss[4]; // 北へ逃げる
            TestContext.WriteLine($"北へ逃げる見積もり：稜線 {onHill:F4}、平地 {onFlat:F4}");
            Assert.That(onHill, Is.LessThan(onFlat));
        }

        [Test]
        public void RoutedUnitStopsAtTheRiver()
        {
            var sim = OnStage("a1");
            var a = Add(sim, "A", Side.Blue, 1500, 350.5, 0, morale: 10);
            Add(sim, "E", Side.Red, 1500, 700, 270);
            sim.Step(); // 壊走
            Assert.That(a.State, Is.EqualTo(CompanyState.Routed));
            a.FleeDirection = -Math.PI / 2; // 真南（川）へ向かわせる
            a.NextEscapeCheckTime = double.PositiveInfinity;
            var before = a.Position;
            sim.Step();
            Assert.That(a.Position.Y, Is.EqualTo(before.Y), "川に突き当たったら、その刻みは動かない");
        }

        // ---------------------------------------------------------------- 予備隊の台本

        [Test]
        public void ReserveCounterattacksFlankOfNearestAttacker()
        {
            var sc = MapScenario.LoadFile(TestUtil.PathOf("maps/scenario_a1.json"));
            var log = new StringEventSink();
            var sim = sc.Build(TestUtil.Params, 1, log);
            var r3 = sim.Find("R3");
            var b2 = sim.Find("B2");
            Run(sim, 5);
            Assert.That(r3.CurrentOrder.Type, Is.EqualTo(OrderType.Hold), "引き金の円に入るまでは動かない");
            b2.Position = new Vec2(1650, 1000); // 村の中心 (1975, 1000) から 325m
            sim.Step();
            Assert.That(r3.CurrentOrder.Type, Is.EqualTo(OrderType.Attack));
            Assert.That(r3.CurrentOrder.TargetId, Is.EqualTo(b2.Id));
            Assert.That(r3.AttackChosenAspect, Is.EqualTo(AttackAspect.Flank));
            Assert.That(r3.ThreatWeightOverride, Is.EqualTo(0.0));
            StringAssert.Contains("\"type\":\"reserve_counterattack\",\"unit\":\"R3\",\"target\":\"B2\"", log.ToString());
            Assert.That(sim.Find("R1").CurrentOrder.Type, Is.EqualTo(OrderType.Hold), "R1・R2 は保持のまま");
        }

        [Test]
        public void InitialDeploymentRespectsSpacing()
        {
            foreach (var stage in MapReferenceTests.Stages)
            {
                var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_{stage}.json"));
                Assert.DoesNotThrow(() => sc.Build(TestUtil.Params, 1));
            }
        }
    }
}
