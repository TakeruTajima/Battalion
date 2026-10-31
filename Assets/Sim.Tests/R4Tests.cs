using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>R4：接近戦では陣地の守りが効かない（仕様「R4：接近戦では陣地の守りが効かない（確定）」）。</summary>
    public class R4Tests
    {
        /// <summary>close_combat.fortification_mult を 1.0（守りなし）にしたパラメータ。規則の形を見るテストは、選んだ値によらずこれを使う。</summary>
        static SimParams NoCover()
        {
            var p = TestUtil.ParamsWith();
            p.CloseCombat.FortificationMult = 1.0;
            return p;
        }

        static Simulation Flat(SimParams p = null)
        {
            p = p ?? NoCover();
            return new Simulation(p, 1, TerrainMap.FlatCovering(p.Terrain, -1500, -1500, 1500, 1500));
        }

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg) =>
            sim.AddCompany(name, side, Training.Regular, Personality.Calm, new Vec2(x, y), facingDeg);

        /// <summary>西向きの守り手 D を (25, 25) に置き、そのマスを西向きの塹壕にする。</summary>
        static Company TrenchDefender(Simulation sim)
        {
            var d = Add(sim, "D", Side.Red, 25, 25, 180);
            sim.Map.CellOf(d.Position, out int i, out int j);
            sim.Map.SetOverlay(i, j, TerrainMap.Trench);
            sim.Map.SetFortificationFacing(i, j, Math.PI);
            return d;
        }

        static readonly Vec2 West = new Vec2(-300, 25), North = new Vec2(25, 300);

        /// <summary>
        /// 塹壕の守り手は、接近戦の相手から撃たれるときは正面の弧の中でも守りが効かない（×1.0）。接近戦でなければ今どおり ×0.3。
        /// 正面の弧の外（今も効いていない）は変わらない。
        /// </summary>
        [Test]
        public void TrenchGivesNoCoverAgainstCloseCombat()
        {
            var sim = Flat();
            var d = TrenchDefender(sim);
            Assert.That(sim.TerrainDamageMult(d, West, false), Is.EqualTo(0.3).Within(1e-12), "接近戦でない・正面から");
            Assert.That(sim.TerrainDamageMult(d, West, true), Is.EqualTo(1.0).Within(1e-12), "接近戦・正面から");
            Assert.That(sim.TerrainDamageMult(d, North, true), Is.EqualTo(sim.Params.Terrain.FortificationOffArcMult).Within(1e-12), "接近戦・側面から");
            Assert.That(sim.TerrainDamageMult(d, null, true), Is.EqualTo(1.0).Within(1e-12), "接近戦・向きの分からない判断");
        }

        /// <summary>陣地構築の途中・完成と、浅い掩体も、接近戦では守りが効かない。</summary>
        [Test]
        public void EntrenchmentAndScrapeGiveNoCoverAgainstCloseCombat()
        {
            var sim = Flat();
            var d = Add(sim, "D", Side.Red, 25, 25, 180);
            d.EntrenchProgress = sim.Params.MapRules.EntrenchSec;
            Assert.That(sim.TerrainDamageMult(d, West, false), Is.EqualTo(sim.Params.MapRules.EntrenchDamageMult).Within(1e-12));
            Assert.That(sim.TerrainDamageMult(d, West, true), Is.EqualTo(1.0).Within(1e-12), "陣地構築・接近戦");
            d.EntrenchProgress = 0;
            d.ScrapeProgress = sim.Params.Autonomy.ScrapeDigSec;
            d.ScrapeFacing = Math.PI;
            Assert.That(sim.TerrainDamageMult(d, West, false), Is.EqualTo(sim.Params.Autonomy.ScrapeDamageMult).Within(1e-12));
            Assert.That(sim.TerrainDamageMult(d, West, true), Is.EqualTo(1.0).Within(1e-12), "浅い掩体・接近戦");
        }

        /// <summary>向きのない守り（村・森）は、接近戦でも今のまま。</summary>
        [TestCase(TerrainMap.Forest)]
        [TestCase(TerrainMap.Village)]
        public void VillageAndForestKeepTheirCover(char terrain)
        {
            var sim = Flat();
            var d = Add(sim, "D", Side.Red, 25, 25, 180);
            sim.Map.CellOf(d.Position, out int i, out int j);
            sim.Map.SetOverlay(i, j, terrain);
            double m = sim.Map.DamageMult(terrain);
            Assert.That(m, Is.LessThan(1.0));
            Assert.That(sim.TerrainDamageMult(d, West, true), Is.EqualTo(m).Within(1e-12));
            Assert.That(sim.TerrainDamageMult(d, West, false), Is.EqualTo(m).Within(1e-12));
        }

        /// <summary>
        /// close_combat.fortification_mult が 0.6 なら、正面の弧の中の守りは 0.6 まで（0.3 より弱い）。弧の外の 1.0 には守りを足さない。
        /// 0（R4 なし）なら、接近戦でも今の守り。
        /// </summary>
        [Test]
        public void FortificationMultCapsTheCover()
        {
            var p = TestUtil.ParamsWith();
            p.CloseCombat.FortificationMult = 0.6;
            var sim = Flat(p);
            var d = TrenchDefender(sim);
            Assert.That(sim.TerrainDamageMult(d, West, true), Is.EqualTo(0.6).Within(1e-12));
            Assert.That(sim.TerrainDamageMult(d, North, true), Is.EqualTo(p.Terrain.FortificationOffArcMult).Within(1e-12));

            var off = Flat(TestUtil.WithoutR4(TestUtil.ParamsWith()));
            var d0 = TrenchDefender(off);
            Assert.That(off.TerrainDamageMult(d0, West, true), Is.EqualTo(0.3).Within(1e-12));
        }

        /// <summary>表示の口：接近戦なら「接近戦で陣地の守りなし」の要素が載り、要素の積は損害の式の倍率と同じ。</summary>
        [Test]
        public void DamageFactorsShowTheLostCover()
        {
            var sim = Flat();
            var d = TrenchDefender(sim);
            var list = new List<ModifierFactor>();
            double prod = sim.DamageFactors(d, West, list, true);
            Assert.That(prod, Is.EqualTo(sim.TerrainDamageMult(d, West, true)).Within(1e-12));
            Assert.That(list.Select(f => f.Kind), Is.EqualTo(new[] { ModifierKind.CloseCombatFortification }));
            Assert.That(list.Aggregate(1.0, (x, f) => x * f.Mult), Is.EqualTo(prod).Within(1e-12));

            var far = new List<ModifierFactor>();
            sim.DamageFactors(d, West, far, false);
            Assert.That(far.Select(f => f.Kind), Is.EqualTo(new[] { ModifierKind.Trench }));
        }

        /// <summary>
        /// 損害の式で効く：塹壕の D の正面 30m（矩形の隙間）にいる A が D を撃つ損害の期待値は、fortification_mult 1.0 では R4 なしの 1/0.3 倍。
        /// </summary>
        [Test]
        public void CloseCombatFireIgnoresTheTrench()
        {
            double Expected(SimParams p)
            {
                var sim = Flat(p);
                var d = TrenchDefender(sim);
                var a = Add(sim, "A", Side.Blue, -55, 25, 0);
                Assert.That(sim.InCloseCombat(a, d), Is.True, "隙間 30m は接近戦");
                sim.Step();
                return sim.LastTickFire.Single(r => r.Shooter == a.Id).ExpectedPerMin;
            }
            double with = Expected(NoCover());
            double without = Expected(TestUtil.WithoutR4(TestUtil.ParamsWith()));
            Assert.That(with / without, Is.EqualTo(1.0 / 0.3).Within(1e-9));
            // 選んだ値（sim_params.json）でも、守りは fortification_mult まで弱まる
            double chosen = Expected(TestUtil.Params);
            Assert.That(chosen / without, Is.EqualTo(Math.Max(0.3, TestUtil.Params.CloseCombat.FortificationMult) / 0.3).Within(1e-9));
        }
    }
}
