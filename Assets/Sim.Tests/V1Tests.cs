using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>V1：作戦目標による評価と、会戦の結果の記録（仕様「検証の道具：作戦の探索（V1・V2）」の V1）。</summary>
    public class V1Tests
    {
        static MapScenario Stage(int stage) => MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));

        static void Run(Simulation sim, BattleRecorder rec, double seconds, Action each = null)
        {
            int n = (int)Math.Round(seconds / sim.Params.TickSec);
            for (int i = 0; i < n; i++)
            {
                each?.Invoke();
                sim.Step();
                rec?.Observe();
            }
        }

        // ---------------------------------------------------------------- 作戦目標

        [Test]
        public void TrenchUnitsAreDefinedPerStageAndStartInTrenches()
        {
            Assert.That(Stage(1).TrenchUnits, Is.EqualTo(new[] { "R1" }));
            Assert.That(Stage(2).TrenchUnits, Is.EqualTo(new[] { "R1", "R2" }));
            foreach (int st in new[] { 1, 2 })
            {
                var sc = Stage(st);
                var sim = sc.Build(TestUtil.Params, 1);
                foreach (var name in sc.TrenchUnits)
                    Assert.That(sim.Map.TerrainAt(sim.Find(name).Position), Is.EqualTo(TerrainMap.Trench), $"ステージ{st} の {name} は塹壕にいる");
            }
        }

        /// <summary>
        /// ステージ2：村の予備隊 R3 を崩して青が村に入る。塹壕の R1・R2（村の中心から約 280m）は崩さない。
        /// 第一目標だけ達成（赤の目標は失敗）。
        /// </summary>
        [Test]
        public void TakingTheVillageWithoutBreakingTheTrenchesIsPrimaryOnly()
        {
            var sc = Stage(2);
            var sim = sc.Build(TestUtil.Params, 1);
            var rec = new BattleRecorder(sim, sc, 1);
            rec.Observe();
            sim.Find("R3").Morale = 0;
            Run(sim, rec, 2);
            sim.Find("B2").Position = MapScenario.VillageCenter + new Vec2(0, 60);
            Run(sim, rec, 1, () => { foreach (var c in sim.Companies) if (c.UnderCommand) c.Morale = 100; });
            var r = rec.Finish();
            Assert.That(r.Objectives.Primary, Is.True, "第一目標（村の奪取）");
            Assert.That(r.Objectives.Secondary, Is.False, "塹壕の R1・R2 は指揮下");
            Assert.That(r.Objectives.RedHolds, Is.False);
            Assert.That(r.FirstPrimarySec, Is.Not.Null);
        }

        /// <summary>同じ場面で、塹壕の R1・R2 も崩せば両方達成。</summary>
        [Test]
        public void TakingTheVillageAndBreakingTheTrenchesIsBoth()
        {
            var sc = Stage(2);
            var sim = sc.Build(TestUtil.Params, 1);
            var rec = new BattleRecorder(sim, sc, 1);
            rec.Observe();
            foreach (var n in new[] { "R1", "R2", "R3" }) sim.Find(n).Morale = 0;
            Run(sim, rec, 2);
            sim.Find("B2").Position = MapScenario.VillageCenter + new Vec2(0, 60);
            Run(sim, rec, 1, () => { foreach (var c in sim.Companies) if (c.UnderCommand) c.Morale = 100; });
            var r = rec.Finish();
            Assert.That(r.Objectives.Primary && r.Objectives.Secondary, Is.True, "両方達成");
            var r1 = r.Companies.Single(c => c.Name == "R1");
            Assert.That(r1.RoutSec.Count + (r1.SurrenderSec.HasValue ? 1 : 0), Is.GreaterThan(0), "R1 の崩れた時刻が記録に残る");
        }

        /// <summary>会戦の初めは、どちらの目標も達成していない（赤の目標は達成）。</summary>
        [Test]
        public void AtTheStartNothingIsAchieved()
        {
            foreach (int st in new[] { 1, 2 })
            {
                var sc = Stage(st);
                var sim = sc.Build(TestUtil.Params, 1);
                var ob = sc.EvaluateObjectives(sim);
                Assert.That(ob.Primary || ob.Secondary, Is.False);
                Assert.That(ob.RedHolds, Is.True);
            }
        }

        // ---------------------------------------------------------------- 記録

        /// <summary>
        /// 平地の小さな場面（青 A が北へ前進しながら赤 R と撃ち合う）で、記録の撃ち合った組と通り道が、実際の会戦と合う。
        /// </summary>
        [Test]
        public void FirePairsAndPathsMatchTheBattle()
        {
            var sim = new Simulation(TestUtil.Params, 3, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -1500, -1500, 1500, 1500));
            var a = sim.AddCompany("A", Side.Blue, Training.Regular, Personality.Calm, new Vec2(0, 0), 90);
            var r = sim.AddCompany("R", Side.Red, Training.Regular, Personality.Calm, new Vec2(0, 500), -90);
            sim.Issue(Order.Advance(a.Id, new Vec2(0, 200)));
            var rec = new BattleRecorder(sim, null, 3);
            rec.Observe();

            // 実際の会戦を、記録とは別に数える
            var pairs = new Dictionary<(string, string), (double first, double last, int ticks, int cas)>();
            var path = new List<(double t, Vec2 p)> { (0, a.Position) };
            for (int k = 0; k < 600; k++) // 300秒
            {
                a.Morale = 100;
                r.Morale = 100;
                sim.Step();
                rec.Observe();
                foreach (var f in sim.LastTickFire)
                {
                    var key = (sim.Companies[f.Shooter].Name, sim.Companies[f.Target].Name);
                    pairs[key] = pairs.TryGetValue(key, out var v)
                        ? (v.first, sim.Time, v.ticks + 1, v.cas + f.Casualties)
                        : (sim.Time, sim.Time, 1, f.Casualties);
                }
                if (Math.Abs(sim.Time % 30) < 1e-9) path.Add((sim.Time, a.Position));
            }
            var record = rec.Finish();

            Assert.That(pairs.Count, Is.EqualTo(2), "A→R と R→A");
            Assert.That(record.FirePairs.Count, Is.EqualTo(pairs.Count));
            foreach (var p in record.FirePairs)
            {
                var v = pairs[(p.Shooter, p.Target)];
                Assert.That((p.FirstSec, p.LastSec, p.Ticks, p.Casualties), Is.EqualTo((v.first, v.last, v.ticks, v.cas)), $"{p.Shooter}→{p.Target}");
            }
            Assert.That(record.FirePairs.Single(p => p.Shooter == "A").Casualties, Is.EqualTo(150 - r.Strength), "A が倒した数 = R の損害");
            Assert.That(record.BlueLost, Is.EqualTo(150 - a.Strength));
            Assert.That(record.RedLost, Is.EqualTo(150 - r.Strength));

            var rp = record.Paths["A"];
            Assert.That(rp.Select(x => x.Sec), Is.EqualTo(path.Select(x => x.t)), "30秒ごと（会戦の初めを含む）");
            for (int k = 0; k < rp.Count; k++)
                Assert.That(new Vec2(rp[k].X, rp[k].Y), Is.EqualTo(path[k].p), $"{rp[k].Sec}秒の位置");
            Assert.That(rp[0].Y, Is.LessThan(rp[rp.Count - 1].Y), "北へ進んだ");
            Assert.That(record.Paths.ContainsKey("R"), Is.False, "通り道は青だけ");
        }

        /// <summary>崩れた・降伏した時刻が記録に残り、記録が JSON として読める。</summary>
        [Test]
        public void RecordHasBreaksAndIsValidJson()
        {
            var sc = Stage(1);
            var sim = sc.Build(TestUtil.Params, 5);
            var rec = new BattleRecorder(sim, sc, 5);
            rec.Observe();
            Run(sim, rec, 10);
            sim.Find("R2").Morale = 0;
            Run(sim, rec, 5);
            var r = rec.Finish();
            r.Plan = "テスト";
            var r2 = r.Companies.Single(c => c.Name == "R2");
            Assert.That(r2.RoutSec.Count + (r2.SurrenderSec.HasValue ? 1 : 0), Is.EqualTo(1));
            Assert.That(r2.FirstBrokenSec.Value, Is.InRange(10, 11));

            var j = JsonValue.Parse(r.ToJson());
            Assert.That(j["scenario"].String, Is.EqualTo("scenario_a1"));
            Assert.That(j["plan"].String, Is.EqualTo("テスト"));
            Assert.That(j["seed"].Number, Is.EqualTo(5));
            Assert.That(j["end_sec"].Number, Is.EqualTo(15));
            Assert.That(j["objectives"]["primary"].Bool, Is.False);
            Assert.That(j["objectives"]["trench_units"].Array.Select(x => x.String), Is.EqualTo(new[] { "R1" }));
            Assert.That(j["losses"]["blue"]["initial"].Number, Is.EqualTo(600));
            Assert.That(j["companies"].Array.Count, Is.EqualTo(7));
            Assert.That(j["paths"]["B1"].Array.Count, Is.EqualTo(1), "0秒の1点（15秒まで）");
            Assert.That(j["paths"]["B1"].Array[0].Array.Count, Is.EqualTo(4), "[秒, x, y, 状態]");
        }

        /// <summary>記録を取っても、会戦とログは1文字も変わらない（ステージ1 の遠い拘束、シード1、90分）。</summary>
        [Test]
        public void RecordingDoesNotChangeTheBattle()
        {
            var sc = Stage(1);
            var plan = ScenarioAReportTests.PlansWithPinAndFlank(1).First(x => x.name == "拘束＋迂回（遠い拘束）").orders;
            string plain = ScenarioLogHashTests.RunAndLog(sc, plan, 1, out _);
            BattleRecorder rec = null;
            string recorded = ScenarioLogHashTests.RunAndLog(sc, plan, 1, out _,
                s => { rec = new BattleRecorder(s, sc, 1); rec.Observe(); },
                s => rec.Observe());
            Assert.That(recorded, Is.EqualTo(plain));
            Assert.That(rec.Finish().FirePairs.Count, Is.GreaterThan(0));
        }
    }
}
