using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P2d：霧と報告（仕様「P2d：霧と報告（確定）」）の単体テストと、場面 S23。</summary>
    public class P2dTests
    {
        static readonly Vec2 Hq = new Vec2(0, 0);

        static Simulation Flat(SimParams p = null, ulong seed = 1, IEventSink log = null)
        {
            p = p ?? TestUtil.Params;
            var sim = new Simulation(p, seed, TerrainMap.FlatCovering(p.Terrain, -1500, -1500, 1500, 1500), log);
            sim.SetHeadquarters(Side.Blue, Hq);
            return sim;
        }

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg,
            Training tr = Training.Regular, int? strength = null) =>
            sim.AddCompany(name, side, tr, Personality.Calm, new Vec2(x, y), facingDeg, strength: strength);

        static void Run(Simulation sim, double sec)
        {
            int n = (int)Math.Round(sec / sim.Params.TickSec);
            for (int k = 0; k < n; k++) sim.Step();
        }

        // ---------------------------------------------------------------- 伝令で届く・遅れる・倒れると届かない（S23）

        /// <summary>
        /// S23：600m 先の赤1個中隊を見た青の中隊の報告が、伝令の時間（本部までの距離÷秒速 3m＋準備 10秒）で本部に届き、
        /// 駒が誤差の範囲（通常 100m）に出る。届くまでは駒がない。各 20シード。
        /// </summary>
        [Test]
        public void S23ReportArrivesByCourierWithinTheError()
        {
            var p = TestUtil.Params;
            for (ulong seed = 1; seed <= 20; seed++)
            {
                var sim = Flat(p, seed);
                var a = Add(sim, "A", Side.Blue, 0, -300, 90);
                var r = Add(sim, "R", Side.Red, 0, -900, 90);
                sim.Step();
                double seen = sim.Time;
                double expect = seen + p.Couriers.PrepSec + Vec2.Distance(a.Position, Hq) / p.Runners.SpeedMps;
                double? arrived = null;
                for (int k = 0; k < 2 * 400 && !arrived.HasValue; k++)
                {
                    if (sim.Intel.Count > 0) arrived = sim.Time;
                    else sim.Step();
                }
                Assert.That(arrived, Is.Not.Null, $"シード {seed}：届いた");
                Assert.That(arrived.Value, Is.EqualTo(expect).Within(p.TickSec + 1e-9), $"シード {seed}：伝令の時間で届く");
                var piece = sim.Intel[0];
                Assert.That(Vec2.Distance(piece.Position, r.Position), Is.LessThanOrEqualTo(p.Of(Training.Regular).ReportErrorM + 1e-9));
                Assert.That(piece.ErrorM, Is.EqualTo(p.Of(Training.Regular).ReportErrorM));
                Assert.That(piece.SeenTime, Is.EqualTo(seen));
                Assert.That(piece.Size, Is.EqualTo(EnemySize.Many));
                Assert.That(piece.Moving, Is.False);
            }
        }

        /// <summary>伝令が倒れると、報告は届かない（駒ができない）。</summary>
        [Test]
        public void LostCourierBringsNoPiece()
        {
            var p = TestUtil.ParamsWith(("\"loss_per_sec\": 0.003", "\"loss_per_sec\": 1.0"));
            var sim = Flat(p);
            // 伝令の道（A から本部へ）は、赤の射界（400m）の中を通る
            Add(sim, "A", Side.Blue, 0, 300, 90);
            Add(sim, "R", Side.Red, 0, 650, -90, strength: 10);
            // 1人目（発見の報告）が倒れた後、次の伝令（60秒のまとめの後）が出る前に見る
            Run(sim, 60);
            string st = string.Join(" / ", sim.Couriers.Select(c => $"{c.State} {c.SentTime} {string.Join(",", c.Items.Select(d => d.Kind))}"));
            Assert.That(sim.Couriers.Count, Is.EqualTo(1), st);
            Assert.That(sim.Couriers[0].Items.Single().Kind, Is.EqualTo(DispatchKind.EnemySighted), st);
            Assert.That(sim.Couriers[0].State, Is.EqualTo(CourierState.Lost), st);
            Assert.That(sim.Intel, Is.Empty);
        }

        // ---------------------------------------------------------------- 中身：誤差・規模・動き

        /// <summary>位置の誤差は練度の半径の中に一様に散る（新兵 150・通常 100・古参 60m）。</summary>
        [TestCase(Training.Green)]
        [TestCase(Training.Regular)]
        [TestCase(Training.Veteran)]
        public void ReportErrorIsWithinTheTrainingRadius(Training tr)
        {
            var p = TestUtil.Params;
            double err = p.Of(tr).ReportErrorM;
            var dist = new List<double>();
            for (ulong seed = 1; seed <= 60; seed++)
            {
                var sim = Flat(p, seed);
                Add(sim, "A", Side.Blue, 0, -300, 90, tr);
                var r = Add(sim, "R", Side.Red, 0, -900, 90);
                Run(sim, 300);
                var rep = sim.Inbox.First(d => d.Report != null).Report;
                Assert.That(rep.ErrorM, Is.EqualTo(err));
                dist.Add(Vec2.Distance(rep.Position, r.Position));
            }
            Assert.That(dist.Max(), Is.LessThanOrEqualTo(err + 1e-9));
            // 円の中に一様なら、中心からの距離の平均は半径の 2/3
            Assert.That(dist.Average(), Is.EqualTo(err * 2 / 3).Within(err * 0.15));
        }

        /// <summary>規模の見積もり：7割以上は多い、4〜7割は半ば、4割未満は少ない。新兵は 20% で1段階間違える。</summary>
        [Test]
        public void SizeEstimateAndGreenMistakes()
        {
            var sim = Flat();
            var e = Add(sim, "R", Side.Red, 0, 0, 0);
            e.Strength = 105;
            Assert.That(sim.SizeOf(e), Is.EqualTo(EnemySize.Many));
            e.Strength = 104;
            Assert.That(sim.SizeOf(e), Is.EqualTo(EnemySize.Half));
            e.Strength = 60;
            Assert.That(sim.SizeOf(e), Is.EqualTo(EnemySize.Half));
            e.Strength = 59;
            Assert.That(sim.SizeOf(e), Is.EqualTo(EnemySize.Few));

            int wrong = 0, n = 200;
            int wrongRegular = 0;
            for (ulong seed = 1; seed <= (ulong)n; seed++)
                foreach (var tr in new[] { Training.Green, Training.Regular })
                {
                    var s = Flat(TestUtil.Params, seed);
                    Add(s, "A", Side.Blue, 0, -300, 90, tr);
                    Add(s, "R", Side.Red, 0, -900, 90).Strength = 80; // 初めの 150 の 53%：半ば
                    Run(s, 300);
                    var rep = s.Inbox.First(d => d.Report != null).Report;
                    if (rep.Size != EnemySize.Half)
                    {
                        if (tr == Training.Green) wrong++;
                        else wrongRegular++;
                    }
                }
            Assert.That(wrongRegular, Is.EqualTo(0));
            Assert.That(wrong / (double)n, Is.EqualTo(0.2).Within(0.07), $"新兵の間違い {wrong}/{n}");
        }

        /// <summary>続報（接敵している間 2分ごと）と変化（動き出した）の報告。</summary>
        [Test]
        public void UpdatesWhileInContactAndChangeWhenTheEnemyMoves()
        {
            var sim = Flat();
            Add(sim, "A", Side.Blue, 0, -300, 90);
            var r = Add(sim, "R", Side.Red, 0, -650, 90, strength: 20);
            Run(sim, 400);
            var kinds = sim.Couriers.SelectMany(c => c.Items).Where(d => d.Report != null).Select(d => d.Kind).ToList();
            Assert.That(kinds.First(), Is.EqualTo(DispatchKind.EnemySighted));
            Assert.That(kinds.Count(k => k == DispatchKind.EnemyUpdate), Is.GreaterThanOrEqualTo(2), "接敵中は2分ごとに続報");
            int before = kinds.Count;
            sim.Issue(Order.Advance(r.Id, new Vec2(600, -650), 0));
            Run(sim, 120);
            var after = sim.Couriers.SelectMany(c => c.Items).Where(d => d.Report != null).Skip(before).ToList();
            Assert.That(after.Any(d => d.Kind == DispatchKind.EnemyChanged && d.Report.Moving && d.Report.HeadingRad.HasValue), Is.True,
                "動き出したら変化の報告（向きつき）");
        }

        // ---------------------------------------------------------------- 本部の推定：紐付けと見え方

        static EnemyReport Rep(double x, double y, double t) =>
            new EnemyReport { ObserverId = 0, SeenTime = t, Position = new Vec2(x, y), ErrorM = 100, Size = EnemySize.Many, TrueEnemyId = -1 };

        /// <summary>報告は、位置が 300m 以内で見た時刻の差が 10分以内の駒（近い方）に紐付く。なければ新しい駒。</summary>
        [Test]
        public void ReportsLinkToNearPiecesWithinTenMinutes()
        {
            var sim = Flat();
            sim.ReceiveReport(Rep(0, 0, 0));
            sim.ReceiveReport(Rep(250, 0, 300));
            Assert.That(sim.Intel.Count, Is.EqualTo(1), "300m 以内・5分後は同じ駒");
            Assert.That(sim.Intel[0].Position, Is.EqualTo(new Vec2(250, 0)), "駒は新しい報告の位置へ");
            sim.ReceiveReport(Rep(600, 0, 400));
            Assert.That(sim.Intel.Count, Is.EqualTo(2), "350m 離れた報告は新しい駒");
            sim.ReceiveReport(Rep(250, 50, 1000));
            Assert.That(sim.Intel.Count, Is.EqualTo(3), "見た時刻の差が 10分を超えた駒には紐付かない");
            sim.ReceiveReport(Rep(0, 0, 1100));
            Assert.That(sim.Intel.Count, Is.EqualTo(3));
            Assert.That(sim.Intel[2].Reports.Count, Is.EqualTo(2));

            // 2つの駒の 300m 以内なら、近い方に紐付く
            var s2 = Flat();
            s2.ReceiveReport(Rep(0, 0, 0));
            s2.ReceiveReport(Rep(400, 0, 0));
            s2.ReceiveReport(Rep(250, 0, 10));
            Assert.That(s2.Intel.Count, Is.EqualTo(2));
            Assert.That(s2.Intel[1].Reports.Count, Is.EqualTo(2));
            // 古い報告が後から届いても、駒の位置は戻さない
            s2.ReceiveReport(Rep(100, 0, 5));
            Assert.That(s2.Intel[0].Position, Is.EqualTo(new Vec2(0, 0)).Or.EqualTo(new Vec2(100, 0)));
        }

        /// <summary>見た時刻から 5分で薄く、10分で「?」、30分で地図から消える。</summary>
        [Test]
        public void PieceFadesThenBecomesUnknownThenIsForgotten()
        {
            var sim = Flat();
            var p = sim.AddInitialIntel(new Vec2(0, 0), EnemySize.Many);
            Assert.That(p.Initial, Is.True);
            Assert.That(sim.LookOf(p, 299), Is.EqualTo(PieceLook.Fresh));
            Assert.That(sim.LookOf(p, 300), Is.EqualTo(PieceLook.Faded));
            Assert.That(sim.LookOf(p, 600), Is.EqualTo(PieceLook.Unknown));
            Assert.That(sim.LookOf(p, 1800), Is.EqualTo(PieceLook.Forgotten));
        }

        /// <summary>地図ファイルの initial_intel（シナリオA は R1・R2、教程は塹壕の赤の全て）。</summary>
        [TestCase("maps/scenario_a1.json", 2)]
        [TestCase("maps/scenario_a2.json", 2)]
        [TestCase("maps/tutorial_1.json", 2)]
        [TestCase("maps/tutorial_2.json", 1)]
        public void MapsHaveInitialIntel(string path, int count)
        {
            var sc = MapScenario.LoadFile(TestUtil.PathOf(path));
            var sim = sc.Build(TestUtil.Params, 1);
            Assert.That(sim.Intel.Count, Is.EqualTo(count));
            Assert.That(sim.Intel.All(i => i.Initial && i.SeenTime == 0 && i.Size == EnemySize.Many), Is.True);
            foreach (var i in sim.Intel)
                Assert.That(sim.Companies.Where(c => c.Side == Side.Red).Min(c => Vec2.Distance(c.Position, i.Position)), Is.LessThan(1e-6));
        }

        // ---------------------------------------------------------------- 報告の駒への命令

        /// <summary>駒への攻撃：駒の位置から 300m 以内の気付いている指揮下の敵のうち、駒に最も近い敵を目標にする。</summary>
        [Test]
        public void AttackOnAPieceTargetsTheNearestAwareEnemy()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, -300, 90);
            var near = Add(sim, "N", Side.Red, 250, -800, -90);
            Add(sim, "F", Side.Red, -250, -800, -90);
            sim.Issue(Order.AttackPiece(a.Id, 0, new Vec2(150, -800)));
            sim.Step();
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Attack));
            Assert.That(a.CurrentOrder.TargetId, Is.EqualTo(near.Id));
            Assert.That(a.PieceOrder, Is.Null);
        }

        /// <summary>駒の近くに気付いている敵がいなければ、駒の位置へ向かい、見えたらその敵への拘束を始める。</summary>
        [Test]
        public void PinOnAPieceMovesThereUntilItSeesTheEnemy()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, -300, 90);
            // 駒の位置から 280m 離れた敵。初めは視界（800m）の外
            var r = Add(sim, "R", Side.Red, 280, -1400, -90);
            sim.Issue(Order.PinPiece(a.Id, 0, new Vec2(0, -1400)));
            sim.Step();
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Advance), "見えるまで駒の位置へ前進");
            Assert.That(a.PieceOrder, Is.Not.Null);
            for (int k = 0; k < 2 * 600 && a.PieceOrder != null; k++) sim.Step();
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Pin));
            Assert.That(a.CurrentOrder.TargetId, Is.EqualTo(r.Id));
        }

        /// <summary>駒の位置に着いても 300m 以内に気付いている敵がいなければ、その場で保持し「敵を見ず」を具申する（要請）。</summary>
        [Test]
        public void NoEnemyAtThePieceHoldsAndAsksForOrders()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, -300, 90);
            Add(sim, "R", Side.Red, 900, -700, -90, strength: 10); // 駒から 500m
            sim.Issue(Order.AttackPiece(a.Id, 0, new Vec2(400, -700)));
            sim.Step();
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Advance));
            for (int k = 0; k < 2 * 900 && a.CurrentOrder.Type != OrderType.Hold; k++) sim.Step();
            Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            Assert.That(Vec2.Distance(a.Position, new Vec2(400, -700)), Is.LessThan(60));
            Run(sim, 120);
            var noEnemy = sim.Couriers.SelectMany(c => c.Items).Where(d => d.Kind == DispatchKind.NoEnemySeen).ToList();
            Assert.That(noEnemy.Count, Is.EqualTo(1));
            Assert.That(noEnemy[0].Category, Is.EqualTo(DispatchCategory.Request));
        }
    }

    /// <summary>P2d は会戦の結果を変えない：報告の規則の入り切りで、戦闘のログ（中隊の伝令の行を除く）が1文字も違わない。</summary>
    [Category("Slow")]
    public class P2dBattleUnchangedTests
    {
        [Test]
        public void ReportsDoNotChangeTheBattle()
        {
            var off = TestUtil.WithoutP2d(TestUtil.ParamsWith());
            var mismatched = new List<string>();
            int n = 0;
            foreach (int stage in new[] { 1, 2 })
            {
                var sc = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{stage}.json"));
                foreach (var (name, plan) in ScenarioAReportTests.PlansWithPinAndFlank(stage))
                    foreach (ulong seed in new ulong[] { 1, 2 })
                    {
                        string a = ScenarioLogHashTests.StripCourierLines(ScenarioLogHashTests.RunAndLog(sc, plan, seed, out _), out int la);
                        string b = ScenarioLogHashTests.StripCourierLines(ScenarioLogHashTests.RunAndLog(sc, plan, seed, out _, p: off), out int lb);
                        n++;
                        if (a != b) mismatched.Add($"stage{stage} {name} seed{seed} {la}/{lb}");
                    }
            }
            TestContext.WriteLine($"比べた会戦 {n}、違う {mismatched.Count}");
            Assert.That(mismatched, Is.Empty);
        }
    }
}
