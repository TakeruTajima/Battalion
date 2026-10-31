using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>T2：教程2（抑えてから詰める）の地図の読み込み（仕様「教程2：抑えてから詰める（T4）」）。</summary>
    public class T2Tests
    {
        public const string TutorialPath = "maps/tutorial_2.json";

        static MapScenario Tutorial() => MapScenario.LoadFile(TestUtil.PathOf(TutorialPath));

        [Test]
        public void LoadsTheTutorialMap()
        {
            var sc = Tutorial();
            Assert.That(sc.Title, Is.EqualTo("教程2：抑えてから詰める"));
            Assert.That(sc.Briefing, Does.StartWith("敵の1個中隊が、二つの湖に挟まれた幅 150m の地峡を"));
            Assert.That(sc.Briefing, Does.Contain("35分以内に"));
            Assert.That(sc.TimeLimitMin, Is.EqualTo(35));
            Assert.That(sc.Objective, Is.EqualTo(ObjectiveKind.TrenchClear));
            Assert.That(sc.TrenchUnits, Is.EqualTo(new[] { "R1" }));
            Assert.That(sc.TrenchCells.Count, Is.EqualTo(3), "塹壕は x 950〜1000・y 650〜800 の3マス");
            Assert.That(sc.TrenchCells.All(c => c.i == 19 && c.j >= 13 && c.j <= 15), Is.True);
            Assert.That(sc.VillageCells.Count, Is.EqualTo(0));
            Assert.That(sc.ReserveUnit, Is.Null);
            Assert.That(sc.Defense.Units.Keys, Is.EquivalentTo(new[] { "R1" }));
            Assert.That(sc.Defense.Units["R1"].Role, Is.EqualTo(DefenseRole.Garrison));
            Assert.That(sc.BlueHq, Is.EqualTo(new Vec2(100, 700)));
            Assert.That(sc.RedHq, Is.EqualTo(new Vec2(1450, 700)));

            var sim = sc.Build(TestUtil.Params, 1);
            Assert.That(sim.Companies.Select(c => c.Name), Is.EqualTo(new[] { "B1", "B2", "B3", "R1" }));
            Assert.That(sim.Find("B1").Position, Is.EqualTo(new Vec2(300, 1000)));
            Assert.That(sim.Find("B2").Position, Is.EqualTo(new Vec2(300, 700)));
            Assert.That(sim.Find("B3").Position, Is.EqualTo(new Vec2(300, 400)));
            Assert.That(sim.Find("B3").Personality, Is.EqualTo(Personality.Reckless));
            Assert.That(sim.Find("B3").Training, Is.EqualTo(Training.Veteran));
            Assert.That(sim.Find("R1").Position, Is.EqualTo(new Vec2(975, 725)));
            Assert.That(sc.InTrench(sim, sim.Find("R1")), Is.True);
            Assert.That(sc.ObjectivePoint(sim.Map), Is.EqualTo(new Vec2(975, 725)));
        }

        /// <summary>二つの湖（x 700〜1250）は地図の南北の端まで続き、通れるのは y 650〜800 の地峡（幅 150m）だけ。西（x 0〜700）は平地。</summary>
        [Test]
        public void LakesCloseEverythingButTheIsthmus()
        {
            var map = Tutorial().Build(TestUtil.Params, 1).Map;
            for (int i = 0; i < map.Width; i++)
                for (int j = 0; j < map.Height; j++)
                {
                    var p = map.CellCenter(i, j);
                    char t = map.TerrainAt(i, j);
                    bool lakeBand = p.X > 700 && p.X < 1250;
                    bool isthmus = p.Y > 650 && p.Y < 800;
                    if (lakeBand && !isthmus) Assert.That(t, Is.EqualTo(TerrainMap.River), $"湖 ({p.X}, {p.Y})");
                    else if (lakeBand && p.X > 950 && p.X < 1000) Assert.That(t, Is.EqualTo(TerrainMap.Trench), $"塹壕 ({p.X}, {p.Y})");
                    else Assert.That(t, Is.EqualTo(TerrainMap.Plain), $"平地 ({p.X}, {p.Y})");
                    if (t == TerrainMap.River) Assert.That(map.MoveMult(t), Is.LessThanOrEqualTo(0), "湖は通れない");
                }
        }
    }
}
