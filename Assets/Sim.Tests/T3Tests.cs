using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>T3：教程3（砲撃に合わせて詰める）の地図の読み込み（仕様「教程3：砲撃に合わせて詰める（T17、P3 の後）」）。</summary>
    public class T3Tests
    {
        public const string TutorialPath = "maps/tutorial_3.json";

        static MapScenario Tutorial() => MapScenario.LoadFile(TestUtil.PathOf(TutorialPath));

        [Test]
        public void LoadsTheTutorialMap()
        {
            var sc = Tutorial();
            Assert.That(sc.Title, Is.EqualTo("教程3：砲撃に合わせて詰める"));
            Assert.That(sc.Briefing, Does.StartWith("敵の1個中隊が、二つの湖に挟まれた幅 150m の地峡を"));
            Assert.That(sc.Briefing, Does.Contain("野砲中隊（40発、5分ぶん）"));
            Assert.That(sc.Briefing, Does.Contain("30分以内に"));
            Assert.That(sc.TimeLimitMin, Is.EqualTo(30));
            Assert.That(sc.Objective, Is.EqualTo(ObjectiveKind.TrenchClear));
            Assert.That(sc.TrenchUnits, Is.EqualTo(new[] { "R1" }));
            Assert.That(sc.TrenchCells.Count, Is.EqualTo(3));
            Assert.That(sc.Defense.Units.Keys, Is.EquivalentTo(new[] { "R1" }));
            Assert.That(sc.Defense.Units["R1"].Role, Is.EqualTo(DefenseRole.Garrison));
            Assert.That(sc.Defense.ArtilleryPoints, Is.Empty);

            var sim = sc.Build(TestUtil.Params, 1);
            Assert.That(sim.Companies.Select(c => c.Name), Is.EqualTo(new[] { "B1", "B2", "R1" }));
            Assert.That(sim.Find("B1").Position, Is.EqualTo(new Vec2(300, 875)));
            Assert.That(sim.Find("B2").Position, Is.EqualTo(new Vec2(300, 575)));
            Assert.That(sim.Find("B1").Personality, Is.EqualTo(Personality.Cautious));
            Assert.That(sim.Find("B2").Personality, Is.EqualTo(Personality.Calm));
            Assert.That(sim.Find("R1").Position, Is.EqualTo(new Vec2(975, 725)));
            Assert.That(sc.InTrench(sim, sim.Find("R1")), Is.True);
            // 砲兵：青だけ 40発。会戦前に計画射撃点を登録できる
            Assert.That(sim.RoundsLeft(Side.Blue), Is.EqualTo(40));
            Assert.That(sim.HasArtillery(Side.Red), Is.False);
            Assert.That(sim.AddPlannedPoint(Side.Blue, new Vec2(975, 725)), Is.True);
        }

        /// <summary>地形は教程2 と同じ（地峡・湖・塹壕）。</summary>
        [Test]
        public void TerrainIsTheSameAsTutorial2()
        {
            var a = Tutorial().Build(TestUtil.Params, 1).Map;
            var b = MapScenario.LoadFile(TestUtil.PathOf(T2Tests.TutorialPath)).Build(TestUtil.Params, 1).Map;
            for (int i = 0; i < a.Width; i++)
                for (int j = 0; j < a.Height; j++)
                {
                    Assert.That(a.TerrainAt(i, j), Is.EqualTo(b.TerrainAt(i, j)));
                    Assert.That(a.ElevationAt(i, j), Is.EqualTo(b.ElevationAt(i, j)));
                }
        }
    }
}
