using System;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P1g：目標が見えないときの中隊長の判断（仕様「P1g：目標が見えないときの中隊長の判断（確定）」）。</summary>
    public class P1gSightTests
    {
        static Simulation Stage1(IEventSink log = null)
        {
            var s = MapScenario.LoadFile(TestUtil.PathOf("maps/scenario_a1.json"));
            return new Simulation(TestUtil.Params, 1, TerrainMap.FromJson(s.Source, TestUtil.Params.Terrain), log);
        }

        static Simulation Flat(IEventSink log = null) =>
            new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -3000, -3000, 3000, 3000), log);

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg) =>
            sim.AddCompany(name, side, Training.Regular, Personality.Calm, new Vec2(x, y), facingDeg);

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

        /// <summary>平らな地図の、x が [x0, x1)、y が [y0, y1) のマスを森にする。</summary>
        static void Forest(Simulation sim, double x0, double x1, double y0, double y1)
        {
            var m = sim.Map;
            for (int j = 0; j < m.Height; j++)
                for (int i = 0; i < m.Width; i++)
                {
                    var c = m.CellCenter(i, j);
                    if (c.X >= x0 && c.X < x1 && c.Y >= y0 && c.Y < y1) m.SetOverlay(i, j, TerrainMap.Forest);
                }
        }

        // ---------------------------------------------------------------- 攻撃

        [Test]
        public void AttackerAtJumpOffInDeadGroundClosesUntilItSeesTheTarget()
        {
            // ステージ1：頂上の塹壕の R1（西向き）を正面から攻撃する。発起点（R1 の 250m 西）は稜線の死角（P1f の断面図）
            var log = new StringEventSink();
            var sim = Stage1(log);
            var r1 = Add(sim, "R1", Side.Red, 1825, 1000, 180);
            var a = Add(sim, "A", Side.Blue, 1300, 1000, 0);
            sim.Issue(Order.Attack(a.Id, r1.Id, AttackAspect.Front));
            for (int k = 0; k < 4000 && !a.AttackFixed; k++) Step(sim);
            Assert.That(a.AttackFixed, Is.True);
            Assert.That(Vec2.Distance(a.Position, sim.JumpOff(r1, Math.PI)), Is.LessThanOrEqualTo(0.6 + 1e-9), "発起点に着いた（着いた刻みに詰め始めるので、1刻み分まで）");
            Assert.That(LineOfSight.Visible(sim.Map, sim.Params.Sight, a.Position, r1.Position), Is.False, "発起点は死角");
            Assert.That(a.SeekingSight, Is.True, "着いた刻みに、見える位置を探して詰め始める");

            for (int k = 0; k < 2000 && a.TargetId != r1.Id; k++) Step(sim);
            Assert.That(a.TargetId, Is.EqualTo(r1.Id), "見える所まで詰めて撃ち始める");
            Assert.That(Vec2.Distance(a.Position, r1.Position), Is.LessThan(250));
            Assert.That(Math.Abs(a.Position.Y - 1000), Is.LessThan(1e-6), "固定した方角（真西）に沿って詰める");
            StringAssert.Contains("\"type\":\"seek_sight\",\"unit\":\"A\"", log.ToString());
            Run(sim, 10);
            Assert.That(a.SeekingSight, Is.False, "着いたら探すのをやめる");
        }

        // ---------------------------------------------------------------- 拘束

        [Test]
        public void PinShiftsSidewaysToASpotThatSeesTheTarget()
        {
            // 平らな地図。目標 E（原点、西向き）と拘束の A（345m 西）の間に、幅 200m・南北 200m の森。遠い拘束（350m）なのでその場で止まるが、
            // 森で見えない。距離を保ったまま、左右に見える位置を探して移る
            var log = new StringEventSink();
            var sim = Flat(log);
            Forest(sim, -300, -100, -100, 100);
            var e = Add(sim, "E", Side.Red, 0, 0, 180);
            var a = Add(sim, "A", Side.Blue, -345, 0, 0);
            Assert.That(LineOfSight.Visible(sim.Map, sim.Params.Sight, a.Position, e.Position), Is.False);
            sim.Issue(Order.Pin(a.Id, e.Id));
            for (int k = 0; k < 400 && !a.SeekingSight; k++) Step(sim);
            Assert.That(a.SeekingSight, Is.True);
            StringAssert.Contains("\"how\":\"side\"", log.ToString());
            for (int k = 0; k < 4000 && a.TargetId != e.Id; k++) Step(sim);
            Assert.That(a.TargetId, Is.EqualTo(e.Id), "見える位置に移って撃つ");
            Assert.That(Math.Abs(a.Position.Y), Is.GreaterThan(50), "横へ移った");
            Assert.That(Vec2.Distance(a.Position, e.Position), Is.EqualTo(345).Within(30), "距離を保つ");
        }

        [Test]
        public void PinClosesInWhenNoSideSpotSeesTheTarget()
        {
            // 南北 1200m の森の壁（幅 200m）：±45° の範囲のどこからも見えない。今の方角に沿って詰める（125m より近くへは詰めない）
            var log = new StringEventSink();
            var sim = Flat(log);
            Forest(sim, -300, -100, -600, 600);
            var e = Add(sim, "E", Side.Red, 0, 0, 180);
            var a = Add(sim, "A", Side.Blue, -345, 0, 0);
            sim.Issue(Order.Pin(a.Id, e.Id));
            for (int k = 0; k < 400 && !a.SeekingSight; k++) Step(sim);
            Assert.That(a.SeekingSight, Is.True);
            StringAssert.Contains("\"how\":\"advance\"", log.ToString());
            for (int k = 0; k < 4000 && a.TargetId != e.Id; k++) Step(sim);
            Assert.That(a.TargetId, Is.EqualTo(e.Id), "見えるまで詰めて撃つ");
            double d = Vec2.Distance(a.Position, e.Position);
            Assert.That(d, Is.LessThan(345 - 50));
            Assert.That(d, Is.GreaterThanOrEqualTo(125 - 1e-6), "近い拘束の 125m より近くへは詰めない");
            Assert.That(Math.Abs(a.Position.Y), Is.LessThan(1e-6), "今の方角に沿って詰める");
        }

        // ---------------------------------------------------------------- 接触中

        [TestCase(250, true)]  // 北向きどうし横に並ぶ：正面幅 200m なので隙間 50m
        [TestCase(270, false)] // 隙間 70m
        public void WithinFiftyMetresGapTheyFireWithoutSight(double x, bool fires)
        {
            var sim = Flat();
            Forest(sim, -1000, 1000, -1000, 1000); // 一面の森：間の森が 100m を超えるので見えない
            var a = Add(sim, "A", Side.Blue, 25, 25, 90);
            var e = Add(sim, "E", Side.Red, x + 25, 25, 90);
            Assert.That(LineOfSight.Visible(sim.Map, sim.Params.Sight, a.Position, e.Position), Is.False);
            Assert.That(sim.InFireContact(a, e), Is.EqualTo(fires));
            Assert.That(sim.SelectTarget(a), fires ? Is.SameAs(e) : Is.Null);
            Assert.That(sim.SelectTarget(e), fires ? Is.SameAs(a) : Is.Null);
        }

        // ---------------------------------------------------------------- P1g.1 接近戦の倍率は隙間で

        /// <summary>向かい合った A と E（中心どうし <paramref name="centers"/>）の最初の刻みの、A の射撃の損害の期待値（1分あたり）。</summary>
        static double ExpectedFireAt(double centers, out bool close)
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var e = Add(sim, "E", Side.Red, centers, 0, 180);
            close = sim.InCloseCombat(a, e);
            sim.Step();
            foreach (var f in sim.LastTickFire)
                if (f.Shooter == a.Id) return f.ExpectedPerMin;
            throw new AssertionException("A が撃っていない");
        }

        [Test]
        public void CloseCombatMultiplierAppliesWithinFiftyMetresGap()
        {
            // 奥行 50m どうしが向かい合う：中心どうし 95m は隙間 45m（接近戦）、105m は隙間 55m（接近戦でない）。
            // どちらも中心どうし 150m 以内なので、P1g までは両方 ×1.3 だった
            double near = ExpectedFireAt(95, out bool closeNear);
            double far = ExpectedFireAt(105, out bool closeFar);
            Assert.That(closeNear, Is.True);
            Assert.That(closeFar, Is.False);
            Assert.That(near / far, Is.EqualTo(2.0 / 1.3).Within(1e-9), "×2.0 と ×1.3 の比");
        }

        // ---------------------------------------------------------------- 保持

        [Test]
        public void HoldingCompanyDoesNotMoveForAnUnseenEnemy()
        {
            var log = new StringEventSink();
            var sim = Stage1(log);
            var r1 = Add(sim, "R1", Side.Red, 1825, 1000, 180);
            var b = Add(sim, "B", Side.Blue, 1825 - 250, 1000, 0); // 死角
            Assert.That(LineOfSight.Visible(sim.Map, sim.Params.Sight, b.Position, r1.Position), Is.False);
            Run(sim, 120);
            Assert.That(b.Position.X, Is.EqualTo(1825 - 250));
            Assert.That(b.Position.Y, Is.EqualTo(1000));
            Assert.That(b.SeekingSight, Is.False);
            StringAssert.DoesNotContain("seek_sight", log.ToString());
        }
    }
}
