using System;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P1f：表示のための視線のたどり（LineOfSight.TraceLine）が、会戦の判定（Check）と同じ結果を返すこと。</summary>
    public class P1fSightTraceTests
    {
        static TerrainMap Stage(string stage)
        {
            var s = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_{stage}.json"));
            return TerrainMap.FromJson(s.Source, TestUtil.Params.Terrain);
        }

        [TestCase("a1")]
        [TestCase("a2")]
        public void TraceMatchesCheckOnRandomPairs(string stage)
        {
            var m = Stage(stage);
            var sp = TestUtil.Params.Sight;
            var rng = new Pcg32(7, 1);
            int blocked = 0, ridge = 0, cover = 0;
            for (int k = 0; k < 3000; k++)
            {
                var a = new Vec2(m.MinX + rng.NextDouble() * (m.MaxX - m.MinX), m.MinY + rng.NextDouble() * (m.MaxY - m.MinY));
                // 近い組を多めに（射程 400m 前後）
                double r = 50 + rng.NextDouble() * 900, th = rng.NextDouble() * 2 * Math.PI;
                var b = new Vec2(a.X + Math.Cos(th) * r, a.Y + Math.Sin(th) * r);
                if (!m.Contains(b)) continue;
                var expected = LineOfSight.Check(m, sp, a, b);
                var t = LineOfSight.TraceLine(m, sp, a, b);
                Assert.That(t.Result, Is.EqualTo(expected), $"{a} → {b}");
                bool isBlocked = expected == SightResult.Ridge || expected == SightResult.Cover;
                Assert.That(t.BlockAt.HasValue, Is.EqualTo(isBlocked));
                if (expected == SightResult.Ridge)
                {
                    ridge++;
                    var s = t.Samples.Find(x => Math.Abs(x.DistanceM - t.BlockDistanceM) < 1e-9);
                    Assert.That(s.AboveSight, Is.True, "遮った点で地面が視線より高い");
                }
                if (expected == SightResult.Cover)
                {
                    cover++;
                    Assert.That(t.CoverBy, Is.EqualTo(TerrainMap.Forest).Or.EqualTo(TerrainMap.Village));
                }
                if (isBlocked) blocked++;
                if (expected != SightResult.Range)
                {
                    Assert.That(t.Samples[0].DistanceM, Is.EqualTo(0));
                    Assert.That(t.Samples[t.Samples.Count - 1].DistanceM, Is.EqualTo(t.DistanceM).Within(1e-9));
                }
            }
            Assert.That(ridge, Is.GreaterThan(0), "稜線で遮られる組を含む");
            if (stage == "a1") Assert.That(cover, Is.GreaterThan(0), "森で遮られる組を含む");
        }

        [Test]
        public void SlopeInFrontOfTheRidgeTrenchIsHiddenByTheRidge()
        {
            // ステージ1：頂上の塹壕（R1）から正面 250m の斜面は稜線の陰（MapBehaviourTests と同じ地点）
            var m = Stage("a1");
            var t = LineOfSight.TraceLine(m, TestUtil.Params.Sight, new Vec2(1825, 1000), new Vec2(1825 - 250, 1000));
            Assert.That(t.Result, Is.EqualTo(SightResult.Ridge));
            Assert.That(t.BlockDistanceM, Is.LessThan(250));
        }
    }
}
