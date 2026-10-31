using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Battalion.Sim.Tests
{
    /// <summary>F1：隊形（仕様「F1：隊形（縦隊と横隊、展開と縮合）（確定）」）の単体テストと、場面 S20〜S22。</summary>
    public class F1Tests
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

        static void Advance(Simulation sim, Company c, double x, double y) =>
            sim.Issue(Order.Advance(c.Id, new Vec2(x, y), 0));

        static SimParams NoF1 => TestUtil.WithoutF1(TestUtil.ParamsWith());

        // ---------------------------------------------------------------- 形と倍率

        /// <summary>縦隊の矩形は正面 50m×奥行き 200m（向きの方向に長い）。横隊は正面 200m×奥行き 50m。</summary>
        [Test]
        public void ColumnRectIsNarrowAndLong()
        {
            var sim = Flat();
            var c = Add(sim, "A", Side.Blue, 0, 0, 0);
            var line = sim.RectOf(c);
            Assert.That(line.A.Y - line.B.Y, Is.EqualTo(200).Within(1e-9), "横隊の正面 200m（向きに直交）");
            c.Formation = Formation.Column;
            var col = sim.RectOf(c);
            Assert.That(col.A.Y - col.B.Y, Is.EqualTo(50).Within(1e-9), "縦隊の正面 50m");
            Assert.That(col.A.X - col.D.X, Is.EqualTo(200).Within(1e-9), "縦隊の奥行き 200m（向きの方向）");
        }

        /// <summary>縦隊の撃つ力 ×0.2・被弾 ×1.5・速さ ×1.25。移り変わる間は撃つ力 ×0.3、開けた所なら被弾 ×3.0（動いている扱い）、制圧のしきい値は動いている時の値。</summary>
        [Test]
        public void ColumnAndChangeMultipliers()
        {
            var p = TestUtil.Params;
            var sim = Flat();
            var c = Add(sim, "A", Side.Blue, 0, 0, 0);
            Add(sim, "E", Side.Red, 0, 2000, -90);
            double fireLine = sim.FireStateMult(c, true, 0);
            double dmgLine = sim.TerrainDamageMult(c);
            c.Formation = Formation.Column;
            Assert.That(sim.FireStateMult(c, true, 0), Is.EqualTo(fireLine * p.Formation.ColumnFireMult).Within(1e-12));
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(dmgLine * p.Formation.ColumnDamageMult).Within(1e-12));
            var move = new List<ModifierFactor>();
            c.MovedThisTick = true;
            sim.MoveFactors(c, move);
            Assert.That(move.Select(f => f.Kind), Does.Contain(ModifierKind.MoveColumn));
            c.MovedThisTick = false;

            // 展開中：矩形はまだ縦隊（被弾 ×1.5）、動いている扱い（開けた所 ×3.0）、撃つ力は ×0.3（×0.2 の代わり）
            c.FormationChange = FormationChange.Deploying;
            Assert.That(sim.FireStateMult(c, true, 0), Is.EqualTo(fireLine * p.Formation.ChangeFireMult).Within(1e-12));
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(dmgLine * p.Formation.ColumnDamageMult * p.Terrain.MovingOpenDamageMult).Within(1e-12));
            Assert.That(sim.SuppressionThreshold(c), Is.EqualTo(p.Suppression.ThresholdMovingPerMin).Within(1e-12));
            var dmg = new List<ModifierFactor>();
            Assert.That(sim.DamageFactors(c, null, dmg), Is.EqualTo(sim.TerrainDamageMult(c)).Within(1e-12));
            Assert.That(dmg.Select(f => f.Kind), Is.EquivalentTo(new[] { ModifierKind.Deploying, ModifierKind.Column }));

            // 縮合中：矩形はまだ横隊
            c.Formation = Formation.Line;
            c.FormationChange = FormationChange.FormingColumn;
            Assert.That(sim.TerrainDamageMult(c), Is.EqualTo(dmgLine * p.Terrain.MovingOpenDamageMult).Within(1e-12));
            Assert.That(sim.FireStateMult(c, true, 0), Is.EqualTo(fireLine * p.Formation.ChangeFireMult).Within(1e-12));
        }

        // ---------------------------------------------------------------- 中隊長の判断

        /// <summary>長い前進：条件が 60秒続いたら縮合を始め（速さ ×0.5 で進み続ける）、120秒で縦隊になる。短い前進（800m 未満）では縮合しない。</summary>
        [Test]
        public void LongAdvanceFormsColumnAfterQuietSeconds()
        {
            var p = TestUtil.Params;
            var sim = Flat();
            var c = Add(sim, "A", Side.Blue, -1200, 0, 0);
            Advance(sim, c, 1200, 0);
            double? start = null, formed = null;
            Vec2 prev = c.Position;
            for (int k = 0; k < 2 * 600 && !formed.HasValue; k++)
            {
                sim.Step();
                if (!start.HasValue && c.FormationChange == FormationChange.FormingColumn) start = sim.Time;
                if (start.HasValue && c.FormationChange == FormationChange.FormingColumn && sim.Time > start.Value + 1)
                    Assert.That(Vec2.Distance(prev, c.Position), Is.EqualTo(p.Movement.SpeedMps * p.Formation.ChangeSpeedMult * p.TickSec).Within(1e-6),
                        "縮合の間は速さ ×0.5 で進み続ける");
                prev = c.Position;
                if (c.Formation == Formation.Column) formed = sim.Time;
            }
            Assert.That(start, Is.Not.Null);
            // 練度の遅れの後に動き始め、60秒続いたら始める
            Assert.That(start.Value, Is.GreaterThanOrEqualTo(p.Formation.ColumnQuietSec - 1e-9));
            Assert.That(formed.Value - start.Value, Is.EqualTo(p.Formation.FormColumnSec).Within(p.TickSec + 1e-9));

            var sim2 = Flat();
            var b = Add(sim2, "B", Side.Blue, 0, 0, 0);
            Advance(sim2, b, 700, 0);
            for (int k = 0; k < 2 * 800; k++) sim2.Step();
            Assert.That(b.Arrived, Is.True);
            Assert.That(b.Formation, Is.EqualTo(Formation.Line));
            Assert.That(b.FormationChange, Is.EqualTo(FormationChange.None), "短い前進では縮合しない");
        }

        /// <summary>縦隊は、目的地まで 300m 以内に来たら展開する（進みながら 90秒）。展開を終えたら横隊で、線を張っている扱い。</summary>
        [Test]
        public void ColumnDeploysNearTheGoal()
        {
            var p = TestUtil.Params;
            var sim = Flat();
            var c = Add(sim, "A", Side.Blue, 0, 0, 0, formation: Formation.Column);
            Advance(sim, c, 800, 0);
            double? start = null;
            for (int k = 0; k < 2 * 1200; k++)
            {
                sim.Step();
                if (!start.HasValue && c.FormationChange == FormationChange.Deploying)
                {
                    start = sim.Time;
                    Assert.That(800 - c.Position.X, Is.LessThanOrEqualTo(p.Formation.DeployBeforeDestM + 1e-6));
                    Assert.That(800 - c.Position.X, Is.GreaterThan(p.Formation.DeployBeforeDestM - 2));
                }
                if (start.HasValue && c.Formation == Formation.Line && c.FormationChange == FormationChange.None)
                {
                    Assert.That(sim.Time - start.Value, Is.EqualTo(p.Formation.DeploySec).Within(p.TickSec + 1e-9));
                    Assert.That(c.StillSec, Is.GreaterThanOrEqualTo(p.Movement.LineFormedStillSec - 1e-9), "線を張っている扱い");
                    break;
                }
            }
            Assert.That(start, Is.Not.Null);
            Assert.That(c.Formation, Is.EqualTo(Formation.Line));
        }

        /// <summary>縦隊で撃たれたら、その場ですぐ展開を始める。縮合の途中で撃たれたら、やめて横隊のまま。</summary>
        [Test]
        public void FiredOnColumnDeploysAtOnceAndFormingIsCancelled()
        {
            var sim = Flat();
            var c = Add(sim, "A", Side.Blue, 0, 0, 0, formation: Formation.Column);
            Advance(sim, c, 1200, 0);
            sim.Step();
            Assert.That(c.FormationChange, Is.EqualTo(FormationChange.None));
            c.LastTargetedTime = sim.Time + sim.Params.TickSec; // 次の刻みに撃たれた
            sim.Step();
            Assert.That(c.FormationChange, Is.EqualTo(FormationChange.Deploying));

            var sim2 = Flat();
            var b = Add(sim2, "B", Side.Blue, 0, 0, 0);
            Advance(sim2, b, 1500, 0);
            for (int k = 0; k < 2 * 300 && b.FormationChange != FormationChange.FormingColumn; k++) sim2.Step();
            Assert.That(b.FormationChange, Is.EqualTo(FormationChange.FormingColumn));
            b.LastTargetedTime = sim2.Time + sim2.Params.TickSec;
            sim2.Step();
            Assert.That(b.FormationChange, Is.EqualTo(FormationChange.None));
            Assert.That(b.Formation, Is.EqualTo(Formation.Line));
        }

        /// <summary>壊走は隊形を持たない（横隊に戻る）。F1 を止めると隊形を変えない。</summary>
        [Test]
        public void RoutedCompanyHasNoColumnAndF1CanBeSwitchedOff()
        {
            var sim = Flat();
            var c = Add(sim, "A", Side.Blue, 0, 0, 0, formation: Formation.Column);
            c.State = CompanyState.Routed;
            sim.Step();
            Assert.That(c.Formation, Is.EqualTo(Formation.Line));

            var off = Flat(NoF1);
            var b = Add(off, "B", Side.Blue, -1200, 0, 0);
            Advance(off, b, 1200, 0);
            for (int k = 0; k < 2 * 600; k++) off.Step();
            Assert.That(b.Formation, Is.EqualTo(Formation.Line));
            Assert.That(b.FormationChange, Is.EqualTo(FormationChange.None));
        }

        /// <summary>地図ファイルの配置の formation を読む（書かなければ横隊）。</summary>
        [Test]
        public void MapDeployReadsFormation()
        {
            var sc = MapScenario.LoadFile(TestUtil.PathOf(T1Tests.TutorialPath));
            Assert.That(sc.Units.All(u => u.Formation == Formation.Line), Is.True);
            sc.Units[0].Formation = Formation.Column;
            var sim = sc.Build(TestUtil.Params, 1);
            Assert.That(sim.Find(sc.Units[0].Id).Formation, Is.EqualTo(Formation.Column));
        }

        // ---------------------------------------------------------------- 場面 S20〜S22

        public sealed class S20Result
        {
            public double LineSec, ColumnSec;
            public double Ratio => ColumnSec / LineSec;
        }

        /// <summary>S20 行軍：1個中隊が 2.4km 前進（敵なし）。横隊のまま（F1 なし）と、隊形あり（縮合・縦隊・展開）の所要時間。</summary>
        public static S20Result S20(SimParams withF1)
        {
            double Run(SimParams p)
            {
                var sim = Flat(p);
                var c = Add(sim, "A", Side.Blue, -1200, 0, 0);
                Advance(sim, c, 1200, 0);
                for (int k = 0; k < 2 * 3600; k++)
                {
                    sim.Step();
                    if (c.Arrived && c.FormationChange == FormationChange.None && c.Formation == Formation.Line) break;
                }
                return sim.Time;
            }
            return new S20Result { LineSec = Run(NoF1), ColumnSec = Run(withF1) };
        }

        public sealed class S21Result
        {
            public double ColumnLoss, LineLoss;
            public int ColumnHit, LineHit;
            public double Ratio => LineLoss > 0 ? ColumnLoss / LineLoss : double.PositiveInfinity;
        }

        /// <summary>
        /// S21 不意に撃たれる：縦隊で東へ進む1個中隊（y 0）を、森の縁（y 325）の赤1個中隊が撃つ。赤の前に奥行き 200m の森の帯（y 100〜300）を置き、
        /// 赤の正面だけ幅 50m 空けるので、青から赤が見える（赤が撃てる）のはほぼ真横に来た時（約 330m）だけ。青の道の南（y −50 より南）は
        /// 通れない水にして、赤の射界を避けて遠回りできないようにする。
        /// 撃たれてから5分の青の損害（兵の減り）の平均を、同じ場面で横隊で進んだ時（F1 なし）と比べる。
        /// </summary>
        public static S21Result S21(SimParams withF1, int seeds = Seeds)
        {
            (double loss, int hit) Run(SimParams p, bool column)
            {
                double sum = 0;
                int hits = 0;
                for (ulong seed = 1; seed <= (ulong)seeds; seed++)
                {
                    var sim = Flat(p, seed);
                    for (int i = 0; i < sim.Map.Width; i++)
                        for (int j = 0; j < sim.Map.Height; j++)
                        {
                            var q = sim.Map.CellCenter(i, j);
                            bool band = q.Y > 100 && q.Y < 300 && !(q.X > 0 && q.X < 50);
                            bool wood = q.Y > 300 && q.Y < 600;
                            if (band || wood) sim.Map.SetOverlay(i, j, TerrainMap.Forest);
                            if (q.Y < -50) sim.Map.SetOverlay(i, j, TerrainMap.River);
                        }
                    var b = Add(sim, "B", Side.Blue, -1000, 0, 0, formation: column ? Formation.Column : Formation.Line);
                    Add(sim, "R", Side.Red, 25, 325, -90);
                    Advance(sim, b, 1400, 0);
                    double? hitAt = null;
                    int s0 = b.Strength;
                    for (int k = 0; k < 2 * 3600; k++)
                    {
                        sim.Step();
                        if (!hitAt.HasValue && b.LastTargetedTime == sim.Time)
                        {
                            hitAt = sim.Time;
                            s0 = b.Strength;
                        }
                        if (hitAt.HasValue && sim.Time >= hitAt.Value + 300 - 1e-9) break;
                    }
                    if (!hitAt.HasValue) continue;
                    hits++;
                    sum += s0 - b.Strength;
                }
                return (hits > 0 ? sum / hits : 0, hits);
            }
            var col = Run(withF1, true);
            var line = Run(NoF1, false);
            return new S21Result { ColumnLoss = col.loss, ColumnHit = col.hit, LineLoss = line.loss, LineHit = line.hit };
        }

        public sealed class S22Result
        {
            public int Started, FinishedOnTime, FacingRed;
            public double StartDistanceMax, StartDistanceMin = double.MaxValue;
        }

        /// <summary>S22 展開：縦隊の冷静な1個中隊が東へ進み、(1100, 300) の赤に 800m で気付く。展開を始め、deploy_sec（90秒）で終わり、赤の方を向いた横隊か。</summary>
        public static S22Result S22(SimParams p, int seeds = Seeds)
        {
            var r = new S22Result();
            for (ulong seed = 1; seed <= (ulong)seeds; seed++)
            {
                var sim = Flat(p, seed);
                var b = Add(sim, "B", Side.Blue, -400, 0, 0, formation: Formation.Column);
                var red = Add(sim, "R", Side.Red, 1100, 300, 180);
                Advance(sim, b, 1400, 0);
                double? start = null;
                for (int k = 0; k < 2 * 1800; k++)
                {
                    sim.Step();
                    if (!start.HasValue && b.FormationChange == FormationChange.Deploying)
                    {
                        start = sim.Time;
                        r.Started++;
                        double d = Vec2.Distance(b.Position, red.Position);
                        r.StartDistanceMax = Math.Max(r.StartDistanceMax, d);
                        r.StartDistanceMin = Math.Min(r.StartDistanceMin, d);
                    }
                    if (start.HasValue && b.FormationChange == FormationChange.None)
                    {
                        if (Math.Abs(sim.Time - start.Value - p.Formation.DeploySec) <= p.TickSec + 1e-9 && b.Formation == Formation.Line) r.FinishedOnTime++;
                        var to = red.Position - b.Position;
                        double diff = Math.Abs(Angles.Wrap(Math.Atan2(to.Y, to.X) - b.Facing)) * Angles.RadToDeg;
                        if (diff <= 1.0) r.FacingRed++;
                        break;
                    }
                }
            }
            return r;
        }

        /// <summary>S20〜S22 の期待（仕様の表）。満たさないものを返す。</summary>
        public static List<string> Unmet(S20Result s20, S21Result s21, S22Result s22, int seeds = Seeds)
        {
            var u = new List<string>();
            if (s20.Ratio > 0.90 + 1e-9) u.Add($"S20 縦隊の所要時間 {s20.ColumnSec:0}秒 が横隊 {s20.LineSec:0}秒 の 90% 以下でない（{s20.Ratio:0.000}）");
            if (s21.ColumnHit < seeds || s21.LineHit < seeds) u.Add($"S21 撃たれなかった会戦がある（縦隊 {s21.ColumnHit}・横隊 {s21.LineHit}）");
            if (s21.Ratio < 2.0 - 1e-9) u.Add($"S21 撃たれてから5分の損害 縦隊 {s21.ColumnLoss:0.0} が横隊 {s21.LineLoss:0.0} の2倍以上でない（{s21.Ratio:0.00}倍）");
            if (s22.Started < seeds || s22.FinishedOnTime < seeds || s22.FacingRed < seeds)
                u.Add($"S22 展開を始めた {s22.Started}・deploy_sec で終えた {s22.FinishedOnTime}・赤を向いた {s22.FacingRed}（/{seeds}）");
            if (s22.Started > 0 && (s22.StartDistanceMax > 800 + 1e-6 || s22.StartDistanceMin < 790))
                u.Add($"S22 展開を始めた距離 {s22.StartDistanceMin:0}〜{s22.StartDistanceMax:0}m が 800m 前後でない");
            return u;
        }

        static string Text(S20Result s20, S21Result s21, S22Result s22) =>
            $"S20 横隊 {s20.LineSec:0}秒・縦隊 {s20.ColumnSec:0}秒（{s20.Ratio:0.000}）／" +
            $"S21 撃たれてから5分の損害 縦隊 {s21.ColumnLoss:0.0}人・横隊 {s21.LineLoss:0.0}人（{s21.Ratio:0.00}倍、撃たれた {s21.ColumnHit}・{s21.LineHit}）／" +
            $"S22 展開を始めた {s22.Started}・deploy_sec で終えた {s22.FinishedOnTime}・赤を向いた {s22.FacingRed}、始めた距離 {s22.StartDistanceMin:0}〜{s22.StartDistanceMax:0}m";

        [Test]
        public void ScenesS20ToS22MeetTheExpectations()
        {
            var p = TestUtil.Params;
            var s20 = S20(p);
            var s21 = S21(p);
            var s22 = S22(p);
            TestContext.WriteLine(Text(s20, s21, s22));
            Assert.That(Unmet(s20, s21, s22), Is.Empty);
        }

        /// <summary>期待を外れたときの掃き出し：deploy_sec（60・90・120）×column_speed_mult（1.25・1.4）。ふだんは回さない。</summary>
        [Test, Explicit("F1 の掃き出し")]
        public void Sweep()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("| deploy_sec | column_speed_mult | S20 | S21 | S22 | 満たさない |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (double dep in new[] { 60.0, 90.0, 120.0 })
                foreach (double dm in new[] { 1.25, 1.4 })
                {
                    var p = TestUtil.ParamsWith();
                    p.Formation.DeploySec = dep;
                    p.Formation.ColumnSpeedMult = dm;
                    var s20 = S20(p);
                    var s21 = S21(p);
                    var s22 = S22(p);
                    var u = Unmet(s20, s21, s22);
                    sb.AppendLine($"| {dep:0} | {dm:0.00} | {s20.ColumnSec:0}／{s20.LineSec:0}秒（{s20.Ratio:0.000}） | {s21.ColumnLoss:0.0}／{s21.LineLoss:0.0}人（{s21.Ratio:0.00}倍） | " +
                                  $"{s22.Started}・{s22.FinishedOnTime}・{s22.FacingRed} | {(u.Count == 0 ? "なし" : string.Join("／", u))} |");
                }
            TestContext.WriteLine(sb.ToString());
        }
    }
}
