using System;
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
            Assert.That(p.Encirclement.MinAspects, Is.EqualTo(2));
            Assert.That(p.Movement.CompanyStrength, Is.EqualTo(150));
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
        static Simulation Empty() => new Simulation(TestUtil.Params, 1, new MapBounds(-5000, -5000, 5000, 5000));

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

        [Test]
        public void RearCorridor()
        {
            var sim = Empty();
            var c = sim.AddCompany("C", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            var e = sim.AddCompany("E", Side.Red, Training.Regular, Personality.Calm, new Vec2(-499, 199), 0);
            Assert.That(sim.RearCorridorBlocked(c), Is.True);
            e.Position = new Vec2(-501, 0);
            Assert.That(sim.RearCorridorBlocked(c), Is.False);
            e.Position = new Vec2(-300, 201);
            Assert.That(sim.RearCorridorBlocked(c), Is.False);
            e.Position = new Vec2(10, 0); // 前方
            Assert.That(sim.RearCorridorBlocked(c), Is.False);
            e.Position = new Vec2(-300, 0);
            e.State = CompanyState.Routed; // 壊走中の敵は数えない
            Assert.That(sim.RearCorridorBlocked(c), Is.False);
        }
    }

    public class TargetSelectionTests
    {
        static Simulation Empty() => new Simulation(TestUtil.Params, 1, new MapBounds(-5000, -5000, 5000, 5000));

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
