using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P1b で Sim に足したもの：移動中の被弾倍率、表示用の読み取り専用の口、StreamingAssets の地図の写し。</summary>
    public class P1bSimTests
    {
        [TestCase("scenario_a1.json")]
        [TestCase("scenario_a2.json")]
        public void StreamingAssetsMapsAreIdenticalToMaps(string file)
        {
            var a = File.ReadAllBytes(TestUtil.PathOf("Assets/StreamingAssets/maps/" + file));
            var b = File.ReadAllBytes(TestUtil.PathOf("maps/" + file));
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void MovingOpenDamageMultIsTheR1Value()
        {
            // R1 の掃き出しで 3.0 に決めた（P1b では 1.0）
            Assert.That(TestUtil.Params.Terrain.MovingOpenDamageMult, Is.EqualTo(3.0));
        }

        static SimParams WithMovingOpenMult(double mult)
        {
            var p = SimParams.LoadFile(TestUtil.PathOf(TestUtil.ParamsRelPath));
            p.Terrain.MovingOpenDamageMult = mult;
            return p;
        }

        [Test]
        public void MovingOnOpenGroundTakesTheMultiplier()
        {
            var p = WithMovingOpenMult(2.0);
            var s = MapScenario.LoadFile(TestUtil.PathOf("maps/scenario_a1.json"));
            var sim = new Simulation(p, 1, TerrainMap.FromJson(s.Source, p.Terrain));
            var c = sim.AddCompany("C", Side.Blue, Training.Regular, Personality.Calm, new Vec2(500, 1000), 0);

            c.MovedThisTick = false;
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(1.0), "止まっていれば掛けない");
            c.MovedThisTick = true;
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(2.0), "平地を移動中");

            c.Position = new Vec2(1825, 1000); // 基本の地形の塹壕
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(2.0), "塹壕のマスを移動中（平地と同じ 1.0 に掛ける）");
            c.MovedThisTick = false;
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(0.3));

            c.MovedThisTick = true;
            c.Position = new Vec2(1000, 1650); // 森
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(0.6), "森は開けた場所ではない");
            c.Position = new Vec2(2025, 1000); // 村
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(0.5), "村は開けた場所ではない");
            c.Position = new Vec2(2025, 300); // 橋
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(2.0));
            c.Position = new Vec2(1025, 300); // 浅瀬
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(2.0));
            sim.Map.CellOf(new Vec2(500, 500), out int i, out int j);
            sim.Map.SetOverlay(i, j, TerrainMap.Crater);
            c.Position = new Vec2(500, 500); // 砲撃痕
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(0.9 * 2.0).Within(1e-12));
        }

        [TestCase(TerrainMap.Plain, true)]
        [TestCase(TerrainMap.Crater, true)]
        [TestCase(TerrainMap.Bridge, true)]
        [TestCase(TerrainMap.Ford, true)]
        [TestCase(TerrainMap.Trench, true)]
        [TestCase(TerrainMap.Forest, false)]
        [TestCase(TerrainMap.Village, false)]
        [TestCase(TerrainMap.River, false)]
        public void OpenGroundCells(char c, bool open)
        {
            Assert.That(Simulation.IsOpenGround(c), Is.EqualTo(open));
        }

        static string Hash(StringEventSink s)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(s.ToUtf8())).Replace("-", "");
        }

        /// <summary>表示から PeekExposure・PeekEscape を毎刻み呼んでも、会戦の結果（ログ）が変わらないこと。</summary>
        [Test]
        [Category("Slow")]
        public void PeeksDoNotChangeTheBattle()
        {
            var sc = MapScenario.LoadFile(TestUtil.PathOf("maps/scenario_a2.json"));
            var plan = ScenarioAReportTests.Plans(2).First(x => x.name == "両翼包囲（鶴翼）").orders;
            string Run(bool peek)
            {
                var log = new StringEventSink();
                var sim = sc.Build(TestUtil.Params, 3, log);
                var orders = plan.OrderBy(o => o.AtSec).ToList();
                int next = 0;
                while (sim.Time < 50 * 60)
                {
                    while (next < orders.Count && orders[next].AtSec <= sim.Time)
                    {
                        var o = orders[next++];
                        sim.Issue(Order.Advance(sim.Find(o.Unit).Id, new Vec2(o.X, o.Y), o.FacingDeg * Angles.DegToRad));
                    }
                    if (peek)
                    {
                        sim.PeekExposure(Side.Blue);
                        sim.PeekExposure(Side.Red);
                        foreach (var c in sim.Companies.Where(c => c.OnBoard)) sim.PeekEscape(c);
                    }
                    sim.Step();
                }
                return Hash(log) + "/" + log.Count;
            }
            Assert.That(Run(true), Is.EqualTo(Run(false)));
        }

        [Test]
        public void PeekEscapeMatchesEstimateWhenNothingIsCached()
        {
            var sim = new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -2000, -2000, 2000, 2000));
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 0);
            sim.AddCompany("E", Side.Red, Training.Regular, Personality.Calm, new Vec2(300, 0), 180);
            var peek = sim.PeekEscape(a);
            var est = sim.EstimateEscape(a);
            Assert.That(peek.Loss, Is.EqualTo(est.Loss));
            Assert.That(peek.Best, Is.EqualTo(est.Best));
        }
    }
}
