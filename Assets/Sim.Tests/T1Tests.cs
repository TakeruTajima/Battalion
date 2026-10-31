using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>T1：教程1（側面を突く）の地図の読み込みと、作戦目標 trench_clear の判定（仕様「ステージの作り直し：教程」）。</summary>
    public class T1Tests
    {
        public const string TutorialPath = "maps/tutorial_1.json";

        static MapScenario Tutorial() => MapScenario.LoadFile(TestUtil.PathOf(TutorialPath));

        // ---------------------------------------------------------------- 地図の読み込み

        [Test]
        public void LoadsTheTutorialMap()
        {
            var sc = Tutorial();
            Assert.That(sc.Objective, Is.EqualTo(ObjectiveKind.TrenchClear));
            Assert.That(sc.TrenchUnits, Is.EqualTo(new[] { "R1", "R2" }));
            Assert.That(sc.TrenchCells.Count, Is.EqualTo(8), "塹壕は x 950〜1000・y 500〜900 の8マス");
            Assert.That(sc.TrenchCells.All(c => c.i == 19 && c.j >= 10 && c.j <= 17), Is.True);
            var map = sc.Build(TestUtil.Params, 1).Map;
            char At(double x, double y)
            {
                map.CellOf(new Vec2(x, y), out int i, out int j);
                return map.TerrainAt(i, j);
            }
            Assert.That(At(975, 1000), Is.EqualTo(TerrainMap.Forest), "北の森");
            Assert.That(At(975, 400), Is.EqualTo(TerrainMap.Forest), "南の森");
            Assert.That(At(700, 700), Is.EqualTo(TerrainMap.Plain), "森に挟まれた平地");
            Assert.That(sc.VillageCells.Count, Is.EqualTo(0), "村のない地図");
            Assert.That(sc.ReserveUnit, Is.Null, "予備はいない");
            Assert.That(sc.Defense.Units.Keys, Is.EquivalentTo(new[] { "R1", "R2" }));
            Assert.That(sc.Defense.Units["R1"].Role, Is.EqualTo(DefenseRole.Garrison));
            Assert.That(sc.Defense.Units["R2"].Role, Is.EqualTo(DefenseRole.Garrison));
            Assert.That(sc.BlueHq, Is.EqualTo(new Vec2(100, 700)));
            Assert.That(sc.RedHq, Is.EqualTo(new Vec2(1450, 700)));
            Assert.That(sc.Briefing, Does.StartWith("敵の2個中隊が、森に挟まれた"));
            Assert.That(sc.Title, Is.EqualTo("教程1：側面を突く"));
            Assert.That(sc.TimeLimitMin, Is.EqualTo(40));

            var sim = sc.Build(TestUtil.Params, 1);
            Assert.That(sim.Companies.Select(c => c.Name), Is.EqualTo(new[] { "B1", "B2", "B3", "R1", "R2" }));
            Assert.That(sim.Find("B1").Personality, Is.EqualTo(Personality.Cautious));
            Assert.That(sim.Find("B3").Training, Is.EqualTo(Training.Veteran));
            Assert.That(sim.Find("B3").Position, Is.EqualTo(new Vec2(250, 700)), "B3 は2個の後ろ");
            Assert.That(sc.InTrench(sim, sim.Find("R1")), Is.True);
            Assert.That(sc.InTrench(sim, sim.Find("R2")), Is.True);
            Assert.That(sc.ObjectivePoint(sim.Map), Is.EqualTo(new Vec2(975, 700)));
        }

        [TestCase("maps/scenario_a1.json")]
        [TestCase("maps/scenario_a2.json")]
        public void ScenarioAMapsReadAsBefore(string path)
        {
            var sc = MapScenario.LoadFile(TestUtil.PathOf(path));
            Assert.That(sc.Objective, Is.EqualTo(ObjectiveKind.Village));
            Assert.That(sc.VillageCells.Count, Is.EqualTo(16));
            Assert.That(sc.BlueHq, Is.EqualTo(MapScenario.BlueHeadquarters));
            Assert.That(sc.RedHq, Is.EqualTo(MapScenario.RedHeadquarters));
            Assert.That(sc.ReserveUnit, Is.EqualTo("R3"));
            Assert.That(sc.Briefing, Is.Null);
            Assert.That(sc.TrenchUnits, Is.EqualTo(MapScenario.TrenchUnitsOf(sc.Stage)));
            Assert.That(sc.ObjectivePoint(sc.Build(TestUtil.Params, 1).Map), Is.EqualTo(MapScenario.VillageCenter));
        }

        /// <summary>守りを止めた（defense.enabled が false）ときも、予備のない地図は予備隊の台本なしで作れる。</summary>
        [Test]
        public void BuildsWithoutAReserveWhenDefenseIsOff()
        {
            var p = TestUtil.ParamsWithoutE1();
            var sim = Tutorial().Build(p, 1);
            for (int k = 0; k < 10; k++) sim.Step();
            Assert.That(sim.Companies.Count, Is.EqualTo(5));
        }

        // ---------------------------------------------------------------- trench_clear の判定

        [Test]
        public void BreakingAndSecuringTheTrenchWins()
        {
            var sc = Tutorial();
            var sim = sc.Build(TestUtil.Params, 1);
            var r1 = sim.Find("R1");
            var r2 = sim.Find("R2");
            var b2 = sim.Find("B2");
            var times = new TrenchClearTimes(sc);
            times.Observe(sim);
            Assert.That(sc.BlueWins(sim), Is.False, "塹壕に指揮下の赤がいる");
            Assert.That(sc.TrenchUnitsBroken(sim), Is.False);
            Assert.That(times.BrokenSec, Is.Null);

            // 片方だけ崩しても、崩したことにならない
            sim.Step();
            r1.State = CompanyState.Routed;
            r1.Position = new Vec2(1300, 800);
            times.Observe(sim);
            Assert.That(sc.TrenchUnitsBroken(sim), Is.False, "R2 がまだ塹壕にいる");
            Assert.That(times.BrokenSec, Is.Null);
            Assert.That(sc.BlueWins(sim), Is.False);

            // 崩した：R2 も壊走して塹壕を出る
            sim.Step();
            r2.State = CompanyState.Routed;
            r2.Position = new Vec2(1300, 600);
            times.Observe(sim);
            Assert.That(sc.TrenchUnitsBroken(sim), Is.True);
            Assert.That(times.BrokenSec, Is.EqualTo(sim.Time));
            Assert.That(sc.TrenchSecured(sim), Is.False, "崩しただけで、まだ入っていない");
            Assert.That(sc.BlueWins(sim), Is.True, "塹壕に指揮下の赤がいなければ勝ち（確保は勝ちの条件に入らない）");

            // 確保：B2 が塹壕に入る
            sim.Step();
            b2.Position = new Vec2(975, 700);
            times.Observe(sim);
            Assert.That(sc.TrenchSecured(sim), Is.True);
            Assert.That(times.SecuredSec, Is.EqualTo(sim.Time));
            Assert.That(times.SecuredSec, Is.GreaterThan(times.BrokenSec));
            Assert.That(sc.BlueWins(sim), Is.True);
            Assert.That(sc.EvaluateObjectives(sim).Primary, Is.True);
        }

        /// <summary>崩しただけで確保しなければ、再集結した赤が塹壕に戻ると勝ちでなくなる。</summary>
        [Test]
        public void RedThatReturnsToTheTrenchUndoesTheWin()
        {
            var sc = Tutorial();
            var sim = sc.Build(TestUtil.Params, 1);
            var r1 = sim.Find("R1");
            r1.State = CompanyState.Routed;
            r1.Position = new Vec2(1300, 800);
            sim.Find("R2").State = CompanyState.Surrendered;
            Assert.That(sc.BlueWins(sim), Is.True);
            r1.State = CompanyState.Rallying;
            Assert.That(sc.BlueWins(sim), Is.True, "再集結中は指揮下でない");
            r1.State = CompanyState.Normal;
            r1.Position = new Vec2(975, 650);
            Assert.That(sc.BlueWins(sim), Is.False, "戻った赤が塹壕にいる");
            Assert.That(sc.TrenchSecured(sim), Is.False);
            // 指揮下の赤でも、中心が塹壕のマスの外なら勝ち
            r1.Position = new Vec2(1100, 650);
            Assert.That(sc.BlueWins(sim), Is.True);
        }

        /// <summary>赤が全て盤上から消えたら（降伏・離脱）勝ち。指揮下の青がいなければ負け。</summary>
        [Test]
        public void RedLeavingTheBoardWinsAndNoBlueLoses()
        {
            var sc = Tutorial();
            var sim = sc.Build(TestUtil.Params, 1);
            sim.Find("R1").State = CompanyState.Surrendered;
            Assert.That(sc.BlueWins(sim), Is.False, "R2 が塹壕にいる");
            sim.Find("R2").State = CompanyState.Surrendered;
            Assert.That(sc.BlueWins(sim), Is.True);
            foreach (var c in sim.Companies.Where(c => c.Side == Side.Blue)) c.State = CompanyState.Rallying;
            Assert.That(sc.BlueWins(sim), Is.False, "指揮下の青が1個以上残っていること");
        }
    }
}
