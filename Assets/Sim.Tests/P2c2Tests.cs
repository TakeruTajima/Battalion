using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P2c.2：意見具申（仕様「P2c.2：意見具申（確定）」）。中隊の伝令で報告・要請・宣言を本部へ送る。</summary>
    public class P2c2Tests
    {
        static Simulation Flat(SimParams p = null, IEventSink log = null)
        {
            p = p ?? TestUtil.Params;
            var sim = new Simulation(p, 1, TerrainMap.FlatCovering(p.Terrain, -1500, -1500, 1500, 1500), log);
            sim.SetHeadquarters(Side.Blue, new Vec2(0, 0));
            return sim;
        }

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg,
            Personality pers = Personality.Calm, int? strength = null) =>
            sim.AddCompany(name, side, Training.Regular, pers, new Vec2(x, y), facingDeg, strength: strength);

        static void Run(Simulation sim, double seconds, Action each = null)
        {
            int n = (int)Math.Round(seconds / sim.Params.TickSec);
            for (int i = 0; i < n; i++)
            {
                each?.Invoke();
                sim.Step();
            }
        }

        /// <summary>中隊が出した具申（伝令に載ったもの）。</summary>
        static List<Dispatch> Sent(Simulation sim, Company c) =>
            sim.Couriers.Where(r => r.CompanyId == c.Id).SelectMany(r => r.Items).ToList();

        static int Count(Simulation sim, Company c, DispatchKind k) => Sent(sim, c).Count(d => d.Kind == k);

        static void Give(Simulation sim, Order o, Discretion d = Discretion.Delegate) =>
            sim.IssueSheet(new OrderSheet(new[] { new Stage(o, StageTrigger.None, d) }));

        // ---------------------------------------------------------------- 中隊の伝令

        [Test]
        public void CourierCarriesTheDispatchToHeadquartersWithoutUsingSlots()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 300, 0, 90);
            Give(sim, Order.Advance(a.Id, new Vec2(300, 150)));
            Run(sim, 150);
            Assert.That(Count(sim, a, DispatchKind.OrdersDone), Is.EqualTo(1), "命令待ちになって「命令を終えた」");
            var c = sim.Couriers.Single();
            Assert.That(sim.RunnerSlotsUsed(Side.Blue), Is.EqualTo(0), "判断枠を使わない");
            double sent = c.SentTime;
            Run(sim, 200);
            Assert.That(c.State, Is.EqualTo(CourierState.Arrived));
            Assert.That(Vec2.Distance(c.Position, new Vec2(0, 0)), Is.LessThan(1e-6), "本部に着いた");
            var d = sim.Inbox.Single();
            Assert.That(d.Kind, Is.EqualTo(DispatchKind.OrdersDone));
            // 経路はマスの中心をたどるので、まっすぐより少し長い
            Assert.That(d.ArrivedTime.Value - sent, Is.EqualTo(10 + Vec2.Distance(new Vec2(300, 150), new Vec2(0, 0)) / 3.0).Within(10.0),
                "準備 10秒＋秒速 3m");
            Assert.That(d.SentState.StrengthRatio, Is.EqualTo(1.0));
            Assert.That(sim.LastCourierArrival(a.Id), Is.EqualTo(d.ArrivedTime));
        }

        [Test]
        public void LostCourierDeliversNothing()
        {
            var p = TestUtil.ParamsWith(("\"loss_per_sec\": 0.003", "\"loss_per_sec\": 1.0"));
            var log = new StringEventSink();
            var sim = Flat(p, log);
            var a = Add(sim, "A", Side.Blue, 300, 0, 90);
            var r = Add(sim, "R", Side.Red, 150, 250, -90, strength: 10); // 本部への道のりが全て射界（400m 以内で見える）
            Give(sim, Order.Advance(a.Id, new Vec2(300, 150)));
            Run(sim, 400, () => { a.Morale = 100; r.Morale = 100; });
            Assert.That(sim.Couriers.Count, Is.GreaterThan(0));
            Assert.That(sim.Couriers.All(c => c.State == CourierState.Lost), Is.True,
                "倒れた：" + string.Join(" / ", sim.Couriers.Select(c => $"{c.State} {c.SentTime} {c.Position.X:0},{c.Position.Y:0} " + string.Join(",", c.Items.Select(d => d.Kind)))));
            Assert.That(sim.Inbox, Is.Empty, "何も届かない");
            Assert.That(sim.LastCourierArrival(a.Id), Is.Null);
        }

        [Test]
        public void RedCompaniesDoNotSend()
        {
            var sim = Flat();
            sim.SetHeadquarters(Side.Red, new Vec2(1000, 1000));
            var r = Add(sim, "R", Side.Red, 1300, 1000, 90);
            Give(sim, Order.Advance(r.Id, new Vec2(1300, 1150)));
            Run(sim, 400);
            Assert.That(sim.Couriers, Is.Empty);
        }

        // ---------------------------------------------------------------- 報告

        [Test]
        public void OrdersDoneIsNotSentForTheInitialAwaitingButAskOrdersIsAfterFiveMinutesThenEveryTen()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 300, 0, 90);
            Run(sim, 299);
            Assert.That(Sent(sim, a), Is.Empty, "会戦の初めの命令待ちは「命令を終えた」にしない");
            Run(sim, 2);
            Assert.That(Count(sim, a, DispatchKind.AskOrders), Is.EqualTo(1), "命令待ち 5分で「命令を請う」");
            Run(sim, 598);
            Assert.That(Count(sim, a, DispatchKind.AskOrders), Is.EqualTo(1));
            Run(sim, 2);
            Assert.That(Count(sim, a, DispatchKind.AskOrders), Is.EqualTo(2), "以後 10分ごと");
            Assert.That(Count(sim, a, DispatchKind.OrdersDone), Is.EqualTo(0));
        }

        [TestCase(Personality.Calm, Discretion.Delegate, true)]
        [TestCase(Personality.Cautious, Discretion.Delegate, true)]
        [TestCase(Personality.Agile, Discretion.Delegate, true)]
        [TestCase(Personality.Reckless, Discretion.Strict, true)]
        [TestCase(Personality.Reckless, Discretion.Delegate, false)]
        public void TargetBrokenAndAskPursuitOrDeclaration(Personality pers, Discretion disc, bool asks)
        {
            var sim = Flat();
            var b = Add(sim, "B", Side.Blue, 0, -1300, 90, pers);
            var r1 = Add(sim, "R1", Side.Red, 0, -1000, -90, strength: 60);
            Give(sim, Order.Attack(b.Id, r1.Id, AttackAspect.None, AttackOnBreak.Secure), disc);
            Run(sim, 2, () => b.Morale = 100);
            r1.Morale = 0;
            Run(sim, 70, () => b.Morale = 100);
            var items = Sent(sim, b);
            Assert.That(items.Count(d => d.Kind == DispatchKind.TargetBroken && d.OtherId == r1.Id), Is.EqualTo(1), "目標崩壊");
            Assert.That(items.Count(d => d.Kind == DispatchKind.AskPursuit && d.OtherId == r1.Id), Is.EqualTo(asks ? 1 : 0), "追撃の可否");
            Assert.That(items.Count(d => d.Kind == DispatchKind.Declaration && d.DeclarationKind == "pursue"), Is.EqualTo(asks ? 0 : 1),
                "独断できる猪突は宣言");
        }

        [Test]
        public void EnemySightedOnlyForEnemiesThatComeWithin600mAfterTheStart()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 300, 0, 90);
            var near = Add(sim, "N", Side.Red, 300, 550, -90, strength: 10);
            var far = Add(sim, "F", Side.Red, 300, 1200, -90, strength: 10);
            Give(sim, Order.Hold(a.Id));
            Run(sim, 5);
            far.Position = new Vec2(300, 580);
            Run(sim, 120, () => a.Morale = 100);
            var items = Sent(sim, a).Where(d => d.Kind == DispatchKind.EnemySighted).ToList();
            Assert.That(items.Select(d => d.OtherId), Is.EquivalentTo(new[] { far.Id }), "会戦の初めから 600m 以内の N は既知。後から入った F だけ");
        }

        [TestCase(Personality.Calm, 30, 29)]
        [TestCase(Personality.Cautious, 15, 14)]
        public void HeavyLossEveryStep(Personality pers, int step, int below)
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 300, 0, 90, pers);
            Give(sim, Order.Hold(a.Id));
            Run(sim, 1);
            a.Strength = 150 - below;
            Run(sim, 61);
            Assert.That(Count(sim, a, DispatchKind.HeavyLoss), Is.EqualTo(0), $"{below}人ではまだ");
            a.Strength = 150 - step;
            Run(sim, 61);
            Assert.That(Count(sim, a, DispatchKind.HeavyLoss), Is.EqualTo(1), $"{step}人（{(double)step / 150:P0}）で損害大");
            a.Strength = 150 - 2 * step + 1;
            Run(sim, 61);
            Assert.That(Count(sim, a, DispatchKind.HeavyLoss), Is.EqualTo(1), "前の損害大から数える");
            a.Strength = 150 - 2 * step;
            Run(sim, 61);
            Assert.That(Count(sim, a, DispatchKind.HeavyLoss), Is.EqualTo(2));
        }

        [Test]
        public void AttackStalledWhenAdvanceIsShakenHalted()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 300, 0, 90);
            Give(sim, Order.Advance(a.Id, new Vec2(300, 600)));
            Run(sim, 20, () => a.Morale = 100);
            Run(sim, 70, () => a.Morale = 45);
            Assert.That(a.ShakenHalt, Is.True);
            Assert.That(Count(sim, a, DispatchKind.AttackStalled), Is.EqualTo(1));
        }

        [Test]
        public void NeighborRoutedWithin300m()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 300, 0, 90);
            var b = Add(sim, "B", Side.Blue, 550, 0, 90);
            var c = Add(sim, "C", Side.Blue, 900, 0, 90);
            Run(sim, 1);
            b.Morale = 0;
            Run(sim, 70, () => { a.Morale = 100; c.Morale = 100; });
            Assert.That(Sent(sim, a).Count(d => d.Kind == DispatchKind.NeighborRouted && d.OtherId == b.Id), Is.EqualTo(1), "250m");
            Assert.That(Sent(sim, c).Count(d => d.Kind == DispatchKind.NeighborRouted), Is.EqualTo(0), "350m は送らない");
        }

        [Test]
        public void EntrenchDone()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 300, 0, 90);
            Give(sim, Order.Entrench(a.Id));
            Run(sim, 599);
            Assert.That(Count(sim, a, DispatchKind.EntrenchDone), Is.EqualTo(0));
            Run(sim, 2);
            Assert.That(Count(sim, a, DispatchKind.EntrenchDone), Is.EqualTo(1));
        }

        // ---------------------------------------------------------------- 要請

        /// <summary>
        /// 後退の許可：動揺して撃たれている時（慎重は士気 60 未満から）。状況が変わるまで（士気がしきい値を越える、10秒撃たれない）繰り返さない。
        /// </summary>
        [TestCase(Personality.Calm, 55, false)]
        [TestCase(Personality.Calm, 45, true)]
        [TestCase(Personality.Cautious, 55, true)]
        public void AskRetreatWhenShakenAndShotOnce(Personality pers, double morale, bool asks)
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 300, 0, 90, pers);
            var r = Add(sim, "R", Side.Red, 300, 350, -90, strength: 10);
            Give(sim, Order.Hold(a.Id));
            Run(sim, 200, () => { a.Morale = morale; r.Morale = 100; });
            Assert.That(Count(sim, a, DispatchKind.AskRetreat), Is.EqualTo(asks ? 1 : 0), "撃たれ続けても1回だけ");
            if (!asks) return;
            Run(sim, 5, () => { a.Morale = 70; r.Morale = 100; }); // 状況が変わる
            Run(sim, 120, () => { a.Morale = morale; r.Morale = 100; });
            Assert.That(Count(sim, a, DispatchKind.AskRetreat), Is.EqualTo(2), "状況が変わった後はまた送る");
        }

        [Test]
        public void ApprovingAskPursuitDraftsAnAttackWithPursuit()
        {
            var d = new Dispatch { Kind = DispatchKind.AskPursuit, CompanyId = 2, OtherId = 5 };
            var draft = Simulation.DraftForRequest(d, 3);
            var o = draft.Stages[0].Order;
            Assert.That(o.Type, Is.EqualTo(OrderType.Attack));
            Assert.That(o.TargetId, Is.EqualTo(5));
            Assert.That(o.OnBreak, Is.EqualTo(AttackOnBreak.Pursue));
            Assert.That(draft.IsValid(3), Is.True);
            Assert.That(Simulation.DraftForRequest(new Dispatch { Kind = DispatchKind.AskRetreat, CompanyId = 2 }, 3).Stages.Count, Is.EqualTo(0),
                "後退の許可は白紙（行き先はプレイヤーが決める）");
            Assert.That(Simulation.DraftForRequest(new Dispatch { Kind = DispatchKind.TargetBroken, CompanyId = 2 }, 3), Is.Null);
        }

        // ---------------------------------------------------------------- 送りすぎを防ぐ

        [Test]
        public void ReportsWithin60sAreBatchedButDeclarationsGoAtOnce()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 300, 0, 90);
            var b = Add(sim, "B", Side.Blue, 550, 0, 90);
            Give(sim, Order.Entrench(a.Id));
            Run(sim, 590);
            var e1 = Add(sim, "E1", Side.Red, 300, 1300, -90, strength: 10);
            Run(sim, 1);
            b.Morale = 0; // 隣の崩壊（595秒ごろ）
            Run(sim, 6, () => a.Morale = 100); // 陣地完成（600秒）
            Assert.That(sim.Couriers.Count(c => c.CompanyId == a.Id), Is.EqualTo(1), "隣の崩壊ですぐ1人");
            Run(sim, 30, () => a.Morale = 100);
            Assert.That(sim.Couriers.Count(c => c.CompanyId == a.Id), Is.EqualTo(1), "陣地完成は 60秒たまる");
            Run(sim, 40, () => a.Morale = 100);
            var couriers = sim.Couriers.Where(c => c.CompanyId == a.Id).ToList();
            Assert.That(couriers.Count, Is.EqualTo(2));
            Assert.That(couriers[1].Items.Select(d => d.Kind), Has.Member(DispatchKind.EntrenchDone));
            Assert.That(couriers[1].SentTime - couriers[0].SentTime, Is.EqualTo(60).Within(1e-6), "前の伝令から 60秒");
        }

        [Test]
        public void DeclarationIsSentImmediatelyWithPendingReports()
        {
            var sim = Flat();
            var b = Add(sim, "B", Side.Blue, 0, -1300, 90, Personality.Reckless);
            var c = Add(sim, "C", Side.Blue, 250, -1300, 90);
            var r1 = Add(sim, "R1", Side.Red, 0, -1000, -90, strength: 60);
            Give(sim, Order.Attack(b.Id, r1.Id));
            Give(sim, Order.Hold(c.Id));
            Run(sim, 2, () => b.Morale = 100);
            r1.Morale = 0;
            Run(sim, 2, () => b.Morale = 100);
            var first = sim.Couriers.First(x => x.CompanyId == b.Id);
            // 攻撃の段が完了したので「命令を終えた」も同じ刻みにたまる（独断の追撃の間も、命令書は終えている）
            Assert.That(first.Items.Select(d => d.Kind), Is.SupersetOf(new[] { DispatchKind.TargetBroken, DispatchKind.Declaration }),
                "宣言は即座に、たまっている目標崩壊と一緒に");
            Assert.That(first.SentTime, Is.LessThanOrEqualTo(4.0));
        }

        [Test]
        public void AtMostTwoCouriersPerCompanyAndTheRestIsDropped()
        {
            var log = new StringEventSink();
            var sim = new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -1500, -1500, 1500, 1500), log);
            sim.SetHeadquarters(Side.Blue, new Vec2(-1400, -1400)); // 遠い本部：伝令は長く走る
            var a = Add(sim, "A", Side.Blue, 1000, 1000, 90);
            var es = Enumerable.Range(0, 3).Select(k => Add(sim, "E" + k, Side.Red, 1000 + 400 * k - 400, 1400 + 300, -90, strength: 10)).ToList();
            Give(sim, Order.Hold(a.Id));
            Run(sim, 1, () => a.Morale = 100); // 会戦の初めの既知の敵を数え終えてから近づける
            for (int k = 0; k < 3; k++)
            {
                es[k].Position = new Vec2(1000 + 300 * (k - 1), 1450);
                Run(sim, 61, () => a.Morale = 100);
            }
            Assert.That(sim.Couriers.Count(c => c.CompanyId == a.Id), Is.EqualTo(2), "同時に2人まで");
            StringAssert.Contains("\"type\":\"courier_dropped\",\"unit\":\"A\"", log.ToString(), "3人目の分は捨てる");
        }

        // ---------------------------------------------------------------- 戦闘の結果を変えない

        /// <summary>
        /// シナリオA の作戦（ステージ1の遠い拘束・ステージ2の段階つき、シード1）で、中隊の伝令を出す版と出さない版（同時の数 0）の、
        /// 中隊の伝令の行を除いたログが1文字も違わない（同じ環境で比べる。P2c.1.2 のハッシュとの一致は ScenarioLogHashTests.CombatLogMatchesP2c12）。
        /// </summary>
        [Test]
        public void CouriersDoNotChangeTheBattle()
        {
            var off = TestUtil.ParamsWith(("\"max_out_per_company\": 2", "\"max_out_per_company\": 0"));
            var sc1 = MapScenario.LoadFile(TestUtil.PathOf("maps/scenario_a1.json"));
            var plan = ScenarioAReportTests.PlansWithPinAndFlank(1).First(p => p.name == "拘束＋迂回（遠い拘束）").orders;
            string on1 = ScenarioLogHashTests.RunAndLog(sc1, plan, 1, out _);
            string off1 = ScenarioLogHashTests.RunAndLog(sc1, plan, 1, out _, p: off);
            StringAssert.Contains("\"type\":\"courier_delivered\"", on1, "中隊の伝令が走っている");
            StringAssert.DoesNotContain("\"type\":\"courier_sent\"", off1, "止めた版は出さない");
            Assert.That(ScenarioLogHashTests.StripCourierLines(on1, out _), Is.EqualTo(ScenarioLogHashTests.StripCourierLines(off1, out _)));

            var sc2 = MapScenario.LoadFile(TestUtil.PathOf("maps/scenario_a2.json"));
            string on2 = ScenarioLogHashTests.RunAndLog(sc2, new ScenarioAReportTests.PlannedOrder[0], 1, out _,
                s => ScenarioAReportTests.StagedPinAndFlankStart(s, 2), ScenarioAReportTests.StagedPinAndFlankTick);
            string off2 = ScenarioLogHashTests.RunAndLog(sc2, new ScenarioAReportTests.PlannedOrder[0], 1, out _,
                s => ScenarioAReportTests.StagedPinAndFlankStart(s, 2), ScenarioAReportTests.StagedPinAndFlankTick, off);
            StringAssert.Contains("\"type\":\"courier_delivered\"", on2);
            Assert.That(ScenarioLogHashTests.StripCourierLines(on2, out _), Is.EqualTo(ScenarioLogHashTests.StripCourierLines(off2, out _)));
        }
    }
}
