using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>R6：射撃の延伸と、突入の比べ・白兵戦の単体テスト（仕様「R6：突撃の最後の一歩（突入と白兵戦、射撃の延伸）（確定）」）。</summary>
    public class R6Tests
    {
        static Simulation Flat(SimParams p = null, ulong seed = 1, IEventSink log = null)
        {
            p = p ?? TestUtil.Params;
            return new Simulation(p, seed, TerrainMap.FlatCovering(p.Terrain, -1500, -1500, 1500, 1500), log);
        }

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg, Training tr = Training.Regular) =>
            sim.AddCompany(name, side, tr, Personality.Calm, new Vec2(x, y), facingDeg);

        /// <summary>中心のマスと南北2マスずつを西向きの塹壕にする。</summary>
        static void TrenchAt(Simulation sim, Vec2 p)
        {
            sim.Map.CellOf(p, out int ci, out int cj);
            for (int k = -2; k <= 2; k++)
            {
                sim.Map.SetOverlay(ci, cj + k, TerrainMap.Trench);
                sim.Map.SetFortificationFacing(ci, cj + k, Math.PI);
            }
        }

        static void RunTo(Simulation sim, double sec, Action each = null)
        {
            while (sim.Time < sec - 1e-9)
            {
                each?.Invoke();
                sim.Step();
            }
        }

        /// <summary>砲撃の制圧を掛け続ける（着弾の円の中にいるのと同じ。損害はない）。</summary>
        static void KeepSuppressed(Company c) => c.LastShelledTime = 1e9;

        /// <summary>
        /// 塹壕の守り手 R（原点、西向き）と、西で接近戦の距離（矩形の隙間 40m）にいる攻める側 B（R の位置への前進）。
        /// <paramref name="suppressed"/> なら、R に砲撃の制圧を掛け続け、制圧下になってから（1刻みの後）前進を出す。
        /// </summary>
        static (Simulation sim, Company r, Company b) TrenchAndAssault(SimParams p = null, ulong seed = 1, IEventSink log = null, bool suppressed = false)
        {
            var sim = Flat(p, seed, log);
            var r = Add(sim, "R", Side.Red, 0, 0, 180);
            TrenchAt(sim, r.Position);
            var b = Add(sim, "B", Side.Blue, -90, 0, 0);
            Assert.That(sim.InCloseCombat(b, r), Is.True);
            if (suppressed)
            {
                KeepSuppressed(r);
                sim.Step();
                Assert.That(r.Suppressed, Is.True);
            }
            sim.Issue(Order.Advance(b.Id, r.Position, 0));
            return (sim, r, b);
        }

        // ---------------------------------------------------------------- 1. 射撃の延伸

        /// <summary>
        /// 目標の矩形から 50m 以内に味方の矩形が入ったら撃たない（隙間 40m の F）。60m の外なら撃つ。撃つ中隊自身が接近戦の距離に
        /// いれば、ほかの味方が近くても撃ち続ける。
        /// </summary>
        [Test]
        public void FireLiftsWhenAFriendIsWithin50m()
        {
            var sim = Flat();
            var r = Add(sim, "R", Side.Red, 0, 0, 180);
            var a = Add(sim, "A", Side.Blue, -300, 0, 0);
            var f = Add(sim, "F", Side.Blue, -140, 0, 0);
            sim.Step();
            Assert.That(sim.SelectTarget(a), Is.SameAs(r), "味方の矩形が 90m 離れていれば撃つ");

            f.Position = new Vec2(-90, 0);
            Assert.That(sim.FireLifted(a, r), Is.True);
            Assert.That(sim.SelectTarget(a), Is.Null, "隙間 40m の味方がいれば撃たない（ほかの目標もない）");

            // 撃つ中隊 G 自身が接近戦の距離（隙間 10m の北）にいれば、F が近くても撃つ
            var g = Add(sim, "G", Side.Blue, -90, 210, 0);
            sim.Step();
            Assert.That(sim.InCloseCombat(g, r), Is.True);
            Assert.That(sim.SelectTarget(g), Is.SameAs(r));
            Assert.That(sim.SelectTarget(f), Is.SameAs(r));
            Assert.That(sim.SelectTarget(a), Is.Null);
        }

        /// <summary>延伸の外にほかの目標がいれば、そちらを選び直す。</summary>
        [Test]
        public void FireLiftChoosesAnotherTarget()
        {
            var sim = Flat();
            var r = Add(sim, "R", Side.Red, 0, 0, 180);
            var q = Add(sim, "Q", Side.Red, 0, -280, 180);
            var a = Add(sim, "A", Side.Blue, -250, 0, 0);
            Add(sim, "F", Side.Blue, -90, 0, 0);
            sim.Step();
            Assert.That(sim.SelectTarget(a), Is.SameAs(q));
        }

        /// <summary>R6 を止めた値（<see cref="TestUtil.WithoutR6"/>）では延伸しない。</summary>
        [Test]
        public void FireLiftCanBeTurnedOff()
        {
            var sim = Flat(TestUtil.WithoutR6(TestUtil.ParamsWith()));
            var r = Add(sim, "R", Side.Red, 0, 0, 180);
            var a = Add(sim, "A", Side.Blue, -300, 0, 0);
            Add(sim, "F", Side.Blue, -90, 0, 0);
            sim.Step();
            Assert.That(sim.SelectTarget(a), Is.SameAs(r));
        }

        // ---------------------------------------------------------------- 2. 押す力と守る力

        [Test]
        public void ForcesUseStrengthMoraleAndMultipliers()
        {
            var (sim, r, b) = TrenchAndAssault();
            r.Morale = 60;
            b.Morale = 80;
            // 守る力：150×0.6×塹壕 2.0、押す力：150×0.8（正面）
            Assert.That(sim.DefendForce(r), Is.EqualTo(180).Within(1e-9));
            Assert.That(sim.PushForce(b, r), Is.EqualTo(120).Within(1e-9));

            KeepSuppressed(r);
            sim.Step();
            Assert.That(r.Suppressed, Is.True);
            Assert.That(sim.DefendForce(r), Is.EqualTo(r.Strength * r.Morale / 100 * 2.0 * 0.25).Within(1e-9), "制圧下 ×0.25");

            double front = sim.PushForce(b, r);
            b.State = CompanyState.Shaken;
            Assert.That(sim.PushForce(b, r), Is.EqualTo(front * 0.5).Within(1e-9), "動揺 ×0.5");
            b.State = CompanyState.Normal;
            b.Position = new Vec2(0, 140);
            Assert.That(sim.AspectOf(r, b.Position), Is.EqualTo(Aspect.Flank));
            Assert.That(sim.PushForce(b, r), Is.EqualTo(front * 1.5).Within(1e-9), "側面 ×1.5");
            b.Position = new Vec2(90, 0);
            Assert.That(sim.PushForce(b, r), Is.EqualTo(front * 2.0).Within(1e-9), "背面 ×2.0");

            var v = Add(sim, "V", Side.Blue, -300, 0, 0, Training.Veteran);
            Assert.That(sim.PushForce(v, r), Is.EqualTo(v.Strength * v.Morale / 100 * 1.2).Within(1e-9), "古参 ×1.2");
        }

        /// <summary>陣地の守る力：塹壕 2.0、村 2.0、森 1.3、平地 1.0。浅い掩体は掘り終えて 1.3。</summary>
        [Test]
        public void DefenseMultiplierByPosition()
        {
            var sim = Flat();
            var d = Add(sim, "D", Side.Red, 500, 500, 180);
            Assert.That(sim.DefenseFortMult(d), Is.EqualTo(1.0));
            sim.Map.CellOf(d.Position, out int i, out int j);
            sim.Map.SetOverlay(i, j, TerrainMap.Village);
            Assert.That(sim.DefenseFortMult(d), Is.EqualTo(2.0));
            sim.Map.SetOverlay(i, j, TerrainMap.Forest);
            Assert.That(sim.DefenseFortMult(d), Is.EqualTo(1.3));
            sim.Map.SetOverlay(i, j, TerrainMap.Trench);
            Assert.That(sim.DefenseFortMult(d), Is.EqualTo(2.0));
            sim.Map.SetOverlay(i, j, TerrainMap.Plain);
            d.ScrapeProgress = sim.Params.Autonomy.ScrapeDigSec;
            Assert.That(sim.DefenseFortMult(d), Is.EqualTo(1.3).Within(1e-9));
        }

        /// <summary>
        /// 陣地の向き（R6 の報告の後に決めたこと 1）：西向きの塹壕は、押す側が正面の弧（西）にいる時だけ ×2.0。北（側面）・東（背面）
        /// から、または1個でも弧の外の中隊がいれば ×1.0。村は向きを見ない。浅い掩体も掘り始めたときの向きで見る。
        /// </summary>
        [Test]
        public void DefenseMultiplierNeedsTheFortificationArc()
        {
            var (sim, r, b) = TrenchAndAssault();
            var list = new List<Company> { b };
            Assert.That(sim.DefenseFortMult(r, list), Is.EqualTo(2.0), "正面（西）から");
            var n = Add(sim, "N", Side.Blue, 0, 140, -90);
            Assert.That(sim.DefenseFortMult(r, new List<Company> { n }), Is.EqualTo(1.0), "側面（北）から");
            Assert.That(sim.DefenseFortMult(r, new List<Company> { b, n }), Is.EqualTo(1.0), "1個でも弧の外");
            var e = Add(sim, "E", Side.Blue, 90, 0, 180);
            Assert.That(sim.DefenseFortMult(r, new List<Company> { e }), Is.EqualTo(1.0), "背面（東）から");

            var v = Add(sim, "V", Side.Red, 600, 600, 180);
            sim.Map.CellOf(v.Position, out int i, out int j);
            sim.Map.SetOverlay(i, j, TerrainMap.Village);
            var behind = Add(sim, "W", Side.Blue, 700, 600, 180);
            Assert.That(sim.DefenseFortMult(v, new List<Company> { behind }), Is.EqualTo(2.0), "村は向きを見ない");

            var sc = Add(sim, "S", Side.Red, -600, -600, 180);
            sc.ScrapeProgress = sim.Params.Autonomy.ScrapeDigSec;
            sc.ScrapeFacing = Math.PI;
            var west = Add(sim, "X", Side.Blue, -700, -600, 0);
            var east = Add(sim, "Y", Side.Blue, -500, -600, 180);
            Assert.That(sim.DefenseFortMult(sc, new List<Company> { west }), Is.EqualTo(1.3).Within(1e-9), "浅い掩体の正面");
            Assert.That(sim.DefenseFortMult(sc, new List<Company> { east }), Is.EqualTo(1.0), "浅い掩体の背面");
        }

        /// <summary>取った敵の塹壕（西向き）を東から逆襲されると、塹壕の守りが効かない：伏せていない守り手でも、押す力が勝てば突入される。</summary>
        [Test]
        public void CounterAttackFromBehindACapturedTrenchIsNotHeldByIt()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Blue, 0, 0, 0);
            TrenchAt(sim, a.Position);
            var e = Add(sim, "E", Side.Red, 90, 0, 180);
            a.Morale = 70;
            // 守る力 150×0.7×1.0＝105（塹壕なら 210）、押す力 150×1.0×1.0＝150
            sim.Issue(Order.Advance(e.Id, a.Position, Math.PI));
            sim.Step();
            Assert.That(e.MeleePush, Is.GreaterThan(e.MeleeDefend));
            Assert.That(Simulation.InMelee(a), Is.True);
        }

        // ---------------------------------------------------------------- 3. 突入と跳ね返し

        /// <summary>伏せていない塹壕の守り手（守る力 200）に、1個中隊（押す力 100）が着くと跳ね返される：士気 −10。白兵戦にならない。</summary>
        [Test]
        public void UnsuppressedTrenchRepulsesTheAssault()
        {
            var log = new StringEventSink();
            var (sim, r, b) = TrenchAndAssault(log: log);
            double before = b.Morale;
            sim.Step();
            Assert.That(r.MeleeAttackers, Is.Empty);
            Assert.That(b.Ledger.Melee, Is.EqualTo(-10).Within(1e-9));
            Assert.That(b.Morale, Is.LessThanOrEqualTo(before - 10 + 1e-9));
            Assert.That(log.ToString(), Does.Contain("\"type\":\"melee_repulsed\""));
            // 次の比べは 30秒後
            RunTo(sim, 29.5);
            Assert.That(b.Ledger.Melee, Is.EqualTo(-10).Within(1e-9));
            RunTo(sim, 31);
            Assert.That(b.Ledger.Melee, Is.LessThanOrEqualTo(-20 + 1e-9));
        }

        /// <summary>伏せている守り手（守る力 ×0.25）には突入する：白兵戦になり、突入した瞬間に守り手の士気 −15。</summary>
        [Test]
        public void SuppressedDefenderIsEntered()
        {
            var log = new StringEventSink();
            var (sim, r, b) = TrenchAndAssault(log: log, suppressed: true);
            sim.Step();
            Assert.That(r.MeleeAttackers, Is.EqualTo(new[] { b.Id }));
            Assert.That(b.MeleeDefenderId, Is.EqualTo(r.Id));
            Assert.That(Simulation.InMelee(r) && Simulation.InMelee(b), Is.True);
            Assert.That(r.Ledger.Melee, Is.LessThanOrEqualTo(-15 + 1e-9));
            Assert.That(log.ToString(), Does.Contain("\"type\":\"melee_entry\""));
            Assert.That(sim.MeleeOpponents(b, r), Is.True);
            sim.Step();
            Assert.That(b.TargetId, Is.EqualTo(-1), "白兵戦の相手は小銃で撃たない");
            Assert.That(r.TargetId, Is.EqualTo(-1));
        }

        /// <summary>
        /// 白兵戦の損害と士気（1分）：守り手は兵力の 8%×（押す力／守る力、上限 3）と士気 −20、攻める側は 4% と士気 −5。
        /// 20シードの平均で見る（ポアソン）。
        /// </summary>
        [Test]
        public void MeleeLossesAndMoralePerMinute()
        {
            double dLoss = 0, aLoss = 0, expectD = 0;
            const int n = 20;
            for (int s = 1; s <= n; s++)
            {
                var (sim, r, b) = TrenchAndAssault(seed: (ulong)s, suppressed: true);
                // 士気を保って、壊走させずに1分の減りを見る
                RunTo(sim, 1.0);
                Assert.That(Simulation.InMelee(r), Is.True);
                int r0 = r.Strength, b0 = b.Strength;
                double rm = r.Ledger.Melee, bm = b.Ledger.Melee;
                expectD += r0 * 0.08 * r.MeleeRatio / n;
                RunTo(sim, 61.0, () => { r.Morale = 100; b.Morale = 100; });
                dLoss += (r0 - r.Strength) / (double)n;
                aLoss += (b0 - b.Strength) / (double)n;
                Assert.That(r.Ledger.Melee - rm, Is.EqualTo(-20).Within(0.5), "守り手の士気 −20／分");
                Assert.That(b.Ledger.Melee - bm, Is.EqualTo(-5).Within(0.5), "攻める側の士気 −5／分");
            }
            Assert.That(dLoss, Is.EqualTo(expectD).Within(expectD * 0.3));
            Assert.That(aLoss, Is.EqualTo(4.0).Within(1.5));
        }

        /// <summary>白兵戦の中で比べ直して押す力が勝たなくなれば、押し返される：白兵戦は終わり、攻める側の士気 −10。</summary>
        [Test]
        public void MeleeEndsWhenPushedBack()
        {
            var log = new StringEventSink();
            var (sim, r, b) = TrenchAndAssault(log: log, suppressed: true);
            RunTo(sim, 5);
            Assert.That(Simulation.InMelee(r), Is.True);
            // 制圧が解け、守り手が持ち直した（守る力 200 対 押す力 ≦ 100）
            r.LastShelledTime = sim.Time - 100;
            double before = b.Ledger.Melee;
            RunTo(sim, 31, () => { r.Morale = 100; });
            Assert.That(Simulation.InMelee(r), Is.False);
            Assert.That(b.MeleeDefenderId, Is.EqualTo(-1));
            Assert.That(b.Ledger.Melee - before, Is.LessThanOrEqualTo(-10 + 1e-9));
            Assert.That(log.ToString(), Does.Contain("\"type\":\"melee_pushed_back\""));
        }

        /// <summary>白兵戦の中で壊走した守り手は、確率 0.3 でその場で降伏する（包囲されていなくても）。</summary>
        [Test]
        public void DefenderRoutedInMeleeSometimesSurrenders()
        {
            int surrendered = 0, routed = 0;
            for (int s = 1; s <= 200; s++)
            {
                var (sim, r, b) = TrenchAndAssault(seed: (ulong)s, suppressed: true);
                RunTo(sim, 2);
                Assert.That(Simulation.InMelee(r), Is.True);
                Assert.That(r.Encircled, Is.False);
                r.Morale = 1;
                sim.Step();
                if (r.State == CompanyState.Surrendered) surrendered++;
                else if (r.State == CompanyState.Routed) routed++;
            }
            Assert.That(surrendered + routed, Is.EqualTo(200));
            Assert.That(surrendered / 200.0, Is.EqualTo(0.3).Within(0.08));
        }

        // ---------------------------------------------------------------- 4. 押す力に数える中隊

        /// <summary>保持・拘束で近くにいる中隊は押す力に数えない（突入しない）。攻撃の命令でその守り手へ向かう中隊は数える。</summary>
        [Test]
        public void OnlyAttackAndAdvanceOnTheDefenderCount()
        {
            var (sim, r, b) = TrenchAndAssault();
            Assert.That(sim.AssaultsOn(b, r), Is.False, "命令はまだ届いていない（保持）");
            sim.Step();
            Assert.That(sim.AssaultsOn(b, r), Is.True, "R の位置への前進");
            sim.Issue(Order.Hold(b.Id));
            sim.Step();
            Assert.That(sim.AssaultsOn(b, r), Is.False, "保持");
            sim.Issue(Order.Pin(b.Id, r.Id, PinRange.Near));
            sim.Step();
            Assert.That(sim.AssaultsOn(b, r), Is.False, "拘束");
            sim.Issue(Order.Attack(b.Id, r.Id));
            sim.Step();
            Assert.That(sim.AssaultsOn(b, r), Is.True, "その守り手への攻撃");
            sim.Issue(Order.Advance(b.Id, new Vec2(-90, 600), 0));
            sim.Step();
            Assert.That(sim.AssaultsOn(b, r), Is.False, "ほかの所への前進");
        }

        /// <summary>同じ塹壕に2個の守り手がいる時、塹壕の点への前進は、目的地に一番近い守り手だけに数える。</summary>
        [Test]
        public void AdvanceIntoAPositionCountsForTheNearestDefender()
        {
            var sim = Flat();
            sim.Map.CellOf(new Vec2(0, 0), out int ci, out int cj);
            for (int k = -8; k <= 8; k++)
            {
                sim.Map.SetOverlay(ci, cj + k, TerrainMap.Trench);
                sim.Map.SetFortificationFacing(ci, cj + k, Math.PI);
            }
            var r1 = Add(sim, "R1", Side.Red, 0, 220, 180);
            var r2 = Add(sim, "R2", Side.Red, 0, -220, 180);
            var b = Add(sim, "B", Side.Blue, -90, 110, 0);
            sim.Step();
            Assert.That(sim.InCloseCombat(b, r1), Is.True);
            // 目的地 (0, 60) は R1 の矩形から 60m（50m の外）で、同じ塹壕の中で R1 に近い
            sim.Issue(Order.Advance(b.Id, new Vec2(0, 60), 0));
            sim.Step();
            Assert.That(sim.AssaultsOn(b, r1), Is.True);
            // 目的地 (0, −60) は R2 の矩形から 60m で、R2 に近い：R1 には数えない（R2 とは接近戦の距離にいない）
            sim.Issue(Order.Advance(b.Id, new Vec2(0, -60), 0));
            sim.Step();
            Assert.That(sim.AssaultsOn(b, r1), Is.False);
            Assert.That(sim.AssaultsOn(b, r2), Is.False);
        }

        /// <summary>攻める側が赤（逆襲の突撃：塹壕の青の位置への前進）でも同じ規則：伏せた青には突入し、伏せていない青には跳ね返される。</summary>
        [Test]
        public void RedCounterAttackFollowsTheSameRules()
        {
            foreach (bool suppressed in new[] { true, false })
            {
                var sim = Flat();
                var a = Add(sim, "A", Side.Blue, 0, 0, 0);
                sim.Map.CellOf(a.Position, out int ci, out int cj);
                for (int k = -2; k <= 2; k++)
                {
                    sim.Map.SetOverlay(ci, cj + k, TerrainMap.Trench);
                    sim.Map.SetFortificationFacing(ci, cj + k, 0);
                }
                var e = Add(sim, "E", Side.Red, 90, 0, 180);
                if (suppressed) KeepSuppressed(a);
                sim.Step();
                sim.Issue(Order.Advance(e.Id, a.Position, Math.PI));
                sim.Step();
                Assert.That(Simulation.InMelee(a), Is.EqualTo(suppressed));
                // 突入なら白兵戦の毎分の減り（1刻み分）だけ、跳ね返しなら −10
                Assert.That(e.Ledger.Melee, suppressed ? Is.GreaterThan(-1) : Is.EqualTo(-10).Within(1e-9));
            }
        }

        /// <summary>R6 を止めた値では、比べも白兵戦もしない。</summary>
        [Test]
        public void MeleeCanBeTurnedOff()
        {
            var (sim, r, b) = TrenchAndAssault(TestUtil.WithoutR6(TestUtil.ParamsWith()), suppressed: true);
            RunTo(sim, 60);
            Assert.That(Simulation.InMelee(r), Is.False);
            Assert.That(b.Ledger.Melee, Is.EqualTo(0));
        }
    }
}
