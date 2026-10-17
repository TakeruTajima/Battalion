using System.IO;
using System.Linq;
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
        public void SpeedsGoUpToSixteenAndSixteenDoesNotHitTheCapAtSixtyFps()
        {
            var c = new SimClock(0.5);
            Assert.That(SimClock.Speeds, Is.EqualTo(new[] { 1, 2, 4, 8, 16 }));
            for (int k = 0; k < 10; k++) c.SpeedUp();
            Assert.That(c.Speed, Is.EqualTo(16));
            // ×16 は現実の1秒でゲーム内 64秒＝128刻み。60fps なら1フレーム約2刻みで、上限の16刻みに当たらない
            int total = 0;
            for (int f = 0; f < 60; f++)
            {
                int n = c.Advance(1.0 / 60);
                Assert.That(n, Is.LessThan(SimClock.MaxTicksPerFrame));
                total += n;
            }
            Assert.That(total, Is.InRange(127, 128), "1秒で128刻み（遅れない）");
            // 上限に当たるのは 8fps 程度まで落ちたとき（1フレーム 16刻み）
            Assert.That(c.Advance(1.0 / 8), Is.EqualTo(16));
            for (int k = 0; k < 10; k++) c.SpeedDown();
            Assert.That(c.Speed, Is.EqualTo(1));
        }

        [Test]
        public void ClockAlphaIsTheFractionOfTheNextTick()
        {
            var c = new SimClock(0.5);
            c.Advance(1.0 / 16); // ゲーム内 0.25 秒
            Assert.That(c.Alpha, Is.EqualTo(0.5f).Within(1e-6));
        }

        [Test]
        public void DraggedBearingPutsTheJumpOffOnTheDragRay()
        {
            // 敵の駒から外へドラッグした点の方角に、250m の発起点が来ること（P1e の4）。結果の向きは目標の向きで決まる
            var p = SimParams.LoadFile(Path.Combine(Application.streamingAssetsPath, "sim_params.json"));
            var sim = new Simulation(p, 1, TerrainMap.FlatCovering(p.Terrain, -1000, -1000, 1000, 1000));
            var e = sim.AddCompany("E", Side.Red, Training.Regular, Personality.Calm, new Vec2(100, 50), 180);
            var dragTo = new Vec2(100 - 60, 50 - 160); // 南南西寄り
            double b = Simulation.BearingFrom(e, dragTo);
            var j = sim.JumpOff(e, b);
            Assert.That(Vec2.Distance(j, e.Position), Is.EqualTo(250).Within(1e-9));
            var u = (dragTo - e.Position) * (1.0 / Vec2.Distance(dragTo, e.Position));
            Assert.That(Vec2.Distance(j, e.Position + u * 250), Is.LessThan(1e-9));
            Assert.That(sim.AspectOfBearing(e, b), Is.EqualTo(AttackAspect.Flank));
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
        public void EventFeedRaisesHappeningsForTheMap()
        {
            var f = new EventFeed();
            f.Write("{\"t\":10,\"type\":\"first_flank_hit\",\"unit\":\"R1\",\"by\":\"B1\",\"morale_hit\":10}");
            f.Write("{\"t\":11,\"type\":\"first_rear_hit\",\"unit\":\"R1\",\"by\":\"B3\",\"morale_hit\":15}");
            f.Write("{\"t\":12,\"type\":\"encircled_start\",\"unit\":\"R1\",\"reason\":\"no_escape\",\"escape_loss\":0.3}");
            f.Write("{\"t\":13,\"type\":\"rally\",\"unit\":\"B2\",\"phase\":\"start\",\"morale\":25,\"strength\":100}");
            Assert.That(f.Recent.Select(h => h.Kind), Is.EqualTo(new[]
            {
                EventFeed.Kind.FlankHit, EventFeed.Kind.RearHit, EventFeed.Kind.Encircled, EventFeed.Kind.RallyStart,
            }));
            Assert.That(f.Recent[0].Other, Is.EqualTo("B1"));
        }

        [Test]
        public void VillageStateFollowsTheWinCondition()
        {
            var p = SimParams.LoadFile(Path.Combine(Application.streamingAssetsPath, "sim_params.json"));
            var sc = MapScenario.LoadFile(Path.Combine(Application.streamingAssetsPath, "maps/scenario_a1.json"));
            var sim = sc.Build(p, 1);
            var o = ObjectiveStatus.Compute(sim, sc);
            Assert.That(o.State, Is.EqualTo(VillageState.Red), "始まりは R2 が村にいる");
            Assert.That(o.WinIfEndedNow, Is.False);

            var b2 = sim.Find("B2");
            b2.Position = new Vec2(1975, 1000); // 村のマス
            o = ObjectiveStatus.Compute(sim, sc);
            Assert.That(o.State, Is.EqualTo(VillageState.Contested));
            Assert.That(o.WinIfEndedNow, Is.False);

            foreach (var r in sim.Companies.Where(c => c.Side == Side.Red)) r.State = CompanyState.Routed;
            o = ObjectiveStatus.Compute(sim, sc);
            Assert.That(o.State, Is.EqualTo(VillageState.Blue));
            Assert.That(o.WinIfEndedNow, Is.True, "MapScenario.BlueWins と同じ判定");

            b2.Position = new Vec2(1000, 1000);
            o = ObjectiveStatus.Compute(sim, sc);
            Assert.That(o.State, Is.EqualTo(VillageState.Empty));
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
