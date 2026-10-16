using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>
    /// 地図の読み込みと、視線・露出・経路探索を docs/reference/map/map_reference_results_a1.json / _a2.json と照合する。
    /// </summary>
    public class MapReferenceTests
    {
        public static readonly string[] Stages = { "a1", "a2" };

        static MapScenario Scenario(string stage) => MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_{stage}.json"));
        static TerrainMap Map(string stage) => TerrainMap.FromJson(Scenario(stage).Source, TestUtil.Params.Terrain);
        static JsonValue Ref(string stage) =>
            JsonValue.Parse(File.ReadAllText(TestUtil.PathOf($"docs/reference/map/map_reference_results_{stage}.json")));

        static List<Vec2> RedDeploy(MapScenario s) => s.Units.Where(u => u.Side == Side.Red).Select(u => new Vec2(u.X, u.Y)).ToList();

        [TestCaseSource(nameof(Stages))]
        public void LoadsRowsNorthFirst(string stage)
        {
            var m = Map(stage);
            Assert.That(m.Width, Is.EqualTo(60));
            Assert.That(m.Height, Is.EqualTo(40));
            Assert.That(m.TerrainAt(38, 18), Is.EqualTo(TerrainMap.Village), "村のマス（objects.village の先頭）");
            Assert.That(m.TerrainAt(40, 5), Is.EqualTo(TerrainMap.Bridge));
            Assert.That(m.TerrainAt(20, 6), Is.EqualTo(TerrainMap.Ford));
            Assert.That(m.TerrainAt(0, 5), Is.EqualTo(TerrainMap.River));
            Assert.That(m.TerrainAt(0, 0), Is.EqualTo(TerrainMap.Plain));
            Assert.That(m.TerrainAt(new Vec2(1000, 1650)), Is.EqualTo(TerrainMap.Forest), "北の森");
            var s = Scenario(stage);
            Assert.That(s.Units.Count, Is.EqualTo(7));
            Assert.That(s.VillageCells.Count, Is.EqualTo(16));
            Assert.That(s.TimeLimitMin, Is.EqualTo(90));
        }

        [TestCaseSource(nameof(Stages))]
        public void LineOfSightMatchesReference(string stage)
        {
            var m = Map(stage);
            foreach (var kv in Ref(stage)["los"].Object)
            {
                var from = kv.Value["from"].Array;
                var to = kv.Value["to"].Array;
                var r = LineOfSight.Check(m, TestUtil.Params.Sight, new Vec2(from[0].Number, from[1].Number), new Vec2(to[0].Number, to[1].Number));
                TestContext.WriteLine($"{stage} {kv.Key}: {LineOfSight.Name(r)}（リファレンス {kv.Value["reason"].String}）");
                Assert.That(LineOfSight.Name(r), Is.EqualTo(kv.Value["reason"].String), kv.Key);
                Assert.That(r == SightResult.Clear, Is.EqualTo(kv.Value["visible"].Bool), kv.Key);
            }
        }

        [TestCaseSource(nameof(Stages))]
        public void ExposureCellCountMatchesReference(string stage)
        {
            var m = Map(stage);
            var exp = Visibility.Exposure(m, TestUtil.Params.Sight, RedDeploy(Scenario(stage)), 400);
            Assert.That(exp.Count(x => x), Is.EqualTo((int)Ref(stage).D("exposed_cells")));
        }

        [TestCaseSource(nameof(Stages))]
        public void PathsMatchReference(string stage)
        {
            var m = Map(stage);
            var exp = Visibility.Exposure(m, TestUtil.Params.Sight, RedDeploy(Scenario(stage)), 400);
            var weights = new Dictionary<string, double> { ["cautious"] = 3.0, ["calm"] = 1.0, ["reckless"] = 0.3 };
            var exposed = new Dictionary<string, double>();
            foreach (var kv in Ref(stage)["paths"].Object)
            {
                var r = kv.Value;
                string pers = kv.Key.Substring(kv.Key.LastIndexOf('_') + 1);
                var from = r["from"].Array;
                var to = r["to"].Array;
                m.CellOf(new Vec2(from[0].Number, from[1].Number), out int si, out int sj);
                m.CellOf(new Vec2(to[0].Number, to[1].Number), out int gi, out int gj);
                var path = PathFinder.Find(m, new PathFinder.Cell(si, sj), new PathFinder.Cell(gi, gj), exp, weights[pers], TestUtil.Params.MapRules.ExposureCostM, out double cost);
                PathFinder.Stats(m, path, exp, out double len, out double expM);
                double refLen = r.D("length_m"), refExp = r.D("exposed_m");
                bool sameCells = path.Count == r["path"].Array.Count &&
                                 path.Select((c, k) => c.I == (int)r["path"].Array[k].Array[0].Number && c.J == (int)r["path"].Array[k].Array[1].Number).All(x => x);
                TestContext.WriteLine($"{stage} {kv.Key}: 長さ {len:F1}（リファレンス {refLen}）, 露出 {expM:F1}（{refExp}）, " +
                                      $"コスト {cost:F1}（{r.D("cost")}）, マスの列 {(sameCells ? "一致" : "不一致")}");
                Assert.That(Math.Abs(len / refLen - 1), Is.LessThanOrEqualTo(0.05), kv.Key + " の経路長");
                Assert.That(Math.Abs(expM - refExp), Is.LessThanOrEqualTo(71), kv.Key + " の露出距離");
                exposed[kv.Key] = expM;
            }

            // 南からの接近で、露出が「慎重≦冷静＜猪突」
            double cautious = exposed["P_south_approach_cautious"], calm = exposed["P_south_approach_calm"], reckless = exposed["P_south_approach_reckless"];
            Assert.That(cautious, Is.LessThanOrEqualTo(calm));
            Assert.That(calm, Is.LessThan(reckless));
            if (stage == "a1") Assert.That(cautious, Is.EqualTo(calm), "ステージ1では慎重と冷静が同じ経路");
        }

        [Test]
        public void WaypointsDropCollinearCells()
        {
            var m = TerrainMap.Flat(TestUtil.Params.Terrain, 0, 0, 10, 10);
            var path = new List<PathFinder.Cell>
            {
                new PathFinder.Cell(0, 0), new PathFinder.Cell(1, 0), new PathFinder.Cell(2, 0),
                new PathFinder.Cell(3, 1), new PathFinder.Cell(4, 2), new PathFinder.Cell(4, 3),
            };
            var pts = PathFinder.ToWaypoints(m, path, new Vec2(222, 177));
            Assert.That(pts, Is.EqualTo(new[] { m.CellCenter(2, 0), m.CellCenter(4, 2), new Vec2(222, 177) }));
        }

        [Test]
        public void PathDoesNotCrossRiverExceptBridgeOrFord()
        {
            var m = Map("a1");
            var path = PathFinder.Find(m, new PathFinder.Cell(30, 2), new PathFinder.Cell(30, 10), null, 0, TestUtil.Params.MapRules.ExposureCostM, out _);
            Assert.That(path, Is.Not.Null);
            Assert.That(path.All(c => m.Passable(c.I, c.J)), Is.True);
            Assert.That(path.Any(c => m.TerrainAt(c.I, c.J) == TerrainMap.Bridge || m.TerrainAt(c.I, c.J) == TerrainMap.Ford), Is.True);
        }
    }
}
