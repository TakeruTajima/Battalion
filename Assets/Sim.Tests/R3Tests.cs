using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>R3：向き直りの速さ（仕様「R3：向き直りの速さ（確定）」）。</summary>
    public class R3Tests
    {
        static Simulation Flat(SimParams p = null) =>
            new Simulation(p ?? TestUtil.Params, 1, TerrainMap.FlatCovering((p ?? TestUtil.Params).Terrain, -1500, -1500, 1500, 1500));

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg, int? strength = null) =>
            sim.AddCompany(name, side, Training.Regular, Personality.Calm, new Vec2(x, y), facingDeg, strength: strength);

        static double Deg(double rad) => rad * Angles.RadToDeg;

        /// <summary>線を張っている（自分の動きで movement.line_formed_still_sec 以上止まっている）状態にする。</summary>
        static Company LineFormed(Simulation sim, Company c)
        {
            c.StillSec = sim.Params.Movement.LineFormedStillSec;
            return c;
        }

        /// <summary>士気を保ったまま <paramref name="seconds"/> 秒進める（壊走・動揺で向きの規則が替わらないように）。</summary>
        static void Run(Simulation sim, double seconds, params Company[] keep)
        {
            int n = (int)Math.Round(seconds / sim.Params.TickSec);
            for (int i = 0; i < n; i++)
            {
                foreach (var c in keep) c.Morale = 100;
                sim.Step();
            }
        }

        static SimParams ParamsWith(Action<SimParams> set)
        {
            var p = SimParams.FromJson(System.IO.File.ReadAllText(TestUtil.PathOf(TestUtil.ParamsRelPath)));
            set(p);
            return p;
        }

        // ---------------------------------------------------------------- 旋回の速さ

        /// <summary>接敵していない中隊は、今までどおり毎秒 movement.turn_deg_per_sec（6°）で回る。</summary>
        [Test]
        public void TurnsFastWhenNotEngaged()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            Add(sim, "Far", Side.Red, 1400, 1400, 180);
            sim.Issue(Order.Hold(a.Id, Math.PI / 2));
            Run(sim, 10, a);
            Assert.That(a.Engaged, Is.False);
            Assert.That(Deg(a.Facing), Is.EqualTo(10 * sim.Params.Movement.TurnDegPerSec).Within(1e-6));
            Assert.That(a.TurningAsMoving, Is.False, "接敵していなければ動いている扱いにしない");
        }

        /// <summary>線を張った中隊は、接敵中（400m 以内に視線の通る指揮下の敵）は毎秒 movement.turn_deg_per_sec_engaged（0.69°）で回る。</summary>
        [Test]
        public void TurnsSlowlyWhenEngaged()
        {
            var sim = Flat();
            var a = LineFormed(sim, Add(sim, "A", Side.Blue, 0, 0, 0));
            var e = Add(sim, "E", Side.Red, 0, 350, -90, strength: 10);
            sim.Issue(Order.Hold(a.Id, Math.PI / 2));
            Run(sim, 10, a, e);
            Assert.That(a.Engaged, Is.True);
            Assert.That(Deg(a.Facing), Is.EqualTo(10 * sim.Params.Movement.TurnDegPerSecEngaged).Within(1e-6));
        }

        /// <summary>壊走中は対象外：逃げる方角へすぐ向く（今のまま）。</summary>
        [Test]
        public void RoutedCompanyIsNotSlowed()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var e = Add(sim, "E", Side.Red, 300, 0, 180, strength: 10);
            Run(sim, 1, e);
            a.State = CompanyState.Routed;
            a.Morale = 10;
            Run(sim, 6, e);
            Assert.That(Math.Abs(Angles.Wrap(a.Facing - Math.PI)), Is.LessThan(0.6), "敵（東）から離れる西の方へ向いて逃げる");
            Assert.That(a.TurningAsMoving, Is.False);
        }

        /// <summary>線を張る前（止まって line_formed_still_sec 未満）は、接敵中でも速く回り、旋回で動いている扱いにならない。</summary>
        [Test]
        public void TurnsFastBeforeTheLineIsFormed()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var e = Add(sim, "E", Side.Red, 0, 350, -90, strength: 10);
            a.StillSec = sim.Params.Movement.LineFormedStillSec - 20;
            sim.Issue(Order.Hold(a.Id, Math.PI / 2));
            Run(sim, 5, a, e);
            Assert.That(a.Engaged, Is.True);
            Assert.That(Deg(a.Facing), Is.EqualTo(5 * sim.Params.Movement.TurnDegPerSec).Within(1e-6));
            Assert.That(a.TurningAsMoving, Is.False, "線を張る前の旋回は動いている扱いにしない");
        }

        /// <summary>止まって line_formed_still_sec が経つと線を張り、そこから遅い旋回になる。</summary>
        [Test]
        public void LineIsFormedAfterStayingStill()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            var e = Add(sim, "E", Side.Red, 1400, 1400, 180, strength: 10);
            a.StillSec = 0;
            Run(sim, sim.Params.Movement.LineFormedStillSec, a, e);
            Assert.That(a.StillSec, Is.EqualTo(sim.Params.Movement.LineFormedStillSec).Within(1e-6));
            Assert.That(a.Facing, Is.EqualTo(0).Within(1e-9));
            e.Position = new Vec2(0, 350);
            sim.Issue(Order.Hold(a.Id, Math.PI / 2));
            Run(sim, 5, a, e);
            Assert.That(a.Engaged, Is.True);
            Assert.That(Deg(a.Facing), Is.EqualTo(5 * sim.Params.Movement.TurnDegPerSecEngaged).Within(1e-6));
            Assert.That(a.TurningAsMoving, Is.True);
        }

        /// <summary>自分で動くと線は解ける（止まっている長さを数え直す）。</summary>
        [Test]
        public void MovingBreaksTheLine()
        {
            var sim = Flat();
            var a = LineFormed(sim, Add(sim, "A", Side.Blue, 0, 0, 90));
            Add(sim, "Far", Side.Red, 1400, 1400, 180);
            sim.Issue(Order.Advance(a.Id, new Vec2(0, 300)));
            Run(sim, 5, a);
            Assert.That(a.MovedThisTick, Is.True);
            Assert.That(a.StillSec, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- 旋回中は動いている扱い

        /// <summary>
        /// 接敵中にその場で 20° を超えて向きを変えている間は、R1 の動いている扱い：撃つ力 ×fire.moving_fire_mult、開けた所の被弾
        /// ×terrain.moving_open_damage_mult、制圧のしきい値は動いている時の値。表示の要素は「旋回中（動いている扱い）」。
        /// </summary>
        [Test]
        public void TurningMoreThan20DegreesCountsAsMoving()
        {
            var sim = Flat();
            var a = LineFormed(sim, Add(sim, "A", Side.Blue, 0, 0, 0));
            var e = Add(sim, "E", Side.Red, 0, 350, -90, strength: 10);
            sim.Issue(Order.Hold(a.Id, Math.PI / 2));
            Run(sim, 5, a, e);
            Assert.That(a.TurningAsMoving, Is.True);
            Assert.That(a.MovedThisTick, Is.False, "その場で回っている");
            var fire = new List<ModifierFactor>();
            sim.CurrentFireMult(a, fire);
            Assert.That(fire.Any(x => x.Kind == ModifierKind.FireTurning && Math.Abs(x.Mult - sim.Params.Fire.MovingFireMult) < 1e-12), Is.True);
            var dmg = new List<ModifierFactor>();
            sim.DamageFactors(a, null, dmg);
            Assert.That(dmg.Any(x => x.Kind == ModifierKind.TurningOpen), Is.True);
            Assert.That(sim.TerrainDamageMult(a), Is.EqualTo(sim.Params.Terrain.MovingOpenDamageMult).Within(1e-12));
            Assert.That(sim.ExposedWhileMoving(a), Is.True, "「露」の印");
            double moving = sim.Params.Suppression.ThresholdMovingPerMin *
                            (sim.Params.Suppression.MovingThresholdPersonalityMult.TryGetValue(a.Personality, out var m) ? m : 1.0);
            Assert.That(sim.SuppressionThreshold(a), Is.EqualTo(moving).Within(1e-12));
        }

        /// <summary>向きの差が 20° 以下になったら（回り終わりの手前）、動いている扱いを外す。</summary>
        [Test]
        public void Turning20DegreesOrLessDoesNotCountAsMoving()
        {
            var sim = Flat();
            var a = LineFormed(sim, Add(sim, "A", Side.Blue, 0, 0, 0));
            var e = Add(sim, "E", Side.Red, 0, 350, -90, strength: 10);
            sim.Issue(Order.Hold(a.Id, 19.5 * Angles.DegToRad));
            Run(sim, 5, a, e);
            Assert.That(a.Facing, Is.GreaterThan(0), "回っている");
            Assert.That(a.TurningAsMoving, Is.False);
            Assert.That(sim.SuppressionThreshold(a), Is.EqualTo(sim.Params.Suppression.ThresholdStationaryPerMin).Within(1e-12));
            Assert.That(sim.TerrainDamageMult(a), Is.EqualTo(1.0).Within(1e-12));
        }

        /// <summary>movement.turning_counts_as_moving が false なら、回っていても動いている扱いにしない。</summary>
        [Test]
        public void TurningAsMovingCanBeSwitchedOff()
        {
            var sim = Flat(ParamsWith(p => p.Movement.TurningCountsAsMoving = false));
            var a = LineFormed(sim, Add(sim, "A", Side.Blue, 0, 0, 0));
            var e = Add(sim, "E", Side.Red, 0, 350, -90, strength: 10);
            sim.Issue(Order.Hold(a.Id, Math.PI / 2));
            Run(sim, 5, a, e);
            Assert.That(Deg(a.Facing), Is.EqualTo(5 * sim.Params.Movement.TurnDegPerSecEngaged).Within(1e-6), "遅い旋回はそのまま");
            Assert.That(a.TurningAsMoving, Is.False);
        }

        // ---------------------------------------------------------------- 検証の場面 S10〜S13

        /// <summary>
        /// S11（塹壕を側面から1個中隊）：30分以内に崩れる割合 0.8 以上。S12（正面と側面）0.8 以上、S10（正面）0.2 以下、S13（正面から
        /// 2個中隊）0.2 以下を保つ（各 20シード）。
        /// </summary>
        [Test]
        public void SlowTurningLetsASingleFlankerBreakTheTrench()
        {
            var p = TestUtil.Params;
            var s10 = R2Tests.RunTrenchScene(Scenarios.S10_trench_from_front(), p);
            var s11 = R2Tests.RunTrenchScene(Scenarios.S11_trench_from_flank(), p);
            var s12 = R2Tests.RunTrenchScene(Scenarios.S12_trench_front_and_flank(), p);
            var s13 = R2Tests.RunTrenchScene(Scenarios.S13_trench_two_from_front(), p);
            string M(R2Tests.TrenchSummary s) => s.BrokenMedianMin.HasValue ? $"{s.BrokenMedianMin:0.0}分" : "—";
            TestContext.WriteLine($"S10 {s10.BrokenRate:0.00}（{M(s10)}）／S11 {s11.BrokenRate:0.00}（{M(s11)}）／" +
                                  $"S12 {s12.BrokenRate:0.00}（{M(s12)}）／S13 {s13.BrokenRate:0.00}（{M(s13)}）");
            Assert.That(s11.BrokenRate, Is.GreaterThanOrEqualTo(0.8 - 1e-9), "S11");
            Assert.That(s12.BrokenRate, Is.GreaterThanOrEqualTo(0.8 - 1e-9), "S12");
            Assert.That(s10.BrokenRate, Is.LessThanOrEqualTo(0.2 + 1e-9), "S10");
            Assert.That(s13.BrokenRate, Is.LessThanOrEqualTo(0.2 + 1e-9), "S13");
        }

        /// <summary>掃き出し（期待を満たさないとき用。ふだんは回さない）：turn_deg_per_sec_engaged × turning_counts_as_moving。</summary>
        [Test, Explicit("掃き出し")]
        public void Sweep()
        {
            foreach (double rate in new[] { 0.69, 1.5, 3.0 })
                foreach (bool moving in new[] { true, false })
                {
                    var p = ParamsWith(x => { x.Movement.TurnDegPerSecEngaged = rate; x.Movement.TurningCountsAsMoving = moving; });
                    var r = new[] { Scenarios.S10_trench_from_front(), Scenarios.S11_trench_from_flank(), Scenarios.S12_trench_front_and_flank(), Scenarios.S13_trench_two_from_front() }
                        .Select(sc => R2Tests.RunTrenchScene(sc, p).BrokenRate).ToArray();
                    TestContext.WriteLine($"| {rate} | {moving} | {r[0]:0.00} | {r[1]:0.00} | {r[2]:0.00} | {r[3]:0.00} |");
                }
        }
    }
}
