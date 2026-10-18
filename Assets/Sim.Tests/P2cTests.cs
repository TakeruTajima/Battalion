using System;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P2c：中隊長の自律行動（仕様「P2c：中隊長の自律行動（確定）」）。「バカ」と「無為」を防ぐ確かめ。</summary>
    public class P2cTests
    {
        static Simulation Flat(IEventSink log = null) =>
            new Simulation(TestUtil.Params, 1, TerrainMap.FlatCovering(TestUtil.Params.Terrain, -1500, -1500, 1500, 1500), log);

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg,
            Personality pers = Personality.Calm, double? morale = null, int? strength = null) =>
            sim.AddCompany(name, side, Training.Regular, pers, new Vec2(x, y), facingDeg, strength: strength, morale: morale);

        static void Give(Simulation sim, Order o, Discretion d) =>
            sim.IssueSheet(new OrderSheet(new[] { new Stage(o, StageTrigger.None, d) }));

        static void Run(Simulation sim, double seconds, Action each = null)
        {
            int n = (int)Math.Round(seconds / sim.Params.TickSec);
            for (int i = 0; i < n; i++)
            {
                each?.Invoke();
                sim.Step();
            }
        }

        /// <summary>中心が <paramref name="p"/> のマスを地形 <paramref name="ch"/> にする。</summary>
        static void SetCell(Simulation sim, Vec2 p, char ch)
        {
            sim.Map.CellOf(p, out int i, out int j);
            sim.Map.SetOverlay(i, j, ch);
        }

        static double Deg(double rad) => rad * Angles.RadToDeg;

        // ---------------------------------------------------------------- 厳命は持ち場から 100m を越えない

        /// <summary>
        /// 開けた所で撃たれ続ける保持の中隊。物陰（森）は持ち場から 150m の真横にだけある。
        /// 厳命ならどの性格でも動かない（100m を越えない）。慎重の任せるだけが、独断でそこへ下がる。
        /// </summary>
        [TestCase(Personality.Calm)]
        [TestCase(Personality.Cautious)]
        [TestCase(Personality.Reckless)]
        [TestCase(Personality.Agile)]
        public void StrictCompanyStaysWithin100mOfItsPost(Personality pers)
        {
            foreach (var d in new[] { Discretion.Strict, Discretion.Delegate })
            {
                var log = new StringEventSink();
                var sim = Flat(log);
                var a = Add(sim, "A", Side.Blue, 25, 25, 90, pers);
                var r = Add(sim, "R", Side.Red, 25, 375, -90, strength: 40);
                SetCell(sim, new Vec2(175, 25), TerrainMap.Forest);
                Give(sim, Order.Hold(a.Id, 90 * Angles.DegToRad), d);
                Give(sim, Order.Hold(r.Id), Discretion.Strict);
                double far = 0;
                Run(sim, 600, () => { a.Morale = 90; r.Morale = 90; far = Math.Max(far, Vec2.Distance(a.Position, new Vec2(25, 25))); });
                if (d == Discretion.Strict || pers != Personality.Cautious)
                {
                    Assert.That(far, Is.LessThanOrEqualTo(100), $"{pers} {d}：持ち場から 100m を越えない");
                }
                else
                {
                    Assert.That(Vec2.Distance(a.Position, new Vec2(175, 25)), Is.LessThan(1), "慎重の任せるは 200m まで物陰を探す");
                    StringAssert.Contains("\"kind\":\"cover\"", log.ToString(), "独断の宣言");
                }
            }
        }

        // ---------------------------------------------------------------- 冷静は独断しない

        [Test]
        public void DelegatedCalmBehavesExactlyLikeStrictCalm()
        {
            string RunWith(Discretion d)
            {
                var log = new StringEventSink();
                var sim = Flat(log);
                var a = Add(sim, "A", Side.Blue, 0, -600, 90);
                var g = Add(sim, "G", Side.Blue, 400, -600, 90);
                var r1 = Add(sim, "R1", Side.Red, 0, 0, -90, morale: 55);
                var r2 = Add(sim, "R2", Side.Red, 450, 100, -90, morale: 45);
                SetCell(sim, new Vec2(75, -575), TerrainMap.Forest);
                Give(sim, Order.Attack(a.Id, r1.Id, AttackAspect.None, AttackOnBreak.Pursue), d);
                Give(sim, Order.Hold(g.Id), d);
                Run(sim, 900);
                return log.ToString();
            }
            Assert.That(RunWith(Discretion.Delegate), Is.EqualTo(RunWith(Discretion.Strict)));
        }

        // ---------------------------------------------------------------- 猪突

        /// <summary>
        /// 任せた猪突は「確保」でも目標が崩れたら追う。追っていた目標が盤外へ逃げたら、400m 以内の動揺中の敵へ続けて攻めかかる。
        /// 厳命なら確保して留まる。
        /// </summary>
        [TestCase(Discretion.Delegate)]
        [TestCase(Discretion.Strict)]
        public void RecklessPursuesEvenOnSecureAndThenAttacksAShakenEnemy(Discretion d)
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var b = Add(sim, "B", Side.Blue, 0, 1050, 90, Personality.Reckless);
            var r1 = Add(sim, "R1", Side.Red, 0, 1420, -90, strength: 60);
            var r2 = Add(sim, "R2", Side.Red, 350, 1150, 180, strength: 60);
            Give(sim, Order.Attack(b.Id, r1.Id, AttackAspect.None, AttackOnBreak.Secure), d);
            Give(sim, Order.Hold(r2.Id), Discretion.Strict);
            Run(sim, 5, () => { b.Morale = 100; r2.Morale = 45; });
            r1.Morale = 0; // 崩れる
            Run(sim, 120, () => { b.Morale = 100; r2.Morale = 45; });
            string s = log.ToString();
            if (d == Discretion.Delegate)
            {
                StringAssert.Contains("\"type\":\"independent\",\"unit\":\"B\",\"side\":\"blue\",\"personality\":\"reckless\",\"kind\":\"pursue\"", s);
                StringAssert.Contains("\"kind\":\"attack\",\"target\":\"R2\"", s, "続けて動揺中の R2 へ攻めかかる");
                Assert.That(b.CurrentOrder.Type, Is.EqualTo(OrderType.Attack));
                Assert.That(b.CurrentOrder.TargetId, Is.EqualTo(r2.Id));
                Assert.That(b.Independent, Is.EqualTo(IndependentAct.Attack));
            }
            else
            {
                StringAssert.DoesNotContain("\"type\":\"independent\"", s);
                Assert.That(b.Pursuing, Is.False);
                Assert.That(b.CurrentOrder.Type, Is.EqualTo(OrderType.Hold), "確保して留まる");
            }
        }

        /// <summary>任せた猪突の独断の追撃は、持ち場から 900m で止まって命令待ちになる。</summary>
        [Test]
        public void RecklessIndependentPursuitStopsAt900mAndAwaitsOrders()
        {
            var log = new StringEventSink();
            // 逃げる敵が先に再集結を始めて追撃が終わらないように、再集結を遅らせる（追う側は追いつけない。仕様の計算の根拠）
            var p = TestUtil.ParamsWith(("\"rally_after_sec\": 180", "\"rally_after_sec\": 100000"));
            var sim = new Simulation(p, 1, TerrainMap.FlatCovering(p.Terrain, -1500, -1500, 1500, 1500), log);
            var b = Add(sim, "B", Side.Blue, 0, -1300, 90, Personality.Reckless);
            var r1 = Add(sim, "R1", Side.Red, 0, -1000, -90, strength: 60);
            Give(sim, Order.Attack(b.Id, r1.Id, AttackAspect.None, AttackOnBreak.Secure), Discretion.Delegate);
            Run(sim, 2, () => b.Morale = 100);
            r1.Morale = 0;
            Run(sim, 2, () => b.Morale = 100);
            Assert.That(b.Pursuing, Is.True);
            var origin = b.PursuitStart;
            Run(sim, 1200, () => b.Morale = 100);
            Assert.That(b.Pursuing, Is.False);
            Assert.That(b.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            Assert.That(Vec2.Distance(b.Position, origin), Is.InRange(890, 905), "持ち場から 900m");
            Assert.That(b.IndependentStopped, Is.True);
            Assert.That(sim.AwaitingOrders(b), Is.True, "命令待ち");
            StringAssert.Contains("\"type\":\"independent_end\",\"unit\":\"B\"", log.ToString());
            double y = b.Position.Y;
            Run(sim, 60, () => b.Morale = 100);
            Assert.That(b.Position.Y, Is.EqualTo(y).Within(1), "戻らない・それ以上進まない");
        }

        /// <summary>独断の途中に次の段（信号弾）が始まったら、独断を捨ててその段を始める。</summary>
        [Test]
        public void NextStageOverridesAnIndependentAction()
        {
            var sim = Flat();
            var b = Add(sim, "B", Side.Blue, 0, -1300, 90, Personality.Reckless);
            var r1 = Add(sim, "R1", Side.Red, 0, -1000, -90, strength: 60);
            sim.IssueSheet(new OrderSheet(new[]
            {
                new Stage(Order.Attack(b.Id, r1.Id), StageTrigger.None),
                new Stage(Order.Hold(b.Id), StageTrigger.OnFlare(FlareColor.Green)),
            }));
            Run(sim, 2, () => b.Morale = 100);
            r1.Morale = 0;
            Run(sim, 30, () => b.Morale = 100);
            Assert.That(b.Independent, Is.EqualTo(IndependentAct.Pursuit));
            sim.FireFlare(Side.Blue, FlareColor.Green);
            Run(sim, 30, () => b.Morale = 100);
            Assert.That(b.StageIndex, Is.EqualTo(1));
            Assert.That(b.Pursuing, Is.False);
            Assert.That(b.Independent, Is.EqualTo(IndependentAct.None));
            Assert.That(b.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
        }

        /// <summary>前進・攻撃を拒む士気：猪突の任せるは 40 以下、慎重の任せるは 60 未満、厳命は 50 以下。</summary>
        [Test]
        public void AdvanceRefusalMoraleDependsOnPersonalityAndDiscretion()
        {
            var sim = Flat();
            var rk = Add(sim, "RK", Side.Blue, 0, 0, 90, Personality.Reckless);
            var ca = Add(sim, "CA", Side.Blue, 400, 0, 90, Personality.Cautious);
            var cs = Add(sim, "CS", Side.Blue, 800, 0, 90, Personality.Cautious);
            Give(sim, Order.Advance(rk.Id, new Vec2(0, 600)), Discretion.Delegate);
            Give(sim, Order.Advance(ca.Id, new Vec2(400, 600)), Discretion.Delegate);
            Give(sim, Order.Advance(cs.Id, new Vec2(800, 600)), Discretion.Strict);
            Run(sim, 10, () => { rk.Morale = 45; ca.Morale = 55; cs.Morale = 55; });
            Assert.That(rk.ShakenHalt, Is.False, "猪突は動揺していても進む");
            Assert.That(rk.Position.Y, Is.GreaterThan(5));
            Assert.That(ca.ShakenHalt, Is.True, "慎重の任せるは 60 未満で止まる");
            Assert.That(ca.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            Assert.That(cs.ShakenHalt, Is.False, "慎重の厳命は 50 まで進む");
            Run(sim, 5, () => { rk.Morale = 39; ca.Morale = 61; cs.Morale = 55; });
            Assert.That(rk.ShakenHalt, Is.True, "猪突も 40 以下で止まる");
            Assert.That(ca.ShakenHalt, Is.False, "60 以上に戻れば再開を待つ");
            Assert.That(ca.StartWaitOrder, Is.Not.Null);
        }

        // ---------------------------------------------------------------- 機敏

        /// <summary>
        /// 任せた機敏は、味方に正面から撃たれている 500m 以内の敵の側面の範囲へ回り込む。経路が 600m を越える（川で遠回り）なら回り込まない。
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void AgileFlanksAnEnemyUnderFrontalFireUnlessThePathIsTooLong(bool wall)
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var a = Add(sim, "A", Side.Blue, 25, 25, 90);
            var g = Add(sim, "G", Side.Blue, 425, 125, 90, Personality.Agile);
            var r = Add(sim, "R", Side.Red, 25, 325, -90);
            if (wall)
            {
                // 東の発起点との間に南北の川（北の端だけ空ける）：経路は 600m を越える
                for (double y = -975; y <= 1025; y += 50) SetCell(sim, new Vec2(325, y), TerrainMap.River);
            }
            Give(sim, Order.Hold(a.Id), Discretion.Strict);
            Give(sim, Order.Hold(g.Id), Discretion.Delegate);
            Give(sim, Order.Hold(r.Id), Discretion.Strict);
            Run(sim, 60, () => { a.Morale = 100; g.Morale = 100; r.Morale = 100; });
            if (!wall)
            {
                StringAssert.Contains("\"type\":\"independent\",\"unit\":\"G\",\"side\":\"blue\",\"personality\":\"agile\",\"kind\":\"flank\",\"target\":\"R\"", log.ToString());
                Assert.That(g.Independent, Is.EqualTo(IndependentAct.Flank));
                Assert.That(g.CurrentOrder.Type, Is.EqualTo(OrderType.Attack));
                Assert.That(g.CurrentOrder.Aspect, Is.EqualTo(AttackAspect.Flank));
            }
            else
            {
                StringAssert.DoesNotContain("\"type\":\"independent\"", log.ToString());
                Assert.That(g.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
            }
        }

        [Test]
        public void StrictAgileDoesNotFlank()
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var a = Add(sim, "A", Side.Blue, 25, 25, 90);
            var g = Add(sim, "G", Side.Blue, 425, 125, 90, Personality.Agile);
            var r = Add(sim, "R", Side.Red, 25, 325, -90);
            Give(sim, Order.Hold(g.Id), Discretion.Strict);
            Run(sim, 60, () => { a.Morale = 100; g.Morale = 100; r.Morale = 100; });
            StringAssert.DoesNotContain("\"type\":\"independent\"", log.ToString());
            Assert.That(g.CurrentOrder.Type, Is.EqualTo(OrderType.Hold));
        }

        // ---------------------------------------------------------------- 慎重

        /// <summary>任せた慎重は「追撃」の字面でも追わない（宣言を出す）。厳命なら追う。</summary>
        [TestCase(Discretion.Delegate, false)]
        [TestCase(Discretion.Strict, true)]
        public void CautiousDoesNotPursueUnlessStrict(Discretion d, bool pursues)
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var b = Add(sim, "B", Side.Blue, 0, -1300, 90, Personality.Cautious);
            var r1 = Add(sim, "R1", Side.Red, 0, -1000, -90, strength: 60);
            Give(sim, Order.Attack(b.Id, r1.Id, AttackAspect.None, AttackOnBreak.Pursue), d);
            Run(sim, 2, () => b.Morale = 100);
            r1.Morale = 0;
            Run(sim, 5, () => b.Morale = 100);
            Assert.That(b.Pursuing, Is.EqualTo(pursues));
            if (pursues) Assert.That(b.PursuitLimitM, Is.EqualTo(600), "厳命の限度は 600m");
            else StringAssert.Contains("\"kind\":\"no_pursuit\"", log.ToString());
        }

        // ---------------------------------------------------------------- 物陰へ寄る

        /// <summary>開けた所で 30秒撃たれた保持の中隊が、100m 以内の森へ寄る。物陰がなければ動かない。</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void UnderFireInTheOpenMovesToNearbyForest(bool forest)
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var a = Add(sim, "A", Side.Blue, 25, 25, 90);
            var r = Add(sim, "R", Side.Red, 25, 375, -90, strength: 40);
            if (forest) SetCell(sim, new Vec2(75, 75), TerrainMap.Forest);
            Give(sim, Order.Hold(a.Id), Discretion.Strict);
            Give(sim, Order.Hold(r.Id), Discretion.Strict);
            Run(sim, 50, () => { a.Morale = 90; r.Morale = 90; });
            Assert.That(a.Position, Is.EqualTo(new Vec2(25, 25)), "30秒＋練度の遅れ 25秒までは動かない");
            Run(sim, 150, () => { a.Morale = 90; r.Morale = 90; });
            if (forest)
            {
                Assert.That(Vec2.Distance(a.Position, new Vec2(75, 75)), Is.LessThan(1), "森へ寄った");
                Assert.That(a.CurrentOrder.Type, Is.EqualTo(OrderType.Hold), "命令は変えない");
                Assert.That(a.PostPoint, Is.EqualTo(new Vec2(25, 25)), "持ち場は変えない");
                StringAssert.Contains("\"type\":\"auto_cover\",\"unit\":\"A\"", log.ToString());
            }
            else
            {
                Assert.That(a.Position, Is.EqualTo(new Vec2(25, 25)), "物陰がなければ動かない");
            }
        }

        /// <summary>撃ってきている敵の 150m 以内の物陰には寄らない（近距離・接近戦に入らないように）。</summary>
        [Test]
        public void DoesNotSeekCoverCloseToTheShooter()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 25, 25, 90);
            var r = Add(sim, "R", Side.Red, 25, 275, -90, strength: 40);
            SetCell(sim, new Vec2(25, 125), TerrainMap.Forest); // 射手から 150m
            Give(sim, Order.Hold(a.Id), Discretion.Strict);
            Give(sim, Order.Hold(r.Id), Discretion.Strict);
            Run(sim, 120, () => { a.Morale = 90; r.Morale = 90; });
            Assert.That(a.Position, Is.EqualTo(new Vec2(25, 25)));
        }

        // ---------------------------------------------------------------- 浅い掩体

        /// <summary>120秒動かず静かな中隊が浅い掩体を掘り、300秒で被弾 ×0.6 になる。動くと失う。</summary>
        [Test]
        public void ScrapeIsDugWhenStillAndQuietAndLostOnMoving()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 25, 25, 90);
            Run(sim, 119);
            Assert.That(a.ScrapeProgress, Is.EqualTo(0));
            Assert.That(sim.TerrainDamageMult(a), Is.EqualTo(1.0));
            Run(sim, 2);
            Assert.That(a.ScrapeProgress, Is.GreaterThan(0), "120秒で掘り始める");
            Run(sim, 150);
            Assert.That(sim.TerrainDamageMult(a), Is.EqualTo(0.8).Within(0.01), "半分で ×0.8");
            Run(sim, 160);
            Assert.That(a.ScrapeProgress, Is.EqualTo(300));
            Assert.That(sim.TerrainDamageMult(a), Is.EqualTo(0.6).Within(1e-9), "300秒で ×0.6");

            sim.Issue(Order.Advance(a.Id, new Vec2(25, 300)));
            Run(sim, 2);
            Assert.That(a.ScrapeProgress, Is.EqualTo(0), "動くと失う");
        }

        [Test]
        public void ScrapeDoesNotStartWhileBeingShotAndNotWithEntrenching()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 25, 25, 90);
            var b = Add(sim, "B", Side.Blue, 625, 25, 90);
            var r = Add(sim, "R", Side.Red, 25, 375, -90, strength: 10);
            Give(sim, Order.Hold(r.Id), Discretion.Strict);
            Give(sim, Order.Entrench(b.Id), Discretion.Strict);
            Run(sim, 200, () => { a.Morale = 90; r.Morale = 90; });
            Assert.That(a.ScrapeProgress, Is.EqualTo(0), "撃たれている間は掘り始めない");
            Assert.That(b.ScrapeProgress, Is.EqualTo(0), "陣地構築とは重ねない");
        }

        // ---------------------------------------------------------------- 崩した敵の陣地に入る

        /// <summary>崩した敵がいた塹壕が確保した地点から 200m 以内なら入る。250m なら入らない。</summary>
        [TestCase(190, true)]
        [TestCase(250, false)]
        public void EntersTheBrokenEnemysTrenchWithin200m(double dist, bool enters)
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var b = Add(sim, "B", Side.Blue, 25, 0, 90);
            var trench = new Vec2(25, dist);
            var r = Add(sim, "R", Side.Red, trench.X, trench.Y, -90, strength: 60);
            SetCell(sim, trench, TerrainMap.Trench);
            Give(sim, Order.Attack(b.Id, r.Id), Discretion.Strict);
            r.Morale = 0;
            Run(sim, 1, () => b.Morale = 100);
            Assert.That(b.CurrentOrder.Type, Is.EqualTo(OrderType.Hold), "確保した");
            Run(sim, 300, () => b.Morale = 100);
            if (enters)
            {
                Assert.That(Vec2.Distance(b.Position, trench), Is.LessThan(1), "塹壕に入った");
                Assert.That(b.PostPoint, Is.EqualTo(b.Position), "持ち場はそこ");
                Assert.That(b.Fortified, Is.True);
                StringAssert.Contains("\"type\":\"enter_position\",\"unit\":\"B\"", log.ToString());
            }
            else
            {
                Assert.That(Vec2.Distance(b.Position, new Vec2(25, 0)), Is.LessThan(5), "入らない");
                StringAssert.DoesNotContain("enter_position", log.ToString());
            }
        }

        // ---------------------------------------------------------------- 撃てる目標がないときの向き

        /// <summary>撃てる目標がない中隊が、600m 以内の最も近い敵の方を向く。600m より遠ければ向かない。</summary>
        [TestCase(550, true)]
        [TestCase(650, false)]
        public void FacesTheNearestEnemyWithin600mWhenNothingToShoot(double dist, bool turns)
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            Add(sim, "R", Side.Red, 0, dist, -90);
            Run(sim, 60);
            Assert.That(Deg(a.Facing), Is.EqualTo(turns ? 90 : 0).Within(1e-6));
        }

        // ---------------------------------------------------------------- 裁量の持ち方

        [Test]
        public void DiscretionTravelsWithTheStageAndDefaultsToDelegate()
        {
            var d = new OrderSheetDraft(0);
            d.Write(Order.Hold(0), 3);
            Assert.That(d.Stages[0].Discretion, Is.EqualTo(Discretion.Delegate), "初めは任せる");
            d.SetDiscretion(0, Discretion.Strict);
            d.Write(Order.Advance(0, new Vec2(0, 100)), 3);
            d.SetTrigger(1, StageTrigger.At(60));
            var sheet = d.ToSheet(3);
            Assert.That(sheet.Stages[0].Discretion, Is.EqualTo(Discretion.Strict));
            Assert.That(sheet.Stages[1].Discretion, Is.EqualTo(Discretion.Delegate));
            d.MoveUp(1);
            Assert.That(d.Stages[0].Discretion, Is.EqualTo(Discretion.Delegate));
            Assert.That(d.Stages[1].Discretion, Is.EqualTo(Discretion.Strict), "入れ替えても段の裁量はついていく");
            Assert.That(OrderSheet.Single(Order.Hold(0)).Stages[0].Discretion, Is.EqualTo(Discretion.Delegate));

            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 90);
            Give(sim, Order.Hold(a.Id), Discretion.Strict);
            sim.Step();
            Assert.That(a.Discretion, Is.EqualTo(Discretion.Strict));
            sim.Issue(Order.Hold(a.Id));
            sim.Step();
            Assert.That(a.Discretion, Is.EqualTo(Discretion.Delegate));
        }
    }
}
