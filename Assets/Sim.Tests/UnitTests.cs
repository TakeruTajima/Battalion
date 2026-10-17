using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    public class ParamsTests
    {
        [Test]
        public void SimParamsIsIdenticalToInitial()
        {
            var a = File.ReadAllBytes(TestUtil.PathOf(TestUtil.ParamsRelPath));
            var b = File.ReadAllBytes(TestUtil.PathOf(TestUtil.InitialParamsRelPath));
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void LoadsValues()
        {
            var p = TestUtil.Params;
            Assert.That(p.TickSec, Is.EqualTo(0.5));
            Assert.That(p.Fire.KPerMin, Is.EqualTo(0.0133));
            Assert.That(p.Fire.AspectCasualtyMult, Is.EqualTo(new[] { 1.0, 1.5, 2.0 }));
            Assert.That(p.Morale.AspectMoraleMult, Is.EqualTo(new[] { 1.0, 2.0, 3.0 }));
            Assert.That(p.Of(Training.Veteran).RoutThreshold, Is.EqualTo(15));
            Assert.That(p.Of(Training.Green).MoraleSensitivity, Is.EqualTo(1.2));
            Assert.That(p.Encirclement.CheckWhenMoraleAtOrBelow, Is.EqualTo(50));
            Assert.That(p.Encirclement.CheckIntervalSec, Is.EqualTo(5));
            Assert.That(p.Encirclement.EscapeDirs, Is.EqualTo(16));
            Assert.That(p.Encirclement.EscapeLenM, Is.EqualTo(500));
            Assert.That(p.Encirclement.EscapeSpeedMps, Is.EqualTo(1.8));
            Assert.That(p.Encirclement.EscapeStepSec, Is.EqualTo(1.0));
            Assert.That(p.Encirclement.EscapeFireAspect, Is.EqualTo(Aspect.Rear));
            Assert.That(p.Encirclement.EscapeLossThreshold, Is.EqualTo(0.2));
            Assert.That(p.Encirclement.DespairShock, Is.EqualTo(15));
            Assert.That(p.Encirclement.MoralePerMin, Is.EqualTo(3.0));
            Assert.That(p.Encirclement.FortifiedMoraleMult, Is.EqualTo(0.5));
            Assert.That(p.Movement.FriendlyMinSpacingM, Is.EqualTo(200));
        }

        /// <summary>P1a.1 で sim_params.json に移した値が、P1a の定数と同じであること。</summary>
        [Test]
        public void LoadsP1MapValuesEqualToP1aConstants()
        {
            var p = TestUtil.Params;
            var move = new Dictionary<char, double> { ['.'] = 1.0, ['F'] = 0.5, ['V'] = 0.7, ['W'] = 0.0, ['B'] = 1.0, ['S'] = 0.3, ['T'] = 0.8, ['C'] = 0.6 };
            var damage = new Dictionary<char, double> { ['.'] = 1.0, ['F'] = 0.6, ['V'] = 0.5, ['W'] = 1.0, ['B'] = 1.0, ['S'] = 1.0, ['T'] = 0.3, ['C'] = 0.9 };
            Assert.That(p.Terrain.Move, Is.EquivalentTo(move));
            Assert.That(p.Terrain.Damage, Is.EquivalentTo(damage));
            Assert.That(p.Terrain.LosWeight, Is.EquivalentTo(new Dictionary<char, double> { ['F'] = 1.0, ['V'] = 0.5 }));

            Assert.That(p.Sight.BaseRangeM, Is.EqualTo(800));
            Assert.That(p.Sight.RangeGainPer10mHeight, Is.EqualTo(0.1));
            Assert.That(p.Sight.EyeHeightM, Is.EqualTo(2.0));
            Assert.That(p.Sight.SampleStepM, Is.EqualTo(10.0));
            Assert.That(p.Sight.CoverBlockM, Is.EqualTo(100.0));

            var r = p.MapRules;
            Assert.That(r.ExposureRebuildSec, Is.EqualTo(10.0));
            Assert.That(r.ReplanSec, Is.EqualTo(30.0));
            Assert.That(r.ExposureCostM, Is.EqualTo(50.0));
            Assert.That(r.ThreatWeight(Personality.Cautious), Is.EqualTo(3.0));
            Assert.That(r.ThreatWeight(Personality.Calm), Is.EqualTo(1.0));
            Assert.That(r.ThreatWeight(Personality.Agile), Is.EqualTo(1.0));
            Assert.That(r.ThreatWeight(Personality.Reckless), Is.EqualTo(0.3));
            Assert.That(r.ScriptedThreatWeight, Is.EqualTo(0.0));
            Assert.That(r.AttackStandoffM, Is.EqualTo(250.0));
            Assert.That(r.AgileFlankCostRatio, Is.EqualTo(1.5));
            Assert.That(r.AttackRepositionM, Is.EqualTo(50.0));
            Assert.That(r.DestinationSearchStepM, Is.EqualTo(50.0));
            Assert.That(r.DestinationSearchMaxM, Is.EqualTo(1000.0));
            Assert.That(r.DestinationSearchDirs, Is.EqualTo(16));
            Assert.That(r.AvoidStationaryFriendM, Is.EqualTo(200.0));
            Assert.That(r.EntrenchSec, Is.EqualTo(600.0));
            Assert.That(r.EntrenchDamageMult, Is.EqualTo(0.3));
            Assert.That(r.EntrenchHalfWidthCells, Is.EqualTo(2));
            Assert.That(r.BlockedEscapeLoss, Is.EqualTo(1.0));
            Assert.That(p.Movement.CompanyStrength, Is.EqualTo(150));
            Assert.That(p.Movement.RetreatSpeedMult, Is.EqualTo(0.6));
            Assert.That(p.CloseCombat.RangeMult, Is.EqualTo(2.0));
            Assert.That(p.CloseCombat.MoraleMult, Is.EqualTo(1.5));
        }
    }

    public class SimAssemblyTests
    {
        [Test]
        public void SimDoesNotReferenceUnityEngine()
        {
            var refs = typeof(Simulation).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
            TestContext.WriteLine("Sim の参照: " + string.Join(", ", refs));
            Assert.That(refs.Any(n => n.StartsWith("UnityEngine", StringComparison.Ordinal) || n.StartsWith("UnityEditor", StringComparison.Ordinal)), Is.False);
        }
    }

    public class Pcg32Tests
    {
        [Test]
        public void MatchesPcgBasicDemoOutput()
        {
            // pcg32-demo（pcg_basic）の seed=42, seq=54 の最初の6個
            var r = new Pcg32(42, 54);
            uint[] expected = { 0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e };
            foreach (var e in expected) Assert.That(r.NextUInt(), Is.EqualTo(e));
        }

        [Test]
        public void NextDoubleInUnitInterval()
        {
            var r = new Pcg32(1);
            for (int i = 0; i < 100000; i++)
            {
                double d = r.NextDouble();
                Assert.That(d, Is.GreaterThanOrEqualTo(0.0).And.LessThan(1.0));
            }
        }

        [TestCase(0.02)]
        [TestCase(0.0865)]
        [TestCase(3.0)]
        public void PoissonMeanAndVariance(double lambda)
        {
            var r = new Pcg32(123);
            const int n = 400000;
            double sum = 0, sum2 = 0;
            for (int i = 0; i < n; i++)
            {
                int k = r.NextPoisson(lambda);
                sum += k;
                sum2 += (double)k * k;
            }
            double mean = sum / n, var = sum2 / n - mean * mean;
            double se = Math.Sqrt(lambda / n);
            Assert.That(mean, Is.EqualTo(lambda).Within(5 * se));
            Assert.That(var, Is.EqualTo(lambda).Within(lambda * 0.05 + 5 * se));
        }

        [Test]
        public void PoissonZeroLambdaConsumesNothing()
        {
            var a = new Pcg32(5);
            var b = new Pcg32(5);
            Assert.That(a.NextPoisson(0), Is.EqualTo(0));
            Assert.That(a.NextUInt(), Is.EqualTo(b.NextUInt()));
        }
    }

    public class GeometryTests
    {
        static Simulation Empty() => new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -5000, -5000, 5000, 5000));

        [TestCase(0, 0, Aspect.Front)]      // 真正面
        [TestCase(60, 0, Aspect.Front)]     // 60° ちょうどは正面
        [TestCase(61, 0, Aspect.Flank)]
        [TestCase(-90, 0, Aspect.Flank)]
        [TestCase(120, 0, Aspect.Flank)]    // 120° ちょうどは側面
        [TestCase(121, 0, Aspect.Rear)]
        [TestCase(180, 0, Aspect.Rear)]
        [TestCase(0, 90, Aspect.Flank)]     // 対象が北を向き、射手が東
        public void AspectOfShooter(double shooterBearingDeg, double targetFacingDeg, Aspect expected)
        {
            var sim = Empty();
            var t = sim.AddCompany("T", Side.Red, Training.Regular, Personality.Calm, new Vec2(0, 0), targetFacingDeg);
            var from = Vec2.FromAngle(shooterBearingDeg * Angles.DegToRad) * 300;
            Assert.That(sim.AspectOf(t, from), Is.EqualTo(expected));
        }

        [TestCase(10, true, 2.0)]     // 接近戦：2.0 で置き換える（×1.3 と重ねない）
        [TestCase(120, true, 2.0)]    // 接近戦は矩形の隙間で決まる（P1g.1）。中心どうしが 150m 以内でも重ねない
        [TestCase(50, false, 1.3)]
        [TestCase(150, false, 1.3)]
        [TestCase(150.01, false, 1.0)]
        [TestCase(400, false, 1.0)]
        [TestCase(400.01, false, 0.0)]
        [TestCase(400.01, true, 0.0)]
        public void RangeMultiplierDoesNotStack(double distance, bool close, double expected)
        {
            Assert.That(Empty().RangeMultiplier(distance, close), Is.EqualTo(expected));
        }

    }

    /// <summary>退路の見積もり（v3 の包囲判定と壊走の方向）。</summary>
    public class EscapeEstimateTests
    {
        static Simulation Empty() => new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -5000, -5000, 5000, 5000));

        static Simulation Build(Scenario sc) => sc.Build(TestUtil.Params, 1);

        [Test]
        public void DirectionsStartEastCounterClockwise()
        {
            var sim = Empty();
            Assert.That(sim.EscapeDirection(0), Is.EqualTo(0));
            Assert.That(sim.EscapeDirection(4), Is.EqualTo(Math.PI / 2).Within(1e-12)); // 北
            Assert.That(sim.EscapeDirection(8), Is.EqualTo(Math.PI).Within(1e-12));    // 西
            Assert.That(sim.EscapeDirection(1), Is.EqualTo(22.5 * Angles.DegToRad).Within(1e-12));
        }

        [Test]
        public void LossMatchesHandCalculation()
        {
            // 東 300m に敵1個中隊。西へ逃げると、1.8m×55歩＝99m 進むまで（距離 399m）は射程内、56歩目（400.8m）で外れる。
            // 150m 以内にはならない。1秒あたり k/60×150×1.0、背面×2 → 55×0.0133/60×150×2 ÷ 150
            var sim = Empty();
            var c = sim.AddCompany("C", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            sim.AddCompany("E", Side.Red, Training.Regular, Personality.Calm, new Vec2(300, 0), 180);
            var est = sim.EstimateEscape(c);
            Assert.That(est.Best, Is.EqualTo(8), "真西");
            Assert.That(est.MinLoss, Is.EqualTo(55 * 0.0133 / 60 * 150 * 2 / 150).Within(1e-12));
            Assert.That(est.Loss.Length, Is.EqualTo(16));
        }

        [Test]
        public void SurroundedOnFourSidesHasNoEscape()
        {
            var sim = Build(Scenarios.S6_4v1_surrounded());
            var est = sim.EstimateEscape(sim.Find("R"));
            TestContext.WriteLine("S6 の R: " + string.Join(", ", est.Loss.Select(l => l.ToString("F3"))));
            Assert.That(est.MinLoss, Is.GreaterThanOrEqualTo(0.2));
        }

        [TestCase("S3_2v1_front_flank")]
        [TestCase("S4_2v1_front_rear")]
        public void TwoDirectionsLeaveAWayOut(string scenario)
        {
            // 正面と側面、正面と背面から撃たれていても、空いている方位に逃げ道があれば包囲ではない
            var sim = Build(Scenarios.ByName(scenario));
            var est = sim.EstimateEscape(sim.Find("R"));
            TestContext.WriteLine($"{scenario} の R: 最小 {est.MinLoss:F3}（方位 {est.Best}）");
            Assert.That(est.MinLoss, Is.LessThan(0.2));
        }

        [Test]
        public void IgnoresRoutedRallyingAndFarEnemies()
        {
            var sim = Empty();
            var c = sim.AddCompany("C", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            var r = sim.AddCompany("R", Side.Red, Training.Regular, Personality.Calm, new Vec2(100, 0), 180);
            var g = sim.AddCompany("G", Side.Red, Training.Regular, Personality.Calm, new Vec2(-100, 0), 0);
            sim.AddCompany("F", Side.Red, Training.Regular, Personality.Calm, new Vec2(0, 1000), 0); // 500m 逃げても 400m 以内に入らない
            r.State = CompanyState.Routed;
            g.State = CompanyState.Rallying;
            // 撃たない敵は損害に数えない。ただし P1d から、壊走中・再集結中の敵の矩形も逃げ道をふさぐ（東西は 1.0）
            var loss = sim.EstimateEscape(c).Loss;
            Assert.That(loss.All(l => l == 0 || l == 1.0), Is.True, string.Join(", ", loss));
            Assert.That(loss[4], Is.EqualTo(0), "北：撃つ敵の射程に入らない");
            Assert.That(loss[0], Is.EqualTo(1.0), "東：壊走中の R の矩形でふさがる");
            Assert.That(loss[8], Is.EqualTo(1.0), "西：再集結中の G の矩形でふさがる");
        }

        [Test]
        public void TiesPreferMovingAwayFromNearbyEnemies()
        {
            // 東 500m の敵。西寄りの多くの方位は損害0で同点になる。参照実装なら最初の同点（67.5°）を選ぶが、
            // 600m 以内の敵の平均位置から最も遠ざかる真西を選ぶ
            var sim = Empty();
            var c = sim.AddCompany("C", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            sim.AddCompany("E", Side.Red, Training.Regular, Personality.Calm, new Vec2(500, 0), 180);
            var est = sim.EstimateEscape(c);
            Assert.That(est.Loss[3], Is.EqualTo(0));
            Assert.That(est.Loss[2], Is.GreaterThan(0));
            Assert.That(est.Best, Is.EqualTo(8));
        }
    }

    public class TargetSelectionTests
    {
        static Simulation Empty() => new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -5000, -5000, 5000, 5000));

        [Test]
        public void PrefersNearestInFrontArc()
        {
            var sim = Empty();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            sim.AddCompany("Side", Side.Red, Training.Regular, Personality.Calm, new Vec2(0, 150), 0);   // 側面、近い
            var front = sim.AddCompany("Front", Side.Red, Training.Regular, Personality.Calm, new Vec2(350, 0), 180);
            Assert.That(sim.SelectTarget(a), Is.SameAs(front));
        }

        [Test]
        public void FallsBackToNearestOutsideArc()
        {
            var sim = Empty();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            var near = sim.AddCompany("N", Side.Red, Training.Regular, Personality.Calm, new Vec2(0, 150), 0);
            sim.AddCompany("Far", Side.Red, Training.Regular, Personality.Calm, new Vec2(-300, 0), 0);
            sim.AddCompany("OutOfRange", Side.Red, Training.Regular, Personality.Calm, new Vec2(401, 0), 0);
            Assert.That(sim.SelectTarget(a), Is.SameAs(near));
        }

        [Test]
        public void TieBreaksOnLowerId()
        {
            var sim = Empty();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            var first = sim.AddCompany("X1", Side.Red, Training.Regular, Personality.Calm, new Vec2(300, 100), 0);
            sim.AddCompany("X2", Side.Red, Training.Regular, Personality.Calm, new Vec2(300, -100), 0);
            Assert.That(sim.SelectTarget(a), Is.SameAs(first));
        }

        [Test]
        public void RoutedAndRallyingDoNotFire()
        {
            var sim = Empty();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            sim.AddCompany("R", Side.Red, Training.Regular, Personality.Calm, new Vec2(300, 0), 180);
            a.State = CompanyState.Routed;
            Assert.That(sim.SelectTarget(a), Is.Null);
            a.State = CompanyState.Rallying;
            Assert.That(sim.SelectTarget(a), Is.Null);
        }

        [Test]
        public void RoutedEnemiesCanBeTargeted()
        {
            var sim = Empty();
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            var r = sim.AddCompany("R", Side.Red, Training.Regular, Personality.Calm, new Vec2(100, 0), 0);
            r.State = CompanyState.Routed;
            Assert.That(sim.SelectTarget(a), Is.SameAs(r));
        }
    }
}
