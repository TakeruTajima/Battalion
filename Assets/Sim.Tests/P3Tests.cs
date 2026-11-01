using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>P3：砲兵（仕様「P3：砲兵（確定）」）の単体テストと、場面 S24〜S29。</summary>
    public class P3Tests
    {
        public const int Seeds = 20;

        static Simulation Flat(SimParams p = null, ulong seed = 1, IEventSink log = null)
        {
            p = p ?? TestUtil.Params;
            return new Simulation(p, seed, TerrainMap.FlatCovering(p.Terrain, -1500, -1500, 1500, 1500), log);
        }

        static Company Add(Simulation sim, string name, Side side, double x, double y, double facingDeg,
            Personality pers = Personality.Calm, Formation formation = Formation.Line)
        {
            var c = sim.AddCompany(name, side, Training.Regular, pers, new Vec2(x, y), facingDeg);
            c.Formation = formation;
            return c;
        }

        static void RunTo(Simulation sim, double sec)
        {
            while (sim.Time < sec - 1e-9) sim.Step();
        }

        /// <summary>中心 <paramref name="p"/> から半径 <paramref name="r"/> のマス（中心で判定）を地形 <paramref name="ch"/> にする。</summary>
        static void Paint(Simulation sim, Vec2 p, double r, char ch)
        {
            Visibility.ForCellsWithin(sim.Map, p, r, (i, j, _) => sim.Map.SetOverlay(i, j, ch));
        }

        /// <summary>中隊の中心のマスと南北に2マスずつ（5マス）を、西向きの塹壕にする。</summary>
        static void TrenchAt(Simulation sim, Vec2 p)
        {
            sim.Map.CellOf(p, out int ci, out int cj);
            for (int k = -2; k <= 2; k++)
            {
                sim.Map.SetOverlay(ci, cj + k, TerrainMap.Trench);
                sim.Map.SetFortificationFacing(ci, cj + k, Math.PI);
            }
        }

        static double P(Company c) => (double)(c.InitialStrength - c.Strength);

        // ---------------------------------------------------------------- 要請できる地点

        /// <summary>計画射撃点、気付いている敵から 200m 以内は要請できる。200m を越える地点・気付いていない敵のまわりは要請できない。</summary>
        [Test]
        public void RequestablePoints()
        {
            var sim = Flat();
            sim.SetArtillery(Side.Blue, 160);
            Add(sim, "A", Side.Blue, -600, 0, 0);
            Add(sim, "R", Side.Red, 0, 0, 180);
            // 森の向こうの赤（見えない）
            Add(sim, "H", Side.Red, -600, 1200, -90);
            Paint(sim, new Vec2(-600, 700), 200, TerrainMap.Forest);
            Assert.That(sim.AddPlannedPoint(Side.Blue, new Vec2(800, 800)), Is.True);
            sim.Step();
            Assert.That(sim.RequestablePoint(Side.Blue, new Vec2(800, 800)), Is.True, "計画射撃点");
            Assert.That(sim.RequestablePoint(Side.Blue, new Vec2(150, 100)), Is.True, "見えている R から 180m");
            Assert.That(sim.RequestablePoint(Side.Blue, new Vec2(250, 0)), Is.False, "R から 250m");
            Assert.That(sim.RequestablePoint(Side.Blue, new Vec2(-600, 1150)), Is.False, "気付いていない H のそば");
            Assert.That(sim.CheckFireRequest(Side.Blue, new Vec2(250, 0), 3), Is.EqualTo(FireCheck.NotAllowedPoint));
            Assert.That(sim.CheckFireRequest(Side.Blue, new Vec2(0, 0), 2), Is.EqualTo(FireCheck.BadDuration), "長さは 1／3／5分");
            Assert.That(sim.CheckFireRequest(Side.Red, new Vec2(0, 0), 3), Is.EqualTo(FireCheck.NoArtillery));
            Assert.That(sim.AddPlannedPoint(Side.Blue, new Vec2(0, 800)), Is.False, "会戦が始まったら登録できない");
        }

        /// <summary>計画射撃点は3箇所まで。</summary>
        [Test]
        public void AtMostThreePlannedPoints()
        {
            var sim = Flat();
            sim.SetArtillery(Side.Blue, 160);
            for (int k = 0; k < 3; k++) Assert.That(sim.AddPlannedPoint(Side.Blue, new Vec2(100 * k, 0)), Is.True);
            Assert.That(sim.AddPlannedPoint(Side.Blue, new Vec2(500, 0)), Is.False);
            Assert.That(sim.RemovePlannedPoint(Side.Blue, 0), Is.True);
            Assert.That(sim.AddPlannedPoint(Side.Blue, new Vec2(500, 0)), Is.True);
        }

        // ---------------------------------------------------------------- 電話と準備、時刻の指定

        /// <summary>電話 30秒・準備 90秒で初弾（計画射撃点は準備 30秒）。</summary>
        [Test]
        public void PhoneAndPrepTimes()
        {
            var sim = Flat();
            sim.SetArtillery(Side.Blue, 160);
            Add(sim, "A", Side.Blue, -600, 0, 0);
            Add(sim, "R", Side.Red, 0, 0, 180);
            sim.AddPlannedPoint(Side.Blue, new Vec2(0, 500));
            RunTo(sim, 10);
            var m = sim.RequestFire(Side.Blue, new Vec2(0, 0), 1);
            Assert.That(m.FirstImpactTime, Is.EqualTo(10 + 30 + 90).Within(1e-9));
            RunTo(sim, 40);
            Assert.That(m.State, Is.EqualTo(FireMissionState.Phone));
            sim.Step();
            Assert.That(m.State, Is.EqualTo(FireMissionState.Preparing));
            RunTo(sim, 130);
            Assert.That(m.State, Is.EqualTo(FireMissionState.Preparing));
            sim.Step();
            Assert.That(m.State, Is.EqualTo(FireMissionState.Firing));
            RunTo(sim, 190);
            Assert.That(m.State, Is.EqualTo(FireMissionState.Done));
            var pm = sim.RequestFire(Side.Blue, new Vec2(0, 500), 1);
            Assert.That(pm.Planned, Is.True);
            Assert.That(pm.FirstImpactTime, Is.EqualTo(190 + 30 + 30).Within(1e-9));
        }

        /// <summary>時刻を指定した要請：その時刻に初弾が落ちる。準備の時間より前は選べない。撃つ時間が重なる要請はできない。</summary>
        [Test]
        public void TimedRequest()
        {
            var sim = Flat();
            sim.SetArtillery(Side.Blue, 160);
            sim.AddPlannedPoint(Side.Blue, new Vec2(0, 0));
            Assert.That(sim.CheckFireRequest(Side.Blue, new Vec2(0, 0), 5, 59), Is.EqualTo(FireCheck.TooEarly));
            var m = sim.RequestFire(Side.Blue, new Vec2(0, 0), 5, 600);
            Assert.That(m, Is.Not.Null);
            Assert.That(sim.CheckFireRequest(Side.Blue, new Vec2(0, 0), 1, 840), Is.EqualTo(FireCheck.Overlap));
            Assert.That(sim.CheckFireRequest(Side.Blue, new Vec2(0, 0), 1, 900), Is.EqualTo(FireCheck.Ok), "前の射撃が終わる時刻から");
            Assert.That(sim.CheckFireRequest(Side.Blue, new Vec2(0, 0), 3), Is.EqualTo(FireCheck.Ok), "前の射撃より前に終わる");
            RunTo(sim, 600);
            Assert.That(m.State, Is.EqualTo(FireMissionState.Preparing));
            sim.Step();
            Assert.That(m.State, Is.EqualTo(FireMissionState.Firing));
        }

        // ---------------------------------------------------------------- 誤差と観測の修正

        /// <summary>初弾の誤差 100m。狙った地点を見ている味方がいれば、撃ち始めて 1分後に 30m。いなければ 100m のまま。計画射撃点は初めから 30m。</summary>
        [Test]
        public void ErrorAndObservation()
        {
            foreach (bool observer in new[] { true, false })
            {
                var sim = Flat(seed: 3);
                sim.SetArtillery(Side.Blue, 160);
                var a = Add(sim, "A", Side.Blue, -600, 0, 0);
                Add(sim, "R", Side.Red, 0, 0, 180);
                sim.Step();
                var m = sim.RequestFire(Side.Blue, new Vec2(0, 0), 3);
                if (!observer)
                {
                    // 撃ち始める前に、見ている中隊を森の陰にする
                    Paint(sim, new Vec2(-300, 0), 120, TerrainMap.Forest);
                }
                RunTo(sim, m.FirstImpactTime + 0.5);
                Assert.That(m.ErrorM, Is.EqualTo(100).Within(1e-9));
                RunTo(sim, m.FirstImpactTime + 59.5);
                Assert.That(m.ErrorM, Is.EqualTo(100).Within(1e-9), "1分の前");
                RunTo(sim, m.FirstImpactTime + 60.5);
                Assert.That(m.ErrorM, Is.EqualTo(observer ? 30 : 100).Within(1e-9), observer ? "観測の修正" : "見ていなければ直らない");
            }
            var s2 = Flat();
            s2.SetArtillery(Side.Blue, 160);
            s2.AddPlannedPoint(Side.Blue, new Vec2(0, 0));
            var pm = s2.RequestFire(Side.Blue, new Vec2(0, 0), 1);
            RunTo(s2, pm.FirstImpactTime + 0.5);
            Assert.That(pm.ErrorM, Is.EqualTo(30).Within(1e-9), "計画射撃点");
        }

        // ---------------------------------------------------------------- 弾

        /// <summary>弾は毎分 8発減る。足りなければ残りの分だけ撃つ。なくなれば要請できない。</summary>
        [Test]
        public void RoundsAreUsedAndRunOut()
        {
            var sim = Flat();
            sim.SetArtillery(Side.Blue, 12);
            sim.AddPlannedPoint(Side.Blue, new Vec2(0, 0));
            var m = sim.RequestFire(Side.Blue, new Vec2(0, 0), 1);
            RunTo(sim, m.EndTime + 1);
            Assert.That(m.State, Is.EqualTo(FireMissionState.Done));
            Assert.That(sim.RoundsLeft(Side.Blue), Is.EqualTo(4).Within(1e-6));
            var m2 = sim.RequestFire(Side.Blue, new Vec2(0, 0), 3);
            RunTo(sim, m2.FirstImpactTime + 120);
            Assert.That(m2.State, Is.EqualTo(FireMissionState.Done));
            Assert.That(m2.FiredSec, Is.EqualTo(30).Within(1e-6), "4発は 30秒分");
            Assert.That(m2.EndedTime.Value, Is.EqualTo(m2.FirstImpactTime + 30).Within(1e-6));
            Assert.That(sim.RoundsLeft(Side.Blue), Is.EqualTo(0));
            Assert.That(sim.CheckFireRequest(Side.Blue, new Vec2(0, 0), 1), Is.EqualTo(FireCheck.NoRounds));
        }

        /// <summary>取りやめはすぐ止む（弾も減らない）。伸ばしは撃っている間に 1分ずつ。次の要請と重なるなら伸ばせない。</summary>
        [Test]
        public void CancelAndExtend()
        {
            var sim = Flat();
            sim.SetArtillery(Side.Blue, 160);
            sim.AddPlannedPoint(Side.Blue, new Vec2(0, 0));
            var m = sim.RequestFire(Side.Blue, new Vec2(0, 0), 1);
            Assert.That(sim.ExtendFire(m), Is.False, "撃つ前は伸ばせない");
            RunTo(sim, m.FirstImpactTime + 30);
            Assert.That(sim.ExtendFire(m), Is.True);
            Assert.That(m.DurationSec, Is.EqualTo(120));
            double left = sim.RoundsLeft(Side.Blue);
            Assert.That(sim.CancelFire(m), Is.True);
            RunTo(sim, sim.Time + 10);
            Assert.That(m.State, Is.EqualTo(FireMissionState.Cancelled));
            Assert.That(sim.RoundsLeft(Side.Blue), Is.EqualTo(left).Within(1e-9));

            var a = sim.RequestFire(Side.Blue, new Vec2(0, 0), 1);
            var b = sim.RequestFire(Side.Blue, new Vec2(0, 0), 1, a.EndTime);
            Assert.That(b, Is.Not.Null);
            RunTo(sim, a.FirstImpactTime + 10);
            Assert.That(sim.ExtendFire(a), Is.False, "次の要請と重なる");
        }

        // ---------------------------------------------------------------- 効果

        /// <summary>中心が着弾の円（100m）に入った中隊だけが効果を受ける（敵味方を問わない）。</summary>
        [Test]
        public void OnlyCompaniesInsideTheCircle()
        {
            var sim = Flat();
            sim.SetArtillery(Side.Blue, 160);
            sim.AddPlannedPoint(Side.Blue, new Vec2(0, 0));
            var m = sim.RequestFire(Side.Blue, new Vec2(0, 0), 1);
            RunTo(sim, m.FirstImpactTime);
            // 撃ち始める刻みの直前に、着弾の中心から 95m・105m の所へ置く（中心は初弾で決まるので、置いてから1刻み進めて見る）
            sim.Step();
            var at = m.Impact;
            var inside = Add(sim, "In", Side.Blue, at.X + 95, at.Y, 0);
            var redIn = Add(sim, "RIn", Side.Red, at.X - 95, at.Y, 180);
            var outside = Add(sim, "Out", Side.Red, at.X, at.Y + 105, 180);
            sim.Step();
            Assert.That(inside.LastShelledTime, Is.EqualTo(sim.Time).Within(1e-9), "味方も受ける");
            Assert.That(redIn.LastShelledTime, Is.EqualTo(sim.Time).Within(1e-9));
            Assert.That(double.IsNegativeInfinity(outside.LastShelledTime), Is.True);
            Assert.That(inside.Ledger.Artillery, Is.LessThan(0));
            Assert.That(outside.Ledger.Artillery, Is.EqualTo(0));
        }

        /// <summary>
        /// 砲撃の被弾倍率：地形（森 0.6・村 0.5・塹壕 0.3・砲撃痕 0.9）・縦隊 1.5。開けた所を動く ×3.0 は掛けない。
        /// 塹壕は向き（R2）によらず効く。
        /// </summary>
        [Test]
        public void ArtilleryDamageMultipliers()
        {
            var p = TestUtil.Params;
            var sim = Flat();
            var c = Add(sim, "A", Side.Red, 0, 0, 180);
            Assert.That(sim.ArtilleryDamageMult(c), Is.EqualTo(1.0));
            c.MovedThisTick = true;
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(p.Terrain.MovingOpenDamageMult), "小銃には ×3.0");
            Assert.That(sim.ArtilleryDamageMult(c), Is.EqualTo(1.0), "砲撃には掛けない");
            c.MovedThisTick = false;
            foreach (char ch in new[] { TerrainMap.Forest, TerrainMap.Village, TerrainMap.Crater })
            {
                Paint(sim, c.Position, 40, ch);
                Assert.That(sim.ArtilleryDamageMult(c), Is.EqualTo(p.Terrain.DamageMult(ch)).Within(1e-12), ch.ToString());
            }
            Paint(sim, c.Position, 40, TerrainMap.Plain);
            c.Formation = Formation.Column;
            Assert.That(sim.ArtilleryDamageMult(c), Is.EqualTo(p.Formation.ColumnDamageMult).Within(1e-12), "縦隊");
            c.Formation = Formation.Line;
            // 西向きの塹壕：小銃は正面の弧の外（東）から撃たれれば守りなし。砲撃は向きによらず 0.3
            TrenchAt(sim, c.Position);
            Assert.That(sim.TerrainDamageMult(c, new Vec2(300, 0)), Is.EqualTo(p.Terrain.FortificationOffArcMult).Within(1e-12));
            Assert.That(sim.ArtilleryDamageMult(c), Is.EqualTo(p.Terrain.DamageMult(TerrainMap.Trench)).Within(1e-12));
        }

        /// <summary>
        /// 1刻みの損害の期待値は 初めの兵力×6%×倍率÷120、士気は損害の分に加えて −6×倍率÷120（20シードの平均で確かめる）。
        /// 3分で中心のマスが砲撃痕（×0.9）になる前の、3分の射撃で見る。
        /// </summary>
        [Test]
        public void LossAndMoraleRates()
        {
            var p = TestUtil.Params;
            double sum = 0, expect = 0;
            for (int s = 1; s <= Seeds; s++)
            {
                var sim = Flat(seed: (ulong)s);
                sim.SetArtillery(Side.Blue, 1000);
                sim.AddPlannedPoint(Side.Blue, new Vec2(0, 0));
                var r = Add(sim, "R", Side.Red, 0, 0, 180, Personality.Calm);
                r.Morale = 1e9; // 崩れないように
                var m = sim.RequestFire(Side.Blue, new Vec2(0, 0), 3);
                RunTo(sim, m.EndTime);
                double minutes = m.FiredSec / 60;
                sum += P(r);
                expect += r.InitialStrength * p.Artillery.LossPctPerMin / 100 * minutes;
                double expectMorale = P(r) / r.InitialStrength * 100 * p.Morale.PerPctLoss + p.Artillery.MoraleExtraPerMin * minutes;
                Assert.That(-r.Ledger.Artillery, Is.EqualTo(expectMorale).Within(1e-6));
                Assert.That(r.Ledger.Front + r.Ledger.UnderFire, Is.EqualTo(0), "射撃の要因には入れない");
            }
            Assert.That(sum / expect, Is.EqualTo(1.0).Within(0.05), "毎分、初めの兵力の 6%");
        }

        /// <summary>制圧：円の中にいる間と、出てから 60秒は制圧下（しきい値は見ない）。撃つ力 ×0.25、動こうとすれば伏せて止まる。</summary>
        [Test]
        public void SuppressionWhileShelledAndSixtySecondsAfter()
        {
            var p = TestUtil.Params;
            var sim = Flat();
            sim.SetArtillery(Side.Blue, 160);
            sim.AddPlannedPoint(Side.Blue, new Vec2(0, 0));
            var r = Add(sim, "R", Side.Red, 0, 0, 180);
            var m = sim.RequestFire(Side.Blue, new Vec2(0, 0), 1);
            RunTo(sim, m.FirstImpactTime + 0.5);
            Assert.That(r.ArtySuppressed && r.Suppressed, Is.True);
            Assert.That(r.RifleSuppressed, Is.False);
            Assert.That(sim.FireStateMult(r, true, sim.Time), Is.EqualTo(p.Suppression.FireMult).Within(1e-12));
            sim.Issue(Order.Advance(r.Id, new Vec2(-800, 0), Math.PI));
            RunTo(sim, sim.Time + 20);
            Assert.That(r.Position.X, Is.EqualTo(0).Within(1e-9), "伏せて止まる");
            RunTo(sim, m.EndTime + 59.5);
            Assert.That(r.Suppressed, Is.True, "出てから 60秒の1刻み前");
            RunTo(sim, m.EndTime + 60);
            Assert.That(r.Suppressed, Is.False, "60秒で解ける");
        }

        /// <summary>同じマスに累計3分着弾すると平地は砲撃痕（移動 ×0.6）。森は変えない。</summary>
        [Test]
        public void CratersAfterThreeMinutes()
        {
            var sim = Flat();
            sim.SetArtillery(Side.Blue, 160);
            sim.AddPlannedPoint(Side.Blue, new Vec2(25, 25));
            var m = sim.RequestFire(Side.Blue, new Vec2(25, 25), 5);
            RunTo(sim, m.FirstImpactTime + 0.5);
            var at = m.Impact;
            sim.Map.CellOf(at, out int ci, out int cj);
            // 中心のマスの隣を森にしておく
            sim.Map.SetOverlay(ci + 1, cj, TerrainMap.Forest);
            RunTo(sim, m.FirstImpactTime + 179.5);
            Assert.That(sim.Map.TerrainAt(ci, cj), Is.EqualTo(TerrainMap.Plain), "3分の1刻み前");
            sim.Step();
            Assert.That(sim.Map.TerrainAt(ci, cj), Is.EqualTo(TerrainMap.Crater));
            Assert.That(sim.Map.TerrainAt(ci + 1, cj), Is.EqualTo(TerrainMap.Forest), "森は変えない");
            Assert.That(sim.Map.MoveMult(TerrainMap.Crater), Is.EqualTo(0.6));
        }

        // ---------------------------------------------------------------- 赤の砲兵

        static Simulation RedArtyScene(Vec2 blue, Vec2? extraRed = null, Vec2? point = null)
        {
            var sim = Flat();
            sim.SetArtillery(Side.Red, 160);
            Add(sim, "R", Side.Red, 0, 0, 180);
            if (extraRed.HasValue) Add(sim, "R2", Side.Red, extraRed.Value.X, extraRed.Value.Y, 180);
            Add(sim, "B", Side.Blue, blue.X, blue.Y, 0);
            var plan = new DefensePlan { ObjectiveCenter = new Vec2(0, 0), Assembly = new Vec2(500, 0) };
            plan.Units["R"] = new DefenseUnitPlan { Role = DefenseRole.Garrison };
            plan.ArtilleryPoints.Add(point ?? new Vec2(-400, 400));
            sim.SetDefensePlan(plan);
            return sim;
        }

        /// <summary>赤の要請の順：阻止射撃（計画射撃点から 200m 以内の青）が先。準備 30秒・誤差 30m。</summary>
        [Test]
        public void RedBarrierFireFirst()
        {
            var sim = RedArtyScene(new Vec2(-400, 500));
            sim.Step();
            var m = sim.FireMissions.Single();
            Assert.That(m.Side, Is.EqualTo(Side.Red));
            Assert.That(m.Kind, Is.EqualTo(FireMissionKind.Barrier));
            Assert.That(m.Planned, Is.True);
            Assert.That(m.Aim.X, Is.EqualTo(-400));
            Assert.That(m.DurationSec, Is.EqualTo(180));
            Assert.That(m.FirstImpactTime, Is.EqualTo(0.5 + 30 + 30).Within(1e-9));
        }

        /// <summary>陣地の前：守備の赤から 600m 以内の青の位置へ（準備 90秒）。撃っている間は次を要請しない。</summary>
        [Test]
        public void RedFrontFire()
        {
            var sim = RedArtyScene(new Vec2(-500, -100));
            sim.Step();
            var m = sim.FireMissions.Single();
            Assert.That(m.Kind, Is.EqualTo(FireMissionKind.Front));
            Assert.That(m.Planned, Is.False);
            Assert.That(Vec2.Distance(m.Aim, new Vec2(-500, -100)), Is.LessThan(1e-9));
            Assert.That(m.FirstImpactTime, Is.EqualTo(0.5 + 30 + 90).Within(1e-9));
            RunTo(sim, 200);
            Assert.That(sim.FireMissions.Count, Is.EqualTo(1), "撃っている間は次を要請しない");
        }

        /// <summary>誤射を避ける：着弾の中心から 250m 以内に赤の中隊がいる地点へは撃たない。600m より遠い青も撃たない。</summary>
        [Test]
        public void RedAvoidsFriendlyFire()
        {
            var near = RedArtyScene(new Vec2(-500, -100), extraRed: new Vec2(-500, -320));
            near.Step();
            Assert.That(near.FireMissions, Is.Empty, "青のまわり 250m に赤");
            var barrier = RedArtyScene(new Vec2(-300, 450), extraRed: new Vec2(-300, 100), point: new Vec2(-300, 300));
            barrier.Step();
            Assert.That(barrier.FireMissions.Single().Kind, Is.EqualTo(FireMissionKind.Front), "計画射撃点のそばに赤がいれば阻止射撃はしない");
            var far = RedArtyScene(new Vec2(-700, -200));
            far.Step();
            Assert.That(far.FireMissions, Is.Empty, "600m より遠い");
        }

        // ---------------------------------------------------------------- 場面 S24〜S29

        /// <summary>崩れるまでの秒数（初弾から。崩れなければ +∞）。</summary>
        static double RoutAfter(Simulation sim, Company c, double firstImpact, double capSec)
        {
            while (sim.Time < capSec - 1e-9)
            {
                sim.Step();
                if (!c.UnderCommand) return sim.Time - firstImpact;
            }
            return double.PositiveInfinity;
        }

        /// <summary>S24・S25・S26：止まっている赤1個中隊（横隊）を計画射撃点として撃ち、崩れるまでの分（初弾から）。</summary>
        public static List<double> ShelledRoutMinutes(char terrain, int volleys, SimParams p = null)
        {
            var rs = new List<double>();
            for (int s = 1; s <= Seeds; s++)
            {
                var sim = Flat(p, (ulong)s);
                sim.SetArtillery(Side.Blue, 1000);
                var r = Add(sim, "R", Side.Red, 0, 0, 180);
                if (terrain == TerrainMap.Trench) TrenchAt(sim, r.Position);
                else if (terrain != TerrainMap.Plain) Paint(sim, r.Position, 300, terrain);
                sim.AddPlannedPoint(Side.Blue, r.Position);
                // 5分を volleys 回、間に 1分
                double first = 60;
                for (int k = 0; k < volleys; k++) sim.RequestFire(Side.Blue, r.Position, 5, first + k * 360);
                rs.Add(RoutAfter(sim, r, first, first + volleys * 360 + 600) / 60);
            }
            return rs;
        }

        static string Show(List<double> xs) => string.Join(" ", xs.Select(x => double.IsInfinity(x) ? "∞" : x.ToString("0.0")));

        [Test]
        public void S24_PlainShelling()
        {
            var rs = ShelledRoutMinutes(TerrainMap.Plain, 1);
            double med = TestUtil.Median(rs);
            TestContext.WriteLine($"S24 崩れるまで（分）中央値 {med:0.00}：{Show(rs)}");
            Assert.That(med, Is.InRange(4.0, 6.0));
        }

        [Test]
        public void S25_TrenchShelling()
        {
            var rs = ShelledRoutMinutes(TerrainMap.Trench, 4);
            double med = TestUtil.Median(rs);
            TestContext.WriteLine($"S25 崩れるまで（分）中央値 {med:0.00}：{Show(rs)}");
            Assert.That(med, Is.InRange(13.0, 19.0));
        }

        [Test]
        public void S26_ForestShelling()
        {
            var rs = ShelledRoutMinutes(TerrainMap.Forest, 2);
            double med = TestUtil.Median(rs);
            TestContext.WriteLine($"S26 崩れるまで（分）中央値 {med:0.00}：{Show(rs)}");
            Assert.That(med, Is.InRange(6.5, 9.5));
        }

        /// <summary>
        /// S27：平地を東へ進む赤1個中隊（猪突。縦隊か横隊）に 3分（観測あり）。青の観測の中隊は進む道から 600m 南（赤の展開の距離 500m の外）。
        /// 赤が着弾の地点に着く頃に初弾が落ちるよう、電話と準備の時間ぶん手前で要請する。損害の平均を返す。
        /// </summary>
        public static double ColumnShellingLoss(Formation f, SimParams p = null)
        {
            p = p ?? TestUtil.Params;
            var aim = new Vec2(0, 0);
            double sum = 0;
            for (int s = 1; s <= Seeds; s++)
            {
                var sim = Flat(p, (ulong)s);
                sim.SetArtillery(Side.Blue, 160);
                var r = Add(sim, "R", Side.Red, -900, 0, 0, Personality.Reckless, f);
                Add(sim, "O", Side.Blue, 0, -600, 90);
                sim.Issue(Order.Advance(r.Id, new Vec2(1400, 0), 0));
                FireMission m = null;
                Formation? atStart = null;
                double speed = p.Movement.SpeedMps * (f == Formation.Column ? p.Formation.ColumnSpeedMult : 1.0);
                double lead = p.Artillery.PhoneSec + p.Artillery.PrepSec;
                while (sim.Time < 900)
                {
                    sim.Step();
                    if (m == null && r.Position.X >= aim.X - speed * lead) m = sim.RequestFire(Side.Blue, aim, 3);
                    if (m != null && m.State == FireMissionState.Firing && !atStart.HasValue) atStart = r.Formation;
                }
                Assert.That(m, Is.Not.Null);
                Assert.That(atStart, Is.EqualTo(f), "撃ち始めた時の隊形");
                sum += P(r);
            }
            return sum / Seeds;
        }

        [Test]
        public void S27_ColumnShelling()
        {
            // 横隊の版は隊形の規則を止める（長い移動で縦隊にならないように。S21 と同じ）
            double col = ColumnShellingLoss(Formation.Column), line = ColumnShellingLoss(Formation.Line, TestUtil.WithoutF1(TestUtil.ParamsWith()));
            TestContext.WriteLine($"S27 損害の平均 縦隊 {col:0.00}・横隊 {line:0.00}（{col / line:0.00}倍）");
            Assert.That(col / line, Is.GreaterThanOrEqualTo(1.3));
        }

        /// <summary>S28 の結果（20シードの平均）。</summary>
        public sealed class S28Result
        {
            /// <summary>詰めるまで（接近戦の距離＝矩形の隙間 50m 以内に入るまで。入らなければ終わりまで）の青の損害。</summary>
            public double LossToClose;
            /// <summary>終わり（赤か青が崩れるか、砲撃が止んでから 15分）までの青の損害。</summary>
            public double LossToEnd;
            /// <summary>砲撃が止んだ時の距離、詰めた割合、赤が崩れた割合、青が崩れた割合。</summary>
            public double DistAtEnd, Closed, RedRouted, BlueRouted;
            /// <summary>赤が崩れた時刻（砲撃が止んでからの分。崩した会戦だけ）。</summary>
            public readonly List<double> RedRoutAfterMin = new List<double>();
        }

        /// <summary>
        /// S28：塹壕の赤1個中隊（西向き）を、青1個中隊が攻める（赤の位置への前進。S7 の突撃と同じ形）。地図は S7・S8 の脇を塞いだ陣地
        /// （攻める側が近づける幅は 400m、守備の線の手前 150m からは守備の正面幅だけ。両脇は水）を、赤が原点に来るようにずらしたもの。
        /// (a) 砲撃 5分の間に詰め、砲撃が止んだ時に 150m 以内にいる：青は 700m 手前から会戦の始めに前進する。砲撃は、青が 150m に着く時刻に
        ///     止む（時刻の指定で合わせる：同じシードを砲撃なしで一度回して、射程の 20m 手前（420m）に着いた時刻を測り、残りの 270m を
        ///     ふつうの速さで歩く時間を足す。赤の射程の中では赤が制圧されていて、青は伏せずに歩ける）。
        /// (b) 砲撃 5分の後に、塹壕の 500m 手前から詰め始める。
        /// 青の損害は「詰めるまで」（仕様の「止んでから詰めるまでの隙」）で比べ、終わりまでの損害も並べる。
        /// </summary>
        /// <paramref name="standoffM"/> は (a) で砲撃が止む時の距離（仕様は 150m。原因の切り分けに 130m＝着弾の円の縁も見る）。
        public static S28Result AssaultAfterShelling(bool during, SimParams p = null, double standoffM = 150)
        {
            p = p ?? TestUtil.Params;
            var r = new S28Result();
            for (int s = 1; s <= Seeds; s++)
            {
                double start = 60;
                if (during)
                {
                    var dry = AssaultOnce(p, (ulong)s, true, null);
                    start = Math.Max(60, (dry.atRange ?? 0) + (p.Fire.RangeMaxM + 20 - standoffM) / p.Movement.SpeedMps - 300);
                }
                var run = AssaultOnce(p, (ulong)s, during, start);
                r.LossToClose += run.lossToClose / Seeds;
                r.LossToEnd += run.lossToEnd / Seeds;
                r.DistAtEnd += run.distEnd / Seeds;
                if (run.closed) r.Closed += 1.0 / Seeds;
                if (run.redRouted)
                {
                    r.RedRouted += 1.0 / Seeds;
                    r.RedRoutAfterMin.Add(run.routAfterSec / 60);
                }
                if (run.blueRouted) r.BlueRouted += 1.0 / Seeds;
            }
            return r;
        }

        /// <summary>1回の S28。<paramref name="fireAt"/> が null なら砲撃なし（時刻を合わせるための下見。射程の手前に着いたら止める）。</summary>
        static (double lossToClose, double lossToEnd, double distEnd, bool closed, bool redRouted, bool blueRouted, double? atRange, double routAfterSec)
            AssaultOnce(SimParams p, ulong seed, bool during, double? fireAt)
        {
            var sim = Flat(p, seed);
            for (int j = 0; j < sim.Map.Height; j++)
                for (int i = 0; i < sim.Map.Width; i++)
                {
                    var c = sim.Map.CellCenter(i, j);
                    if (c.X >= -150 && c.X < 50 ? Math.Abs(c.Y) > 100 : Math.Abs(c.Y) > 200) sim.Map.SetOverlay(i, j, TerrainMap.River);
                }
            sim.SetArtillery(Side.Blue, 160);
            var r = Add(sim, "R", Side.Red, 0, 0, 180);
            TrenchAt(sim, r.Position);
            var b = Add(sim, "B", Side.Blue, during ? -700 : -500, 0, 0);
            sim.AddPlannedPoint(Side.Blue, r.Position);
            if (fireAt.HasValue) sim.RequestFire(Side.Blue, r.Position, 5, fireAt.Value);
            if (during) sim.Issue(Order.Advance(b.Id, r.Position, 0));
            double end = (fireAt ?? 0) + 300;
            double? atRange = null, lossToClose = null;
            double distEnd = 0;
            bool issued = during, ended = false;
            while (sim.Time < end + 900 - 1e-9 && r.UnderCommand && b.UnderCommand)
            {
                sim.Step();
                if (!atRange.HasValue && Vec2.Distance(b.Position, r.Position) <= p.Fire.RangeMaxM + 20)
                {
                    atRange = sim.Time;
                    if (!fireAt.HasValue) break;
                }
                if (!lossToClose.HasValue && sim.InCloseCombat(b, r)) lossToClose = P(b);
                if (!ended && sim.Time >= end - 1e-9)
                {
                    ended = true;
                    distEnd = Vec2.Distance(b.Position, r.Position);
                    if (!issued) sim.Issue(Order.Advance(b.Id, r.Position, 0));
                    issued = true;
                }
            }
            return (lossToClose ?? P(b), P(b), distEnd, lossToClose.HasValue, !r.UnderCommand, !b.UnderCommand, atRange, sim.Time - end);
        }

        [Test]
        public void S28_ShellingAndAdvance()
        {
            var a = AssaultAfterShelling(true);
            var b = AssaultAfterShelling(false);
            string Line(S28Result x) => $"詰めるまでの損害 {x.LossToClose:0.00}・終わりまで {x.LossToEnd:0.00}（止んだ時 {x.DistAtEnd:0}m、詰めた {x.Closed:0.00}、" +
                                        $"赤が崩れた {x.RedRouted:0.00}" +
                                        (x.RedRoutAfterMin.Count > 0 ? $"（止んでから中央値 {TestUtil.Median(x.RedRoutAfterMin):0.0}分）" : "") +
                                        $"、青が崩れた {x.BlueRouted:0.00}）";
            TestContext.WriteLine($"S28 (a) {Line(a)}");
            TestContext.WriteLine($"S28 (b) {Line(b)}");
            Assert.That(a.DistAtEnd, Is.LessThanOrEqualTo(150), "(a) 止んだ時に 150m 以内");
            Assert.That(a.LossToClose, Is.LessThanOrEqualTo(b.LossToClose / 2));
        }

        /// <summary>S29：着弾の円の中の青の中隊も、赤と同じ損害・制圧を受ける（同じ置き方で、青の砲撃を青・赤に落とした平均を比べる）。</summary>
        [Test]
        public void S29_FriendlyFire()
        {
            double Loss(Side side, out int suppressed)
            {
                double sum = 0;
                suppressed = 0;
                for (int s = 1; s <= Seeds; s++)
                {
                    var sim = Flat(seed: (ulong)s);
                    sim.SetArtillery(Side.Blue, 160);
                    var c = Add(sim, "C", side, 0, 0, side == Side.Blue ? 0 : 180);
                    sim.AddPlannedPoint(Side.Blue, c.Position);
                    var m = sim.RequestFire(Side.Blue, c.Position, 3);
                    RunTo(sim, m.FirstImpactTime + 1);
                    if (c.Suppressed) suppressed++;
                    RunTo(sim, m.EndTime);
                    sum += P(c);
                }
                return sum / Seeds;
            }
            double blue = Loss(Side.Blue, out int bs), red = Loss(Side.Red, out int rs);
            TestContext.WriteLine($"S29 損害の平均 青 {blue:0.00}・赤 {red:0.00}");
            Assert.That(bs, Is.EqualTo(Seeds), "青も制圧下");
            Assert.That(rs, Is.EqualTo(Seeds));
            Assert.That(blue, Is.EqualTo(red).Within(red * 0.1), "同じ損害");
        }

        // ---------------------------------------------------------------- 掃き出し

        /// <summary>S24〜S29 の期待（仕様の表）。満たさないものを返す。</summary>
        public static List<string> Unmet(SimParams p, StringBuilder line = null)
        {
            var u = new List<string>();
            double s24 = TestUtil.Median(ShelledRoutMinutes(TerrainMap.Plain, 1, p));
            double s25 = TestUtil.Median(ShelledRoutMinutes(TerrainMap.Trench, 4, p));
            double s26 = TestUtil.Median(ShelledRoutMinutes(TerrainMap.Forest, 2, p));
            double col = ColumnShellingLoss(Formation.Column, p);
            var noF1 = SimParams.FromJson(System.IO.File.ReadAllText(TestUtil.PathOf(TestUtil.ParamsRelPath)));
            noF1.Artillery.LossPctPerMin = p.Artillery.LossPctPerMin;
            noF1.Artillery.MoraleExtraPerMin = p.Artillery.MoraleExtraPerMin;
            noF1.Artillery.CraterAfterSec = p.Artillery.CraterAfterSec;
            double lin = ColumnShellingLoss(Formation.Line, TestUtil.WithoutF1(noF1));
            var a = AssaultAfterShelling(true, p);
            var b = AssaultAfterShelling(false, p);
            if (!(s24 >= 4 && s24 <= 6)) u.Add($"S24 {s24:0.00}");
            if (!(s25 >= 13 && s25 <= 19)) u.Add($"S25 {s25:0.00}");
            if (!(s26 >= 6.5 && s26 <= 9.5)) u.Add($"S26 {s26:0.00}");
            if (!(col / lin >= 1.3)) u.Add($"S27 {col / lin:0.00}倍");
            if (!(a.DistAtEnd <= 150 && a.LossToClose <= b.LossToClose / 2)) u.Add($"S28 (a) {a.LossToClose:0.0}・(b) {b.LossToClose:0.0}");
            line?.Append($"| {Show1(s24)} | {Show1(s25)} | {Show1(s26)} | {col / lin:0.00} | {a.LossToClose:0.0}（終わり {a.LossToEnd:0.0}、詰めた {a.Closed:0.00}）| " +
                         $"{b.LossToClose:0.0}（終わり {b.LossToEnd:0.0}、詰めた {b.Closed:0.00}）|");
            return u;
        }

        static string Show1(double x) => double.IsInfinity(x) || double.IsNaN(x) ? "∞" : x.ToString("0.00");

        /// <summary>
        /// 仕様の掃き出し：artillery.loss_pct_per_min（5.0・6.0・7.0）× artillery.morale_extra_per_min（4・6・8）。参考に、砲撃痕なし
        /// （crater_after_sec ∞）の版も並べる（S28 の原因の切り分け）。
        /// </summary>
        [Test, Explicit]
        public void Sweep()
        {
            var sb = new StringBuilder();
            sb.AppendLine("| 毎分の損害 | 士気 | 砲撃痕 | S24（分） | S25（分） | S26（分） | S27（倍） | S28 (a) 詰めるまでの損害 | S28 (b) | 満たさないもの |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (bool crater in new[] { true, false })
                foreach (double loss in new[] { 5.0, 6.0, 7.0 })
                    foreach (double mor in new[] { 4.0, 6.0, 8.0 })
                    {
                        if (!crater && !(loss == 6.0 && mor == 6.0)) continue;
                        var p = SimParams.FromJson(System.IO.File.ReadAllText(TestUtil.PathOf(TestUtil.ParamsRelPath)));
                        p.Artillery.LossPctPerMin = loss;
                        p.Artillery.MoraleExtraPerMin = mor;
                        if (!crater) p.Artillery.CraterAfterSec = double.PositiveInfinity;
                        var line = new StringBuilder();
                        var u = Unmet(p, line);
                        sb.AppendLine($"| {loss:0.0} | {mor:0} | {(crater ? "あり" : "なし")} {line} {(u.Count == 0 ? "なし" : string.Join("、", u))} |");
                    }
            var edge = AssaultAfterShelling(true, null, 130);
            sb.AppendLine($"S28 (a) を 130m（着弾の円の縁：半径 100m＋誤差 30m）で止む時刻にした版：詰めるまでの損害 {edge.LossToClose:0.0}・終わりまで {edge.LossToEnd:0.0}" +
                          $"（止んだ時 {edge.DistAtEnd:0}m、詰めた {edge.Closed:0.00}、赤が崩れた {edge.RedRouted:0.00}、青が崩れた {edge.BlueRouted:0.00}）");
            TestContext.WriteLine(sb.ToString());
            Assert.Pass();
        }

        // ---------------------------------------------------------------- 砲兵のない地図

        /// <summary>砲兵のない地図（教程1・2）は砲兵を持たない。シナリオA は両軍 160発、赤の計画射撃点3箇所。</summary>
        [Test]
        public void MapsDeclareArtillery()
        {
            foreach (var f in new[] { "tutorial_1", "tutorial_2" })
            {
                var sim = MapScenario.LoadFile(TestUtil.PathOf($"maps/{f}.json")).Build(TestUtil.Params, 1);
                Assert.That(sim.HasArtillery(Side.Blue) || sim.HasArtillery(Side.Red), Is.False, f);
            }
            foreach (int st in new[] { 1, 2 })
            {
                var sim = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{st}.json")).Build(TestUtil.Params, 1);
                Assert.That(sim.RoundsLeft(Side.Blue), Is.EqualTo(160));
                Assert.That(sim.RoundsLeft(Side.Red), Is.EqualTo(160));
                Assert.That(sim.BatteryOf(Side.Red).PlannedPoints.Count, Is.EqualTo(3));
                Assert.That(sim.BatteryOf(Side.Blue).PlannedPoints, Is.Empty);
                var off = MapScenario.LoadFile(TestUtil.PathOf($"maps/scenario_a{st}.json")).Build(TestUtil.WithoutP3(TestUtil.ParamsWith()), 1);
                Assert.That(off.HasArtillery(Side.Red), Is.False, "artillery.enabled false");
            }
        }
    }
}
