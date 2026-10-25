using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>R2.1：伏せた味方の超越と、効いている倍率の表示（仕様「R2.1：伏せた味方の超越と、効いている倍率の表示（確定）」）。</summary>
    public class R21Tests
    {
        static Simulation Flat(SimParams p = null)
        {
            p = p ?? TestUtil.Params;
            return new Simulation(p, 1, TerrainMap.FlatCovering(p.Terrain, -1500, -1500, 1500, 1500));
        }

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg, int? strength = null) =>
            sim.AddCompany(name, side, Training.Regular, Personality.Calm, new Vec2(x, y), facingDeg, strength: strength);

        static void Give(Simulation sim, Company c, Vec2 to) =>
            sim.IssueSheet(new OrderSheet(new[] { new Stage(Order.Advance(c.Id, to, 90 * Angles.DegToRad), StageTrigger.None, Discretion.Strict) }));

        /// <summary>撃つ敵を置かずに、制圧下を保つ（解けるまでの数えを毎刻み戻す）。</summary>
        static void KeepSuppressed(Company c)
        {
            c.Suppressed = true;
            c.SuppressOffSec = 0;
        }

        /// <summary>動き始めるまで進める（練度の遅れ）。<paramref name="each"/> は毎刻みの前に呼ぶ。</summary>
        static void UntilMoving(Simulation sim, Company c, Action each = null)
        {
            for (int k = 0; k < 200 && !c.MovedThisTick; k++)
            {
                each?.Invoke();
                sim.Step();
            }
            Assert.That(c.MovedThisTick, Is.True, $"{c.Name} が動き始めた");
        }

        // ---------------------------------------------------------------- 伏せた味方の超越

        /// <summary>
        /// 前の F（北へ前進）が、前進を始めたときから制圧下で伏せて止まっていると、150m 後ろの G はそれを止まっている味方とみなして
        /// 超越し、F の前へ抜ける。F が動いている間（拘束で遅いときを含む）は超越の相手にせず、押し離しで後ろに残る。
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void PinnedDownFriendIsPassedButAMovingOneIsNot(bool pinnedDown)
        {
            var sim = Flat();
            var f = Add(sim, "F", Side.Blue, 0, 0, 90);
            var g = Add(sim, "G", Side.Blue, 0, -150, 90);
            Give(sim, f, new Vec2(0, 1000));
            Give(sim, g, new Vec2(0, 600));
            bool passed = false;
            for (int k = 0; k < 2 * 400; k++)
            {
                if (pinnedDown) KeepSuppressed(f);
                else f.Pinned = true; // 拘束で遅い（動いている）味方
                sim.Step();
                passed |= g.Passing.Contains(f.Id);
            }
            Assert.That(sim.PinnedDown(f), Is.EqualTo(pinnedDown));
            Assert.That(passed, Is.EqualTo(pinnedDown), pinnedDown ? "伏せて止まった味方は超越する" : "動いている味方は抜けない");
            if (pinnedDown) Assert.That(g.Position.Y, Is.GreaterThan(f.Position.Y + 100), "F の前へ抜けた");
            else Assert.That(g.Position.Y, Is.LessThan(f.Position.Y), "F の後ろに残る");
        }

        /// <summary>伏せて止まった F を、後ろの G が抜けて前へ出られる（R1 の取りこぼし。押し離しで押し戻されない）。</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void RearCompanyGetsPastAPinnedDownFriend(bool friendPinned)
        {
            var sim = Flat();
            var f = Add(sim, "F", Side.Blue, 0, 0, 90);
            var g = Add(sim, "G", Side.Blue, 0, -260, 90);
            Give(sim, f, new Vec2(0, 1000));
            Give(sim, g, new Vec2(0, 600));
            UntilMoving(sim, f);
            if (friendPinned)
            {
                // F を伏せたままにする（撃つ敵を置かずに、制圧下を保つ）。G は F を超越するか、止まっている味方を避ける経路で前へ出る
                for (int k = 0; k < 2 * 900; k++)
                {
                    KeepSuppressed(f);
                    sim.Step();
                }
                Assert.That(sim.PinnedDown(f), Is.True);
                Assert.That(g.Position.Y, Is.GreaterThan(f.Position.Y + 100), "G は F を抜けて前へ出た");
                Assert.That(Vec2.Distance(g.Position, g.MoveGoal), Is.LessThan(30), "G は行き先の近くまで進んだ（止まっている味方を避ける経路で回り込む）");
            }
            else
            {
                // 比べ：F が動いていれば、G は F の後ろを（間隔を保って）ついて行く
                for (int k = 0; k < 2 * 300; k++) sim.Step();
                Assert.That(g.Position.Y, Is.LessThan(f.Position.Y), "動いている味方は抜かない");
            }
        }

        // ---------------------------------------------------------------- 効いている倍率の読み取りの口

        /// <summary>
        /// 被弾の要素を掛けた値が、損害の式で使う被弾倍率（TerrainDamageMult）と一致する（塹壕の正面・正面の弧の外、
        /// 開けた所を移動中、陣地構築の途中、浅い掩体、森）。
        /// </summary>
        [Test]
        public void DamageFactorsMultiplyToTheDamageMultiplierUsedByFire()
        {
            var sim = Flat();
            var a = Add(sim, "A", Side.Red, 25, 25, 180);
            sim.Map.CellOf(a.Position, out int i, out int j);
            var west = new Vec2(-300, 25);
            var north = new Vec2(25, 300);

            void Check(string what, Vec2? from, ModifierKind? expectKind)
            {
                var list = new List<ModifierFactor>();
                double prod = sim.DamageFactors(a, from, list);
                Assert.That(prod, Is.EqualTo(sim.TerrainDamageMult(a, from)).Within(1e-12), what);
                Assert.That(list.Aggregate(1.0, (x, f) => x * f.Mult), Is.EqualTo(prod).Within(1e-12), what + "（要素の積）");
                if (expectKind.HasValue) Assert.That(list.Select(f => f.Kind), Does.Contain(expectKind.Value), what);
            }

            Check("平地で止まっている", west, null);
            a.MovedThisTick = true;
            Check("開けた所を移動中", west, ModifierKind.MovingOpen);
            a.MovedThisTick = false;
            a.EntrenchProgress = sim.Params.MapRules.EntrenchSec / 2;
            Check("陣地構築の途中・正面から", west, ModifierKind.Entrench);
            Check("陣地構築の途中・側面から", north, ModifierKind.EntrenchOffArc);
            a.EntrenchProgress = 0;
            a.ScrapeProgress = sim.Params.Autonomy.ScrapeDigSec;
            a.ScrapeFacing = Math.PI;
            Check("浅い掩体・正面から", west, ModifierKind.Scrape);
            Check("浅い掩体・側面から", north, ModifierKind.ScrapeOffArc);
            a.ScrapeProgress = 0;
            sim.Map.SetOverlay(i, j, TerrainMap.Trench);
            sim.Map.SetFortificationFacing(i, j, Math.PI);
            Check("塹壕・正面から", west, ModifierKind.Trench);
            Check("塹壕・側面から", north, ModifierKind.TrenchOffArc);
            Check("塹壕・向きの分からない判断", null, ModifierKind.Trench);
            sim.Map.SetOverlay(i, j, TerrainMap.Forest);
            Check("森", north, ModifierKind.Terrain);
        }

        /// <summary>
        /// 撃つ力の要素（表示の口）が、損害の式で使った値と一致する：動きながら撃つ射手（×0.5）と、制圧下の射手（×0.25）で、
        /// 直前の射撃の損害の期待値を、撃つ力の倍率以外の要素で割った値と比べる。
        /// </summary>
        [Test]
        public void FireFactorsMatchTheFireUsedByTheDamageFormula()
        {
            // 動きながら撃つ：M が北へ進みながら、正面 300m の弱い T（10人）を撃つ（T の火力は小さく、M は伏せない）
            var sim = Flat();
            var m = Add(sim, "M", Side.Blue, 0, 0, 90);
            Add(sim, "T", Side.Red, 0, 300, -90, strength: 10);
            Give(sim, m, new Vec2(0, 100));
            int s0 = m.Strength;
            UntilMoving(sim, m, () => s0 = m.Strength);
            var list = new List<ModifierFactor>();
            double mult = sim.CurrentFireMult(m, list);
            var rec = sim.LastTickFire.Single(r => r.Shooter == m.Id);
            double baseRate = sim.Params.Fire.KPerMin * s0 * sim.RangeMultiplier(rec.Distance, false);
            Assert.That(rec.ExpectedPerMin / baseRate, Is.EqualTo(mult).Within(1e-12));
            Assert.That(list.Select(f => f.Kind), Is.EqualTo(new[] { ModifierKind.FireMoving }));

            // 制圧下：止まっている A を、正面の2個中隊が撃つ（R1 の TwoOnOne と同じ配置）
            var sim2 = Flat();
            var a = Add(sim2, "A", Side.Blue, 25, 25, 90);
            Add(sim2, "R1", Side.Red, 25, 350, -90);
            Add(sim2, "R2", Side.Red, 235, 330, -90);
            for (int k = 0; k < 30; k++)
            {
                foreach (var c in sim2.Companies) c.Morale = 100;
                sim2.Step();
            }
            Assert.That(a.Suppressed, Is.True);
            int sa = a.Strength;
            foreach (var c in sim2.Companies) c.Morale = 100;
            sim2.Step();
            var list2 = new List<ModifierFactor>();
            double mult2 = sim2.CurrentFireMult(a, list2);
            var rec2 = sim2.LastTickFire.Single(r => r.Shooter == a.Id);
            double base2 = sim2.Params.Fire.KPerMin * sa * sim2.RangeMultiplier(rec2.Distance, false);
            Assert.That(rec2.ExpectedPerMin / base2, Is.EqualTo(mult2).Within(1e-12));
            Assert.That(list2.Select(f => f.Kind), Is.EqualTo(new[] { ModifierKind.FireSuppressed }));
            Assert.That(mult2, Is.EqualTo(sim2.Params.Suppression.FireMult));
        }

        /// <summary>移動の要素：開けた所を移動中なら「露」の印の条件が立ち、伏せて止まれば消える。</summary>
        [Test]
        public void ExposedMarkFollowsMovingInTheOpen()
        {
            var sim = Flat();
            var m = Add(sim, "M", Side.Blue, 0, 0, 90);
            Give(sim, m, new Vec2(0, 1000));
            UntilMoving(sim, m);
            Assert.That(sim.ExposedWhileMoving(m), Is.True);
            var list = new List<ModifierFactor>();
            Assert.That(sim.MoveFactors(m, list), Is.EqualTo(1.0), "平地を前へ進む：移動の倍率なし");
            for (int k = 0; k < 2; k++)
            {
                KeepSuppressed(m);
                sim.Step();
            }
            Assert.That(sim.ExposedWhileMoving(m), Is.False, "伏せて止まれば印は消える");
        }
    }
}
