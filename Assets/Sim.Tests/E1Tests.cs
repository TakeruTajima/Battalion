using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>E1：赤の守り方（最小版）と、C12 の占領の乱れの単体テスト。</summary>
    public class E1Tests
    {
        static Simulation Flat(IEventSink log = null, SimParams p = null)
        {
            p = p ?? TestUtil.Params;
            return new Simulation(p, 1, TerrainMap.FlatCovering(p.Terrain, -1500, -1500, 1500, 1500), log);
        }

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg) =>
            sim.AddCompany(name, side, Training.Regular, Personality.Calm, new Vec2(x, y), facingDeg);

        static void SetCell(Simulation sim, Vec2 p, char ch, double? facingDeg = null)
        {
            sim.Map.CellOf(p, out int i, out int j);
            sim.Map.SetOverlay(i, j, ch);
            if (facingDeg.HasValue) sim.Map.SetFortificationFacing(i, j, facingDeg.Value * Angles.DegToRad);
        }

        static void Steps(Simulation sim, double seconds, Action each = null)
        {
            int n = (int)Math.Round(seconds / sim.Params.TickSec);
            for (int k = 0; k < n; k++)
            {
                each?.Invoke();
                sim.Step();
            }
        }

        static int Count(StringEventSink log, string type) => log.ToString().Split('\n').Count(l => l.Contains($"\"type\":\"{type}\""));

        /// <summary>塹壕（西向き、(25,25)）の赤の前線 R。後退先は東 300m。</summary>
        static (Simulation sim, Company r, StringEventSink log) FrontInTrench()
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var r = Add(sim, "R", Side.Red, 25, 25, 180);
            SetCell(sim, r.Position, TerrainMap.Trench, 180);
            var plan = new DefensePlan { ObjectiveCenter = new Vec2(325, 25), Assembly = new Vec2(600, 25) };
            plan.Units["R"] = new DefenseUnitPlan { Role = DefenseRole.Front, Fallback = new Vec2(325, 25), FallbackFacingDeg = 180 };
            return (sim, r, log);
        }

        // ---------------------------------------------------------------- 規則1：前線の後退

        /// <summary>
        /// 迂回：陣地の線より 50m 以上後ろ・600m 以内に見えている青がいて、正面の弧 400m に青がいなければ下がる。
        /// 後退先に着くと守備になり、会戦中に1回だけ。
        /// </summary>
        [Test]
        public void BypassedFrontFallsBackOnceAndBecomesGarrison()
        {
            var (sim, r, log) = FrontInTrench();
            var plan = new DefensePlan { ObjectiveCenter = new Vec2(325, 25), Assembly = new Vec2(600, 25) };
            plan.Units["R"] = new DefenseUnitPlan { Role = DefenseRole.Front, Fallback = new Vec2(325, 25), FallbackFacingDeg = 180 };
            var b = Add(sim, "B", Side.Blue, 125, 325, -90); // R の東 100m・北 300m（線より後ろ、正面の弧の外）
            sim.SetDefensePlan(plan);
            Steps(sim, 6);
            Assert.That(r.FellBack, Is.True, "迂回されたので下がる");
            StringAssert.Contains("\"type\":\"defense_fallback\",\"unit\":\"R\"", log.ToString());
            Steps(sim, 900, () => { r.Morale = 100; b.Morale = 100; });
            Assert.That(r.Role, Is.EqualTo(DefenseRole.Garrison), "着いたら守備");
            Assert.That(Count(log, "defense_fallback"), Is.EqualTo(1), "1回だけ");
        }

        /// <summary>正面の弧の 400m 以内に見えている青がいれば（拘束されていれば）、迂回されても下がらない。</summary>
        [Test]
        public void PinnedFrontDoesNotFallBack()
        {
            var (sim, r, log) = FrontInTrench();
            var plan = new DefensePlan { ObjectiveCenter = new Vec2(325, 25), Assembly = new Vec2(600, 25) };
            plan.Units["R"] = new DefenseUnitPlan { Role = DefenseRole.Front, Fallback = new Vec2(325, 25), FallbackFacingDeg = 180 };
            Add(sim, "B", Side.Blue, 125, 325, -90);
            Add(sim, "P", Side.Blue, -325, 25, 0); // 正面 350m（遠い拘束の距離）
            sim.SetDefensePlan(plan);
            Steps(sim, 60);
            Assert.That(r.FellBack, Is.False);
        }

        /// <summary>見えていない青（森で視線が通らない）では、迂回の判定をしない。</summary>
        [Test]
        public void UnseenEnemyDoesNotCountAsBypass()
        {
            var (sim, r, _) = FrontInTrench();
            var plan = new DefensePlan { ObjectiveCenter = new Vec2(325, 25), Assembly = new Vec2(600, 25) };
            plan.Units["R"] = new DefenseUnitPlan { Role = DefenseRole.Front, Fallback = new Vec2(325, 25), FallbackFacingDeg = 180 };
            var b = Add(sim, "B", Side.Blue, 325, 425, -90); // 線より 300m 後ろ・北 400m（射程の外）
            for (double x = 75; x <= 525; x += 50)
                for (double y = 175; y <= 325; y += 50)
                    SetCell(sim, new Vec2(x, y), TerrainMap.Forest);
            sim.SetDefensePlan(plan);
            Assert.That(sim.Aware(r, b), Is.False, "森で見えない");
            Steps(sim, 60);
            Assert.That(r.FellBack, Is.False);
        }

        /// <summary>直近 30秒に陣地の正面の弧の外（側面）から撃たれたら、迂回とみなす（青が線より後ろにいなくても）。</summary>
        [Test]
        public void FlankFireCountsAsBypass()
        {
            var (sim, r, log) = FrontInTrench();
            var plan = new DefensePlan { ObjectiveCenter = new Vec2(325, 25), Assembly = new Vec2(600, 25) };
            plan.Units["R"] = new DefenseUnitPlan { Role = DefenseRole.Front, Fallback = new Vec2(325, 25), FallbackFacingDeg = 180 };
            Add(sim, "B", Side.Blue, 25, 325, -90); // 真北 300m（線の上、側面）
            sim.SetDefensePlan(plan);
            Steps(sim, 6);
            Assert.That(r.FellBack, Is.True);
            StringAssert.Contains("\"reason\":\"flank_fire\"", log.ToString());
        }

        // ---------------------------------------------------------------- 規則2：即座の逆襲

        static (Simulation sim, Company v, StringEventSink log) ReserveScene(params (string name, double x, double y)[] blues)
        {
            var log = new StringEventSink();
            var sim = Flat(log);
            var v = Add(sim, "V", Side.Red, 525, 25, 180);
            var g = Add(sim, "G", Side.Red, 25, 25, 180);
            foreach (var (name, x, y) in blues) Add(sim, name, Side.Blue, x, y, 0);
            sim.SetHeadquarters(Side.Red, new Vec2(1025, 25));
            var plan = new DefensePlan { ObjectiveCenter = new Vec2(25, 25), Assembly = new Vec2(525, 25) };
            plan.Units["V"] = new DefenseUnitPlan { Role = DefenseRole.Reserve, Post = new Vec2(525, 25), TriggerCenter = new Vec2(25, 25), TriggerRadiusM = 400 };
            plan.Units["G"] = new DefenseUnitPlan { Role = DefenseRole.Garrison };
            sim.SetDefensePlan(plan);
            return (sim, v, log);
        }

        /// <summary>予備は伝令なしで逆襲する。目標は、引き金の円の中の見えている青のうち、守る地点に最も近い中隊。</summary>
        [Test]
        public void ReserveCounterattacksTheDeepestEnemyWithoutRunner()
        {
            var (sim, v, log) = ReserveScene(("A", -325, 25), ("B", -175, 225));
            Steps(sim, 6);
            Assert.That(v.Reserve, Is.EqualTo(ReserveState.Attacking));
            StringAssert.Contains("\"type\":\"defense_counterattack\",\"unit\":\"V\",\"side\":\"red\",\"target\":\"B\"", log.ToString());
            Assert.That(Count(log, "runner_sent"), Is.EqualTo(0), "伝令なし");
            Steps(sim, 30);
            Assert.That(v.CurrentOrder.Type, Is.EqualTo(OrderType.Attack));
        }

        /// <summary>余力がない（士気が動揺のしきい値以下、または損害 3割以上）予備は逆襲しない。</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void SpentReserveDoesNotCounterattack(bool lowMorale)
        {
            var (sim, v, log) = ReserveScene(("A", -175, 25));
            if (lowMorale) v.Morale = sim.Params.Morale.ShakenThreshold - 10;
            else v.Strength = (int)(v.InitialStrength * 0.7);
            Assert.That(sim.HasReserveStrength(v), Is.False);
            Steps(sim, 6, () => { if (lowMorale) v.Morale = sim.Params.Morale.ShakenThreshold - 10; });
            Assert.That(Count(log, "defense_counterattack"), Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- 規則3：奪回

        /// <summary>S17 の配置で、C2 を遠くに置く：揃う（2個）前に 600秒たったら、着いている C1 だけで攻撃に移る。</summary>
        [Test]
        public void RetakeLaunchesAfterMaxWaitWhenNotAllArrive()
        {
            var sc = Scenarios.S17_retake();
            sc.Companies.Single(c => c.Name == "C2").Y = -1300;
            var log = new StringEventSink();
            var sim = sc.Build(TestUtil.Params, 1, log);
            int next = 0;
            while (sim.Time < 30 * 60)
            {
                sc.IssueDue(sim, ref next);
                sim.Step();
            }
            var lines = log.ToString().Split('\n');
            double order = JsonValue.Parse(lines.First(l => l.Contains("\"type\":\"retake_order\""))).D("t");
            double attack = JsonValue.Parse(lines.First(l => l.Contains("\"type\":\"retake_attack\""))).D("t");
            Assert.That(attack - order, Is.EqualTo(sim.Params.Defense.AssembleMaxWaitSec).Within(0.5 + 1e-9), "600秒で出す");
            Assert.That(JsonValue.Parse(lines.First(l => l.Contains("\"type\":\"retake_attack\""))).D("assembled"), Is.EqualTo(1));
        }

        /// <summary>守る地点の 250m 以内に指揮下の赤がいれば、村に青がいても奪回しない。</summary>
        [Test]
        public void NoRetakeWhileRedHoldsNearTheObjective()
        {
            var sc = Scenarios.S17_retake();
            sc.Companies.Single(c => c.Name == "C1").X = 225;
            var log = new StringEventSink();
            var sim = sc.Build(TestUtil.Params, 1, log);
            int next = 0;
            while (sim.Time < 5 * 60)
            {
                sc.IssueDue(sim, ref next);
                foreach (var c in sim.Companies) c.Morale = 100;
                sim.Step();
            }
            Assert.That(Count(log, "retake_order"), Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- C12：占領の乱れ

        /// <summary>中心 (25,25) の村のマス（持ち主は <paramref name="owner"/>）へ、西から青 B が入る。</summary>
        static (Simulation sim, Company b, StringEventSink log) EnterVillage(Side? owner, SimParams p = null)
        {
            var log = new StringEventSink();
            var sim = Flat(log, p);
            SetCell(sim, new Vec2(25, 25), TerrainMap.Village);
            if (owner.HasValue) sim.SetInitialOwner(new Vec2(25, 25), owner.Value);
            var b = Add(sim, "B", Side.Blue, -175, 25, 0);
            Add(sim, "Far", Side.Red, 1400, 1400, 180);
            sim.Issue(Order.Advance(b.Id, new Vec2(25, 25)));
            for (int k = 0; k < 2000 && sim.Map.TerrainAt(b.Position) != TerrainMap.Village; k++) sim.Step();
            return (sim, b, log);
        }

        /// <summary>敵の側が持ち主の陣地に入ると占領の乱れ（撃つ力 ×0.5、300秒）。持ち主はその側に変わる。</summary>
        [Test]
        public void EnteringAnEnemyOwnedPositionCausesDisarray()
        {
            var (sim, b, log) = EnterVillage(Side.Red);
            Assert.That(sim.InOccupationDisarray(b), Is.True);
            Assert.That(sim.PositionOwnerAt(b.Position), Is.EqualTo(Side.Blue), "持ち主が変わる");
            Assert.That(b.OccupationDisarrayUntil - sim.Time, Is.EqualTo(sim.Params.Occupation.DisarraySec).Within(1.0));
            var list = new List<ModifierFactor>();
            sim.CurrentFireMult(b, list);
            Assert.That(list.Select(f => f.Kind), Does.Contain(ModifierKind.FireOccupation));
            StringAssert.Contains("\"type\":\"occupation_disarray\",\"unit\":\"B\"", log.ToString());
        }

        /// <summary>持ち主なしの陣地に入ったら、持ち主は変わるが乱れにはならない。</summary>
        [Test]
        public void EnteringAnUnownedPositionChangesOwnerWithoutDisarray()
        {
            var (sim, b, _) = EnterVillage(null);
            Assert.That(sim.InOccupationDisarray(b), Is.False);
            Assert.That(sim.PositionOwnerAt(b.Position), Is.EqualTo(Side.Blue));
        }

        /// <summary>occupation.enabled が false なら掛からない。</summary>
        [Test]
        public void DisabledOccupationCausesNoDisarray()
        {
            var p = TestUtil.ParamsWith(("\"enabled\": true,\n    \"disarray_sec\"", "\"enabled\": false,\n    \"disarray_sec\""));
            var (sim, b, _) = EnterVillage(Side.Red, p);
            Assert.That(sim.InOccupationDisarray(b), Is.False);
        }

        /// <summary>乱れの間は陣地構築を始めない（守りの用意ができない）。乱れが解けたら始める。</summary>
        [Test]
        public void NoEntrenchingDuringDisarray()
        {
            var (sim, b, _) = EnterVillage(Side.Red);
            sim.Issue(Order.Entrench(b.Id));
            Steps(sim, 60);
            Assert.That(b.EntrenchProgress, Is.EqualTo(0), "乱れの間は始めない");
            Steps(sim, sim.Params.Occupation.DisarraySec);
            Assert.That(b.EntrenchProgress, Is.GreaterThan(0), "解けたら始める");
        }
    }
}
