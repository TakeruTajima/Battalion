using System.IO;
using Battalion.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Battalion.View.Tests
{
    /// <summary>表示側の、画面に依存しない部分（時間の進め方、攻撃の側の選び方、ログの拾い方、読み込み）。Unity の EditMode でだけ回る。</summary>
    public class ViewLogicTests
    {
        [Test]
        public void ClockRunsFourGameSecondsPerRealSecondTimesSpeed()
        {
            var c = new SimClock(0.5);
            Assert.That(c.Advance(1.0 / 8), Is.EqualTo(1), "×1：現実の 1/8 秒＝ゲーム内 0.5 秒＝1刻み");
            c.SetSpeed(4);
            Assert.That(c.Advance(1.0 / 8), Is.EqualTo(4));
            c.Paused = true;
            Assert.That(c.Advance(1.0), Is.EqualTo(0), "一時停止中は進めない");
        }

        [Test]
        public void ClockCapsAtSixteenTicksPerFrameAndDropsTheRest()
        {
            var c = new SimClock(0.5);
            c.SetSpeed(4);
            Assert.That(c.Advance(1.0), Is.EqualTo(16), "×4 で現実の1秒＝32刻みたまるが、1フレーム16刻みまで");
            Assert.That(c.Advance(0.0001), Is.EqualTo(0), "上限を超えた分は捨てる");
        }

        [Test]
        public void ClockAlphaIsTheFractionOfTheNextTick()
        {
            var c = new SimClock(0.5);
            c.Advance(1.0 / 16); // ゲーム内 0.25 秒
            Assert.That(c.Alpha, Is.EqualTo(0.5f).Within(1e-6));
        }

        [TestCase(0, 90, 0)]      // 中央
        [TestCase(0, 40, +1)]     // 西向きの敵：左は南。南寄り（across>33.3）なら +1
        [TestCase(0, 160, -1)]
        public void FlankSideFromClickPosition(double x, double y, int expected)
        {
            // 敵は (0, 100) にいて西（180°）を向く。左（向きから +90°）は南
            var center = new Vec2(0, 100);
            double facing = 180 * Angles.DegToRad;
            Assert.That(UnitGeometry.FlankSideAt(center, facing, 200, new Vec2(x, y)), Is.EqualTo(expected));
        }

        [Test]
        public void FlankSideMatchesTheJumpOffSide()
        {
            // UnitGeometry の +1 の側が、Sim の JumpOff の +1（敵の左）と同じ側であること
            var p = SimParams.LoadFile(Path.Combine(Application.streamingAssetsPath, "sim_params.json"));
            var sim = new Simulation(p, 1, TerrainMap.FlatCovering(p.Terrain, -1000, -1000, 1000, 1000));
            var e = sim.AddCompany("E", Side.Red, Training.Regular, Personality.Calm, new Vec2(0, 0), 30);
            var left = sim.JumpOff(e, AttackAspect.Flank, +1);
            Assert.That(UnitGeometry.FlankSideAt(e.Position, e.Facing, 200, left), Is.EqualTo(+1));
            var right = sim.JumpOff(e, AttackAspect.Flank, -1);
            Assert.That(UnitGeometry.FlankSideAt(e.Position, e.Facing, 200, right), Is.EqualTo(-1));
        }

        [Test]
        public void UnitRectangleHitTest()
        {
            // 東向き：正面幅 200m は南北、奥行 50m は東西
            Assert.That(UnitGeometry.Contains(new Vec2(0, 0), 0, 200, 50, new Vec2(20, 90)), Is.True);
            Assert.That(UnitGeometry.Contains(new Vec2(0, 0), 0, 200, 50, new Vec2(30, 0)), Is.False);
            Assert.That(UnitGeometry.Contains(new Vec2(0, 0), 0, 200, 50, new Vec2(0, 101)), Is.False);
        }

        [Test]
        public void EventFeedPicksMainEventsAndCountsBrokenCompanies()
        {
            var f = new EventFeed();
            f.Write("{\"t\":60,\"type\":\"unit_snapshot\",\"unit\":\"B1\"}");
            f.Write("{\"t\":61,\"type\":\"rout\",\"unit\":\"B2\",\"morale\":24,\"strength\":120,\"encircled\":false}");
            f.Write("{\"t\":62,\"type\":\"rout\",\"unit\":\"R1\",\"morale\":24,\"strength\":120,\"encircled\":true}");
            f.Write("{\"t\":62,\"type\":\"surrender\",\"unit\":\"R1\",\"strength\":120}");
            f.Write("{\"t\":70,\"type\":\"reserve_counterattack\",\"unit\":\"R3\",\"target\":\"B3\"}");
            f.Write("{\"t\":80,\"type\":\"entrench_complete\",\"unit\":\"B4\",\"x\":1,\"y\":2}");
            Assert.That(f.Entries.Count, Is.EqualTo(4), "スナップショットは出さず、降伏は壊走と二重に出さない");
            Assert.That(f.EverRouted, Is.EquivalentTo(new[] { "B2", "R1" }));
            Assert.That(f.TerrainChanged, Is.True);
            Assert.That(EventFeed.FormatTime(125), Is.EqualTo("T+02:05"));
        }

        [Test]
        public void StreamingAssetsHaveParamsAndBothMaps()
        {
            var p = SimParams.LoadFile(Path.Combine(Application.streamingAssetsPath, "sim_params.json"));
            foreach (var stage in new[] { 1, 2 })
            {
                var sc = MapScenario.LoadFile(Path.Combine(Application.streamingAssetsPath, $"maps/scenario_a{stage}.json"));
                Assert.DoesNotThrow(() => sc.Build(p, 1));
            }
        }
    }
}
